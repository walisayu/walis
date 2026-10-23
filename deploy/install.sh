#!/usr/bin/env bash
# Installs Walis as a macOS service (LaunchDaemon). Needs sudo.
#
#   sudo ./deploy/install.sh [--port 11120] [--user <account>] [--prefix /usr/local/walis] [--trusted-proxies 127.0.0.1,::1]
#
# Add --no-build to skip the build, keep the existing current and only re-register the daemons.
#
# Two daemons are installed:
#   com.walisayu.walis            the service itself, running as the given account (not root).
#   com.walisayu.walis-restarter  watches the trigger file, promotes a staged release and restarts the service on its behalf.
# An installation registered under the earlier com.spex.* labels is unloaded and removed on the way.
#
# Ownership: $PREFIX, $PREFIX/releases and $PREFIX/current belong to root (755), so a build script running as the service
# account cannot replace the executable or wwwroot the service restarts into. Only $PREFIX/data and $PREFIX/var belong to the
# service account; builds land in $PREFIX/var/staging and are promoted by root (this script, or the restarter after the
# self-update job). An installation from before this layout is converted in place.
set -euo pipefail
umask 077
export LC_ALL=C
export PATH="/usr/bin:/bin:/usr/sbin:/sbin"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

LABEL="com.walisayu.walis"
RESTART_LABEL="com.walisayu.walis-restarter"
# Labels used before 2026-10-20; still unloaded and cleaned up so an upgrade does not leave two services behind.
OLD_LABEL="com.spex.walis"
OLD_RESTART_LABEL="com.spex.walis-restarter"
PREFIX="${WALIS_PREFIX-/usr/local/walis}"
PORT="${WALIS_PORT:-11120}"
SERVICE_USER="${WALIS_USER:-${SUDO_USER:-}}"
ALLOWED_HOSTS="${WALIS_ALLOWED_HOSTS:-localhost;127.0.0.1;walis.localhost}"
BOOTSTRAP_TOKEN="${WALIS_BOOTSTRAP_TOKEN:-}"
# The server no longer trusts loopback as a proxy by default (a build on this machine could otherwise forge X-Forwarded-For).
# This layout puts Caddy on the same machine in front of Walis, so the loopback addresses are listed explicitly; pass
# --trusted-proxies "" when nothing sits in front of the service.
TRUSTED_PROXIES="${WALIS_TRUSTED_PROXIES-127.0.0.1,::1}"
HELPER_DIR="/Library/PrivilegedHelperTools"
HELPER="$HELPER_DIR/com.walisayu.walis-apply-restart"
OLD_HELPER="$HELPER_DIR/com.spex.walis-apply-restart"
LEGACY_HELPER="/usr/local/libexec/walis-apply-restart.sh"
DAEMONS="/Library/LaunchDaemons"
RESTART_STATE="/var/db/walis-restarter"
BUILD=1
RELEASES_DIR=""

die() {
    echo "$1" >&2
    exit 1
}

# macOS does not guarantee realpath(1). Walk to the existing parent directory segment by segment, resolve its
# symlinks with pwd -P, then reattach the not-yet-created tail; also reject broad paths that could let root operations damage the system.
canonicalize_prefix() {
    candidate="$1"

    [ -n "$candidate" ] || die "安裝路徑不可為空白。"
    case "$candidate" in
        /*) ;;
        *) die "安裝路徑必須是絕對路徑：$candidate" ;;
    esac
    case "$candidate" in
        *[!A-Za-z0-9._/-]*) die "安裝路徑只能包含 ASCII 英數字、點、底線、連字號與斜線。" ;;
        *//*|*/./*|*/../*|*/.|*/..) die "安裝路徑不可包含重複斜線、. 或 ..：$candidate" ;;
    esac

    while [ "$candidate" != "/" ] && [ "${candidate%/}" != "$candidate" ]; do
        candidate="${candidate%/}"
    done

    [ "$candidate" != "/" ] || die "拒絕把檔案系統根目錄當成 Walis 安裝路徑。"
    [ ! -L "$candidate" ] || die "安裝路徑本身不可為符號連結：$candidate"

    probe="$candidate"
    suffix=""
    while [ ! -e "$probe" ]; do
        suffix="/$(basename "$probe")$suffix"
        parent="$(dirname "$probe")"
        [ "$parent" != "$probe" ] || die "無法解析安裝路徑：$candidate"
        probe="$parent"
    done

    [ -d "$probe" ] || die "安裝路徑的既有父項目不是目錄：$probe"
    physical="$(cd "$probe" 2>/dev/null && pwd -P)" || die "無法解析安裝路徑：$candidate"
    canonical="${physical%/}${suffix}"

    case "$canonical" in
        *[!A-Za-z0-9._/-]*) die "解析後的安裝路徑含有不允許的字元：$canonical" ;;
    esac

    case "$canonical" in
        /|/usr|/usr/local|/opt|/var|/private|/private/var|/tmp|/private/tmp|/Users|/Library|/System|/Applications|/Volumes)
            die "安裝路徑太寬廣，拒絕對它執行 root 操作：$canonical"
            ;;
    esac

    [ "$(dirname "$canonical")" != "/" ] || die "安裝路徑至少要位於兩層目錄之下：$canonical"
    case "$(basename "$canonical")" in
        *[Ww][Aa][Ll][Ii][Ss]*) ;;
        *) die "安裝路徑最後一層必須包含 walis，以避免選錯操作目標：$canonical" ;;
    esac

    printf '%s\n' "$canonical"
}

xml_escape() {
    printf '%s' "$1" | sed \
        -e 's/&/\&amp;/g' \
        -e 's/</\&lt;/g' \
        -e 's/>/\&gt;/g' \
        -e 's/"/\&quot;/g' \
        -e "s/'/\&apos;/g"
}

stop_daemon() {
    local daemon_label="$1"
    local waited=0

    launchctl bootout "system/$daemon_label" 2>/dev/null || true

    while launchctl print "system/$daemon_label" >/dev/null 2>&1; do
        if [ "$waited" -ge 15 ]; then
            echo "等不到 $daemon_label 卸載完成。" >&2
            return 1
        fi
        sleep 1
        waited=$((waited + 1))
    done
}

validate_root_only_ancestry() {
    local directory="$1"
    local owner mode symbolic_mode

    while :; do
        [ -d "$directory" ] && [ ! -L "$directory" ] ||
            die "root 操作的祖先必須是既存的實體目錄：$directory"
        owner="$(stat -f %u "$directory")"
        mode="$(stat -f %OLp "$directory")"
        symbolic_mode="$(stat -f %Sp "$directory")"
        [ "$owner" = "0" ] || die "root 操作的祖先不是 root 擁有：$directory"
        case "$mode" in
            ''|*[!0-7]*) die "無法驗證 root 操作祖先的權限：$directory" ;;
        esac
        [ $((8#$mode & 0022)) -eq 0 ] || die "root 操作的祖先可由 group/other 寫入：$directory"
        case "$symbolic_mode" in
            *+*) die "root 操作的祖先含 ACL，拒絕繼續：$directory" ;;
        esac

        [ "$directory" = "/" ] && break
        directory="$(dirname "$directory")"
    done
}

while [ $# -gt 0 ]; do
    case "$1" in
        --port)
            [ $# -ge 2 ] || die "--port 缺少數值。"
            PORT="$2"; shift 2
            ;;
        --user)
            [ $# -ge 2 ] || die "--user 缺少帳號。"
            SERVICE_USER="$2"; shift 2
            ;;
        --prefix)
            [ $# -ge 2 ] || die "--prefix 缺少路徑。"
            PREFIX="$2"; shift 2
            ;;
        --trusted-proxies)
            [ $# -ge 2 ] || die "--trusted-proxies 缺少位址（不經代理時給空字串）。"
            TRUSTED_PROXIES="$2"; shift 2
            ;;
        --no-build) BUILD=0; shift ;;
        -h|--help) sed -n '2,10p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "未知的參數：$1" >&2; exit 1 ;;
    esac
done

case "$PORT" in
    ''|*[!0-9]*) die "連接埠必須是 1 到 65535 的十進位整數：$PORT" ;;
esac
[ "${#PORT}" -le 5 ] || die "連接埠必須是 1 到 65535 的十進位整數：$PORT"
PORT=$((10#$PORT))
[ "$PORT" -ge 1 ] && [ "$PORT" -le 65535 ] || die "連接埠必須介於 1 到 65535：$PORT"

PREFIX="$(canonicalize_prefix "$PREFIX")"
RELEASES_DIR="$PREFIX/releases"

if [ "$(id -u)" -ne 0 ]; then
    echo "請用 sudo 執行：sudo $0 $*" >&2
    exit 1
fi

if [ -z "$SERVICE_USER" ] || [ "$SERVICE_USER" = "root" ]; then
    echo "請用 --user 指定執行服務的帳號（Walis 會執行任意 shell 腳本，不要用 root）。" >&2
    exit 1
fi

if [[ "$ALLOWED_HOSTS" == *"*"* ]] || [[ "$ALLOWED_HOSTS" =~ [[:space:]] ]]; then
    echo "WALIS_ALLOWED_HOSTS 必須列出明確主機名稱，不可使用萬用 * 或空白字元。" >&2
    exit 1
fi

if [ -n "$BOOTSTRAP_TOKEN" ]; then
    if [ "${#BOOTSTRAP_TOKEN}" -lt 32 ] || [[ "$BOOTSTRAP_TOKEN" == *[!A-Za-z0-9_-]* ]]; then
        echo "WALIS_BOOTSTRAP_TOKEN 至少 32 碼，且只能使用英數、底線與連字號。" >&2
        exit 1
    fi
fi

id "$SERVICE_USER" >/dev/null 2>&1 || { echo "帳號不存在：$SERVICE_USER" >&2; exit 1; }
SERVICE_UID="$(id -u "$SERVICE_USER")"
[ "$SERVICE_UID" -ne 0 ] || die "服務帳號的 UID 不可為 0。"
SERVICE_GROUP="$(id -gn "$SERVICE_USER")"
SERVICE_HOME="$(dscl . -read "/Users/$SERVICE_USER" NFSHomeDirectory | awk '{print $2}')"

case "$SERVICE_HOME" in
    /*) ;;
    *) die "服務帳號的家目錄不是有效的絕對路徑：$SERVICE_HOME" ;;
esac

XML_PREFIX="$(xml_escape "$PREFIX")"
XML_PORT="$(xml_escape "$PORT")"
XML_SERVICE_USER="$(xml_escape "$SERVICE_USER")"
XML_SERVICE_GROUP="$(xml_escape "$SERVICE_GROUP")"
XML_SERVICE_HOME="$(xml_escape "$SERVICE_HOME")"
XML_ALLOWED_HOSTS="$(xml_escape "$ALLOWED_HOSTS")"
case "$TRUSTED_PROXIES" in
    *[!0-9A-Fa-f.:,]*) die "--trusted-proxies 只能是以逗號分隔的 IP 位址：$TRUSTED_PROXIES" ;;
esac
XML_TRUSTED_PROXIES="$(xml_escape "$TRUSTED_PROXIES")"
XML_BOOTSTRAP_TOKEN="$(xml_escape "$BOOTSTRAP_TOKEN")"
XML_HELPER="$(xml_escape "$HELPER")"
XML_RESTART_STATE="$(xml_escape "$RESTART_STATE")"

echo "==> 暫停舊版 root restarter"
stop_daemon "$RESTART_LABEL" || exit 1
# The old labels are not re-registered below, so they have to go now, plist included, or launchd keeps running both copies.
if launchctl print "system/$OLD_RESTART_LABEL" >/dev/null 2>&1 || launchctl print "system/$OLD_LABEL" >/dev/null 2>&1 ||
    [ -e "$DAEMONS/$OLD_LABEL.plist" ] || [ -e "$DAEMONS/$OLD_RESTART_LABEL.plist" ]; then
    echo "==> 移除舊標籤 ${OLD_LABEL}、${OLD_RESTART_LABEL}"
    stop_daemon "$OLD_RESTART_LABEL" || exit 1
    stop_daemon "$OLD_LABEL" || exit 1
    validate_root_only_ancestry "$DAEMONS"
    rm -f "$DAEMONS/$OLD_LABEL.plist" "$DAEMONS/$OLD_RESTART_LABEL.plist"
fi

echo "==> 服務帳號 $SERVICE_USER:${SERVICE_GROUP}（家目錄 ${SERVICE_HOME}）"
echo "==> 安裝位置 ${PREFIX}，連接埠 $PORT"

for directory in "$PREFIX" "$PREFIX/releases" "$PREFIX/data" "$PREFIX/var" "$PREFIX/var/log" "$PREFIX/var/staging"; do
    [ ! -L "$directory" ] || die "安裝目錄不可為符號連結：$directory"
done

# PREFIX itself stays root-owned: root creates the service-owned data/ and var/ directly under it, and because the service
# account cannot rename anything at that level, root never follows a path the service account could have swapped. Root never
# descends into data/ or var/. An older installation that handed PREFIX to the service account is taken back here; its parent must
# already be root-only, otherwise another account could swap PREFIX under us.
prefix_parent="$(dirname "$PREFIX")"
validate_root_only_ancestry "$prefix_parent"

if [ -e "$PREFIX" ]; then
    [ -d "$PREFIX" ] || die "安裝路徑已存在但不是目錄：$PREFIX"
    prefix_uid="$(stat -f %u "$PREFIX")"
    if [ "$prefix_uid" != "0" ]; then
        [ "$prefix_uid" = "$SERVICE_UID" ] ||
            die "安裝路徑既不是 root 也不是服務帳號 $SERVICE_USER 擁有；目前 UID 為 ${prefix_uid}：$PREFIX"
        echo "==> 把安裝路徑的擁有者從服務帳號改回 root：$PREFIX"
        chown root:wheel "$PREFIX"
    fi
    chmod -N "$PREFIX"
    chmod 755 "$PREFIX"
else
    install -d -o root -g wheel -m 755 "$PREFIX"
fi
validate_root_only_ancestry "$PREFIX"

# releases/ and the releases inside it: root-owned and read-only for everyone else. Releases built by an older install.sh belong
# to the service account and are converted; one that contains a symbolic link is not trusted (the current one aborts the install,
# an old one is simply removed).
if [ -e "$RELEASES_DIR" ]; then
    [ -d "$RELEASES_DIR" ] || die "releases 已存在但不是目錄：$RELEASES_DIR"
    chown root:wheel "$RELEASES_DIR"
    chmod -N "$RELEASES_DIR"
    chmod 755 "$RELEASES_DIR"
else
    install -d -o root -g wheel -m 755 "$RELEASES_DIR"
fi
validate_root_only_ancestry "$RELEASES_DIR"

CURRENT_TARGET="$(readlink "$PREFIX/current" 2>/dev/null || true)"
while IFS= read -r release_dir; do
    [ -n "$release_dir" ] || continue
    [ ! -L "$release_dir" ] || { rm -f "$release_dir"; continue; }
    [ -d "$release_dir" ] || continue
    if [ -n "$(find "$release_dir" -type l -print -quit)" ]; then
        if [ "$release_dir" = "$CURRENT_TARGET" ]; then
            die "目前使用中的版本含有符號連結，拒絕信任：${release_dir}。請不要加 --no-build 重跑一次。"
        fi
        echo "==> 移除含有符號連結的舊版本：$release_dir"
        rm -rf "$release_dir"
        continue
    fi
    if [ -n "$(find "$release_dir" ! -user root -print -quit)" ]; then
        echo "==> 把版本目錄改為 root 擁有：$release_dir"
        chown -R root:wheel "$release_dir"
        chmod -R go-w "$release_dir"
    fi
done < <(find "$RELEASES_DIR" -mindepth 1 -maxdepth 1)

for directory in "$PREFIX/data" "$PREFIX/var"; do
    if [ -e "$directory" ]; then
        [ -d "$directory" ] || die "安裝目錄已存在但不是目錄：$directory"
        [ "$(stat -f %u "$directory")" = "$SERVICE_UID" ] ||
            die "此目錄必須由服務帳號 $SERVICE_USER 擁有：$directory"
    else
        install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 700 "$directory"
    fi
done
sudo -u "$SERVICE_USER" -H install -d -m 700 "$PREFIX/var/log" "$PREFIX/var/staging"
# Do not touch the mtime if it already exists: it would send the still-loaded restarter on a pointless restart mid-install.
if [ -e "$PREFIX/var/restart.request" ] && [ ! -f "$PREFIX/var/restart.request" ] && [ ! -L "$PREFIX/var/restart.request" ]; then
    die "restart.request 必須是普通檔案：$PREFIX/var/restart.request"
fi
if [ -L "$PREFIX/var/restart.request" ]; then
    sudo -u "$SERVICE_USER" -H rm -f "$PREFIX/var/restart.request"
fi
if [ ! -f "$PREFIX/var/restart.request" ]; then
    request_tmp="$(sudo -u "$SERVICE_USER" -H mktemp "$PREFIX/var/.restart.request.XXXXXX")"
    sudo -u "$SERVICE_USER" -H chmod 600 "$request_tmp"
    sudo -u "$SERVICE_USER" -H mv -f "$request_tmp" "$PREFIX/var/restart.request"
fi
sudo -u "$SERVICE_USER" -H chmod -R go-rwx "$PREFIX/data" "$PREFIX/var"

# The root daemon's state and logs must never live inside the service-account-writable PREFIX, otherwise the service account
# could use a symlink to make root overwrite arbitrary files.
[ ! -L "$RESTART_STATE" ] || die "restarter state 目錄不可為符號連結：$RESTART_STATE"
install -d -o root -g wheel -m 700 "$RESTART_STATE"
chmod -N "$RESTART_STATE"
chmod 700 "$RESTART_STATE"
[ ! -L "$RESTART_STATE/restart.log" ] || die "restarter log 不可為符號連結。"
[ ! -e "$RESTART_STATE/restart.log" ] || [ -f "$RESTART_STATE/restart.log" ] ||
    die "restarter log 必須是普通檔案。"
touch "$RESTART_STATE/restart.log"
chown root:wheel "$RESTART_STATE/restart.log"
chmod -N "$RESTART_STATE/restart.log"
chmod 600 "$RESTART_STATE/restart.log"
sudo -u "$SERVICE_USER" -H rm -f "$PREFIX/var/restart.applied"

prefix_record_tmp="$(mktemp "$RESTART_STATE/.install-prefix.XXXXXX")"
trap 'rm -f "$prefix_record_tmp"' EXIT
printf '%s\n' "$PREFIX" > "$prefix_record_tmp"
chmod -N "$prefix_record_tmp"
chmod 600 "$prefix_record_tmp"
[ ! -L "$RESTART_STATE/install-prefix" ] || die "安裝路徑紀錄不可為符號連結。"
[ ! -e "$RESTART_STATE/install-prefix" ] || [ -f "$RESTART_STATE/install-prefix" ] ||
    die "安裝路徑紀錄必須是普通檔案。"
mv -f "$prefix_record_tmp" "$RESTART_STATE/install-prefix"
trap - EXIT

service_uid_record_tmp="$(mktemp "$RESTART_STATE/.service-uid.XXXXXX")"
trap 'rm -f "$service_uid_record_tmp"' EXIT
printf '%s\n' "$SERVICE_UID" > "$service_uid_record_tmp"
chmod -N "$service_uid_record_tmp"
chmod 600 "$service_uid_record_tmp"
[ ! -L "$RESTART_STATE/service-uid" ] || die "服務 UID 紀錄不可為符號連結。"
[ ! -e "$RESTART_STATE/service-uid" ] || [ -f "$RESTART_STATE/service-uid" ] ||
    die "服務 UID 紀錄必須是普通檔案。"
mv -f "$service_uid_record_tmp" "$RESTART_STATE/service-uid"
trap - EXIT

if [ "$BUILD" = "1" ]; then
    BUILD_LOG="$(mktemp "$RESTART_STATE/.install-build.XXXXXX")"
    trap 'rm -f "$BUILD_LOG"' EXIT

    echo "==> 以 $SERVICE_USER 的身分建置到暫存區"
    sudo -u "$SERVICE_USER" -H env WALIS_PREFIX="$PREFIX" \
        bash "$REPO_ROOT/deploy/build-release.sh" 2>&1 | tee "$BUILD_LOG"

    STAGED="$(tail -n 1 "$BUILD_LOG")"
    if [ ! -x "$STAGED/Walis.Api" ]; then
        echo "建置輸出不正確：$STAGED" >&2
        exit 1
    fi
    rm -f "$BUILD_LOG"
    trap - EXIT

    # The staged release is validated, moved into the root-owned releases/ and made current by the same code the restarter
    # uses after a self-update; the service-uid record it needs was written above.
    echo "==> 驗證暫存的版本並搬進 ${RELEASES_DIR}"
    WALIS_PROMOTE_ONLY=1 /bin/bash "$REPO_ROOT/deploy/apply-restart.sh" "$PREFIX" "$LABEL" "$RESTART_STATE"
    RELEASE="$(readlink "$PREFIX/current")"
    [ -x "$RELEASE/Walis.Api" ] || die "切換 current 後找不到可執行的 Walis.Api：$RELEASE"
else
    # --no-build: only re-register the daemons and keep the existing current.
    RELEASE="$(readlink "$PREFIX/current" 2>/dev/null || true)"
    if [ -z "$RELEASE" ] || [ ! -x "$RELEASE/Walis.Api" ]; then
        echo "沒有可用的 current，請先不要加 --no-build 跑一次。" >&2
        exit 1
    fi
    case "$RELEASE" in
        "$RELEASES_DIR"/*) ;;
        *) die "current 指向 releases 之外的位置，拒絕沿用：$RELEASE" ;;
    esac
    # A current link left by an older install.sh belongs to the service account; re-create it as root so it can no longer be re-pointed.
    if [ "$(stat -f %u "$PREFIX/current")" != "0" ]; then
        echo "==> 把 current 改為 root 擁有"
        ln -s "$RELEASE" "$PREFIX/.current.$$"
        mv -f "$PREFIX/.current.$$" "$PREFIX/current"
    fi
    echo "==> 略過建置，沿用 $RELEASE"
fi

# The restart script deliberately lives in macOS's privileged helper directory: it is run by the root daemon, and putting it
# in /usr/local, often owned by the logged-in user, would let the service account replace the script and gain root.
if [ ! -e "$HELPER_DIR" ]; then
    validate_root_only_ancestry "$(dirname "$HELPER_DIR")"
    install -d -o root -g wheel -m 755 "$HELPER_DIR"
fi
validate_root_only_ancestry "$HELPER_DIR"
[ ! -L "$HELPER" ] || die "privileged helper 不可為符號連結：$HELPER"
[ ! -e "$HELPER" ] || [ -f "$HELPER" ] || die "privileged helper 必須是普通檔案：$HELPER"
HELPER_TMP="$(mktemp "$HELPER_DIR/.com.walisayu.walis-apply-restart.XXXXXX")"
trap 'rm -f "$HELPER_TMP"' EXIT
install -o root -g wheel -m 755 "$REPO_ROOT/deploy/apply-restart.sh" "$HELPER_TMP"
chmod -N "$HELPER_TMP"
chmod 755 "$HELPER_TMP"
mv -f "$HELPER_TMP" "$HELPER"
trap - EXIT
[ ! -e "$OLD_HELPER" ] || rm -f "$OLD_HELPER"
if [ -e "$LEGACY_HELPER" ] || [ -L "$LEGACY_HELPER" ]; then
    if ! sudo -u "$SERVICE_USER" -H rm -f "$LEGACY_HELPER" 2>/dev/null; then
        echo "警告：舊 helper 已停用但無法安全自動刪除，請人工確認：$LEGACY_HELPER" >&2
    fi
fi

SERVICE_PLIST="$DAEMONS/$LABEL.plist"
RESTART_PLIST="$DAEMONS/$RESTART_LABEL.plist"
validate_root_only_ancestry "$DAEMONS"
[ ! -L "$SERVICE_PLIST" ] || die "LaunchDaemon plist 不可為符號連結：$SERVICE_PLIST"
[ ! -L "$RESTART_PLIST" ] || die "LaunchDaemon plist 不可為符號連結：$RESTART_PLIST"
[ ! -e "$SERVICE_PLIST" ] || [ -f "$SERVICE_PLIST" ] ||
    die "LaunchDaemon plist 必須是普通檔案：$SERVICE_PLIST"
[ ! -e "$RESTART_PLIST" ] || [ -f "$RESTART_PLIST" ] ||
    die "LaunchDaemon plist 必須是普通檔案：$RESTART_PLIST"
SERVICE_PLIST_TMP="$(mktemp "$DAEMONS/.${LABEL}.XXXXXX")"
RESTART_PLIST_TMP="$(mktemp "$DAEMONS/.${RESTART_LABEL}.XXXXXX")"
trap 'rm -f "$SERVICE_PLIST_TMP" "$RESTART_PLIST_TMP"' EXIT

cat > "$SERVICE_PLIST_TMP" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>$XML_PREFIX/current/Walis.Api</string>
        <string>--urls</string>
        <string>http://127.0.0.1:$XML_PORT</string>
    </array>
    <key>UserName</key>
    <string>$XML_SERVICE_USER</string>
    <key>GroupName</key>
    <string>$XML_SERVICE_GROUP</string>
    <key>WorkingDirectory</key>
    <string>$XML_PREFIX/current</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>HOME</key>
        <string>$XML_SERVICE_HOME</string>
        <key>PATH</key>
        <string>/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin</string>
        <key>ASPNETCORE_ENVIRONMENT</key>
        <string>Production</string>
        <!-- No diagnostics IPC socket: a build under the same account could otherwise attach to the service process. -->
        <key>DOTNET_EnableDiagnostics</key>
        <string>0</string>
        <key>AllowedHosts</key>
        <string>$XML_ALLOWED_HOSTS</string>
        <!-- External TLS terminates in front of cloudflared/Caddy; sessions always use Secure cookies. -->
        <key>WALIS_SECURE_COOKIES</key>
        <string>true</string>
        <!-- The same-machine Caddy; X-Forwarded-For from any other peer is ignored. -->
        <key>Security__TrustedProxies</key>
        <string>$XML_TRUSTED_PROXIES</string>
        <key>Security__BootstrapToken</key>
        <string>$XML_BOOTSTRAP_TOKEN</string>
        <key>ConnectionStrings__Walis</key>
        <string>Data Source=$XML_PREFIX/data/walis.db</string>
    </dict>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>ThrottleInterval</key>
    <integer>10</integer>
    <key>ProcessType</key>
    <string>Background</string>
    <key>StandardOutPath</key>
    <string>$XML_PREFIX/var/log/walis.out.log</string>
    <key>StandardErrorPath</key>
    <string>$XML_PREFIX/var/log/walis.err.log</string>
</dict>
</plist>
PLIST

cat > "$RESTART_PLIST_TMP" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$RESTART_LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>$XML_HELPER</string>
        <string>$XML_PREFIX</string>
        <string>$LABEL</string>
        <string>$XML_RESTART_STATE</string>
    </array>
    <key>WatchPaths</key>
    <array>
        <string>$XML_PREFIX/var/restart.request</string>
    </array>
    <key>RunAtLoad</key>
    <false/>
    <key>StandardOutPath</key>
        <string>$XML_RESTART_STATE/restart.log</string>
    <key>StandardErrorPath</key>
        <string>$XML_RESTART_STATE/restart.log</string>
</dict>
</plist>
PLIST

plutil -lint "$SERVICE_PLIST_TMP" "$RESTART_PLIST_TMP" >/dev/null
chown root:wheel "$SERVICE_PLIST_TMP" "$RESTART_PLIST_TMP"
chmod -N "$SERVICE_PLIST_TMP" "$RESTART_PLIST_TMP"
chmod 600 "$SERVICE_PLIST_TMP" "$RESTART_PLIST_TMP"
mv -f "$SERVICE_PLIST_TMP" "$SERVICE_PLIST"
mv -f "$RESTART_PLIST_TMP" "$RESTART_PLIST"
trap - EXIT

# launchctl bootout is asynchronous: bootstrapping before it finishes unloading fails,
# leaving the service in an "unloaded but never loaded back" state.
reload_daemon() {
    local daemon_label="$1"
    local attempt=1

    stop_daemon "$daemon_label" || return 1

    while ! launchctl bootstrap system "$DAEMONS/$daemon_label.plist"; do
        if [ "$attempt" -ge 5 ]; then
            echo "載入 $daemon_label 失敗（已重試 $attempt 次）。" >&2
            return 1
        fi
        echo "    載入 $daemon_label 失敗，2 秒後重試"
        attempt=$((attempt + 1))
        sleep 2
    done

    return 0
}

for label in "$LABEL" "$RESTART_LABEL"; do
    echo "==> 載入 $label"
    reload_daemon "$label" || exit 1
done

launchctl kickstart -k "system/$LABEL"

echo "==> 等待服務就緒"
for _ in $(seq 1 30); do
    if curl -fsS "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
        echo "==> 服務已啟動：http://127.0.0.1:$PORT"
        echo "    日誌：$PREFIX/var/log/walis.out.log"
        exit 0
    fi
    sleep 1
done

echo "服務沒有在 30 秒內回應 /health，請看 $PREFIX/var/log/walis.err.log" >&2
exit 1
