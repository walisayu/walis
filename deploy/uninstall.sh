#!/usr/bin/env bash
# Removes the macOS service. Keeps ${PREFIX} by default (the database and artifacts live there);
# add --purge to delete it as well.
set -euo pipefail
umask 077
export LC_ALL=C
export PATH="/usr/bin:/bin:/usr/sbin:/sbin"

LABEL="com.walisayu.walis"
RESTART_LABEL="com.walisayu.walis-restarter"
# Labels used before 2026-10-20, removed as well in case the installation was never upgraded.
OLD_LABEL="com.spex.walis"
OLD_RESTART_LABEL="com.spex.walis-restarter"
PREFIX="${WALIS_PREFIX-/usr/local/walis}"
HELPER="/Library/PrivilegedHelperTools/com.walisayu.walis-apply-restart"
OLD_HELPER="/Library/PrivilegedHelperTools/com.spex.walis-apply-restart"
LEGACY_HELPER="/usr/local/libexec/walis-apply-restart.sh"
DAEMONS="/Library/LaunchDaemons"
RESTART_STATE="/var/db/walis-restarter"
PURGE=0

die() {
    echo "$1" >&2
    exit 1
}

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
        /|/usr|/usr/local|/opt|/var|/private|/private/var|/tmp|/private/tmp|/Users|/Library|/System|/Applications|/Volumes)
            die "安裝路徑太寬廣，拒絕將它刪除：$canonical"
            ;;
    esac

    [ "$(dirname "$canonical")" != "/" ] || die "安裝路徑至少要位於兩層目錄之下：$canonical"
    case "$(basename "$canonical")" in
        *[Ww][Aa][Ll][Ii][Ss]*) ;;
        *) die "安裝路徑最後一層必須包含 walis，以避免刪錯資料：$canonical" ;;
    esac

    printf '%s\n' "$canonical"
}

validate_restart_state() {
    if [ ! -e "$RESTART_STATE" ] && [ ! -L "$RESTART_STATE" ]; then
        return 0
    fi

    [ ! -L "$RESTART_STATE" ] || die "restarter state 目錄不可為符號連結：$RESTART_STATE"
    [ -d "$RESTART_STATE" ] || die "restarter state 路徑不是目錄：$RESTART_STATE"

    state_uid="$(stat -f %u "$RESTART_STATE")"
    state_mode="$(stat -f %OLp "$RESTART_STATE")"
    state_symbolic_mode="$(stat -f %Sp "$RESTART_STATE")"
    case "$state_uid" in
        ''|*[!0-9]*) die "無法驗證 restarter state 擁有者：$RESTART_STATE" ;;
    esac
    case "$state_mode" in
        ''|*[!0-7]*) die "無法驗證 restarter state 權限：$RESTART_STATE" ;;
    esac
    [ "$state_uid" = "0" ] || die "restarter state 必須由 root 擁有：$RESTART_STATE"
    [ $((8#$state_mode & 0022)) -eq 0 ] || die "restarter state 不可讓 group/other 寫入：$RESTART_STATE"
    case "$state_symbolic_mode" in
        *+*) die "restarter state 不可含擴充 ACL：$RESTART_STATE" ;;
    esac
}

validate_root_record() {
    record_path="$1"
    maximum_size="$2"

    [ -f "$record_path" ] && [ ! -L "$record_path" ] ||
        die "缺少可信任的 root-only 紀錄：$record_path"

    record_uid="$(stat -f %u "$record_path")"
    record_mode="$(stat -f %OLp "$record_path")"
    record_symbolic_mode="$(stat -f %Sp "$record_path")"
    record_size="$(stat -f %z "$record_path")"
    case "$record_uid" in
        ''|*[!0-9]*) die "無法驗證紀錄擁有者：$record_path" ;;
    esac
    case "$record_mode" in
        ''|*[!0-7]*) die "無法驗證紀錄權限：$record_path" ;;
    esac
    case "$record_size" in
        ''|*[!0-9]*) die "無法驗證紀錄大小：$record_path" ;;
    esac
    [ "$record_uid" = "0" ] || die "紀錄必須由 root 擁有：$record_path"
    [ $((8#$record_mode & 0022)) -eq 0 ] || die "紀錄不可讓 group/other 寫入：$record_path"
    case "$record_symbolic_mode" in
        *+*) die "紀錄不可含擴充 ACL：$record_path" ;;
    esac
    [ "$record_size" -gt 1 ] && [ "$record_size" -le "$maximum_size" ] ||
        die "紀錄大小不合理：$record_path"
}

validate_purge_target() {
    target_exists=0
    if [ -e "$PREFIX" ] || [ -L "$PREFIX" ]; then
        [ ! -L "$PREFIX" ] || die "拒絕刪除符號連結形式的安裝路徑：$PREFIX"
        [ -d "$PREFIX" ] || die "安裝路徑不是目錄，拒絕刪除：$PREFIX"
        target_exists=1
    elif [ ! -d "$RESTART_STATE" ]; then
        # After a full purge the paths and trust records no longer exist; stay idempotent.
        PURGE_TARGET=""
        return 0
    fi

    prefix_record="$RESTART_STATE/install-prefix"
    service_uid_record="$RESTART_STATE/service-uid"
    validate_root_record "$prefix_record" 4096
    validate_root_record "$service_uid_record" 32

    recorded_prefix="$(< "$prefix_record")"
    recorded_canonical="$(canonicalize_prefix "$recorded_prefix")"
    [ "$recorded_prefix" = "$recorded_canonical" ] ||
        die "安裝路徑紀錄不是 canonical path，拒絕 --purge：$recorded_prefix"
    [ "$PREFIX" = "$recorded_canonical" ] ||
        die "要求刪除的路徑與安裝紀錄不符，拒絕 --purge：$PREFIX"

    PURGE_UID="$(< "$service_uid_record")"
    case "$PURGE_UID" in
        ''|*[!0-9]*) die "服務 UID 紀錄無效，拒絕 --purge：$PURGE_UID" ;;
    esac
    [ "${#PURGE_UID}" -le 10 ] || die "服務 UID 紀錄過大，拒絕 --purge。"
    [ "$PURGE_UID" -ne 0 ] || die "拒絕用 root 身分遞迴刪除安裝路徑。"

    if [ "$target_exists" = "0" ]; then
        PURGE_TARGET=""
        return 0
    fi

    # The current layout keeps PREFIX root-owned (releases/ and current are root's); an installation from before that handed
    # the whole PREFIX to the service account. Anything else is not ours to delete.
    prefix_owner="$(stat -f %u "$PREFIX")"
    [ "$prefix_owner" = "$PURGE_UID" ] || [ "$prefix_owner" = "0" ] ||
        die "安裝路徑擁有者與可信任紀錄不符，拒絕 --purge：$PREFIX"

    PURGE_TARGET="$PREFIX"
}

# releases/ and current belong to root in the current layout, so the service UID cannot delete them. Root removes them only after
# confirming every ancestor is root-only, releases/ is a real root-owned directory and nothing inside belongs to another account.
purge_root_owned_parts() {
    target="$1"
    validate_root_only_ancestry "$target"

    if [ -L "$target/current" ]; then
        rm -f "$target/current"
    fi

    releases="$target/releases"
    if [ -e "$releases" ] || [ -L "$releases" ]; then
        [ ! -L "$releases" ] || die "releases 不可為符號連結，拒絕刪除：$releases"
        [ -d "$releases" ] || die "releases 不是目錄，拒絕刪除：$releases"
        [ "$(stat -f %u "$releases")" = "0" ] || die "releases 不是 root 擁有，拒絕由 root 刪除：$releases"
        if [ -n "$(find "$releases" ! -user root -print -quit)" ]; then
            die "releases 內仍有不是 root 擁有的檔案，拒絕由 root 遞迴刪除：$releases"
        fi
        rm -rf "$releases"
    fi

    # The service UID emptied data/ and var/ but cannot unlink them from the root-owned PREFIX; rmdir refuses anything not empty.
    for leftover in "$target/data" "$target/var"; do
        if [ -d "$leftover" ] && [ ! -L "$leftover" ]; then
            rmdir "$leftover" || die "目錄仍有服務 UID 刪不掉的內容，拒絕由 root 遞迴處理：$leftover"
        fi
    done
}

stop_daemon() {
    local daemon_label="$1"
    local waited=0

    launchctl bootout "system/$daemon_label" 2>/dev/null || true

    while launchctl print "system/$daemon_label" >/dev/null 2>&1; do
        if [ "$waited" -ge 15 ]; then
            die "等不到 $daemon_label 卸載完成；尚未刪除任何檔案。"
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
        --purge) PURGE=1; shift ;;
        --prefix)
            [ $# -ge 2 ] || die "--prefix 缺少路徑。"
            PREFIX="$2"; shift 2
            ;;
        -h|--help) sed -n '2,3p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "未知的參數：$1" >&2; exit 1 ;;
    esac
done

PREFIX="$(canonicalize_prefix "$PREFIX")"

if [ "$(id -u)" -ne 0 ]; then
    echo "請用 sudo 執行：sudo $0 $*" >&2
    exit 1
fi

validate_restart_state
PURGE_TARGET=""
if [ "$PURGE" = "1" ]; then
    validate_purge_target
fi

# The root restarter must be fully unloaded first; otherwise the old configuration could still run the replaceable
# old script under /usr/local while the helper is being moved.
stop_daemon "$RESTART_LABEL"
stop_daemon "$LABEL"
stop_daemon "$OLD_RESTART_LABEL"
stop_daemon "$OLD_LABEL"
validate_root_only_ancestry "$DAEMONS"
rm -f "$DAEMONS/$LABEL.plist" "$DAEMONS/$RESTART_LABEL.plist" "$DAEMONS/$OLD_LABEL.plist" "$DAEMONS/$OLD_RESTART_LABEL.plist"

helper_parent="$(dirname "$HELPER")"
if [ -e "$helper_parent" ] || [ -L "$helper_parent" ]; then
    validate_root_only_ancestry "$helper_parent"
    rm -f "$HELPER" "$OLD_HELPER"
fi
if [ -e "$LEGACY_HELPER" ] || [ -L "$LEGACY_HELPER" ]; then
    echo "警告：未以 root 穿越 /usr/local 刪除舊 helper；請人工確認：$LEGACY_HELPER" >&2
fi

if [ "$PURGE" = "1" ]; then
    if [ -n "$PURGE_TARGET" ]; then
        # Check once more right before deleting; rm -rf must never receive a path not validated against the record.
        [ ! -L "$PURGE_TARGET" ] && [ -d "$PURGE_TARGET" ] ||
            die "安裝路徑在驗證後發生變化，已停止刪除：$PURGE_TARGET"
        [ "$(canonicalize_prefix "$PURGE_TARGET")" = "$PURGE_TARGET" ] ||
            die "安裝路徑在驗證後發生變化，已停止刪除：$PURGE_TARGET"
        echo "==> 以服務 UID ${PURGE_UID} 刪除 ${PURGE_TARGET} 內容（含資料庫）"
        # Recursive deletion always runs with dropped privileges; even if the service account races to swap an intermediate symlink,
        # it cannot make root delete anything elsewhere. When the parent is root-only, the last level may leave an empty directory.
        sudo -u "#$PURGE_UID" -H rm -rf "$PURGE_TARGET" 2>/dev/null || true

        if [ -e "$PURGE_TARGET" ] || [ -L "$PURGE_TARGET" ]; then
            [ ! -L "$PURGE_TARGET" ] && [ -d "$PURGE_TARGET" ] ||
                die "安裝路徑在刪除期間發生變化，已停止：$PURGE_TARGET"
            [ "$(canonicalize_prefix "$PURGE_TARGET")" = "$PURGE_TARGET" ] ||
                die "安裝路徑在刪除期間發生變化，已停止：$PURGE_TARGET"

            purge_parent="$(dirname "$PURGE_TARGET")"
            validate_root_only_ancestry "$purge_parent"

            # What the service UID could not delete is root's own releases/ and current; everything else must be gone by now.
            if [ "$(stat -f %u "$PURGE_TARGET")" = "0" ]; then
                purge_root_owned_parts "$PURGE_TARGET"
            fi

            # Root may only run a non-recursive rmdir on an empty directory under a trusted parent.
            rmdir "$PURGE_TARGET" ||
                die "安裝路徑仍含無法由服務 UID 刪除的內容，拒絕由 root 遞迴處理：$PURGE_TARGET"
        fi
    else
        echo "==> ${PREFIX} 不存在，沒有資料需要刪除"
    fi

    if [ -d "$RESTART_STATE" ]; then
        rm -rf "$RESTART_STATE"
    fi
else
    # Keep the two root-only records so a later --purge can still verify the exact path and the dropped-privilege UID;
    # the other runtime state can be removed safely.
    rm -f "$RESTART_STATE/restart.log" "$RESTART_STATE/restart.applied"
    echo "==> 保留 ${PREFIX}，要一併刪除請加 --purge"
fi

echo "==> 已移除服務"
