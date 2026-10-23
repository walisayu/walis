#!/bin/sh
# One-line Walis install: detect the platform, download the matching release, verify the signature and checksum, install.
#
#   curl -fsSL https://walis.io/install.sh | sh
#   curl -fsSL https://walis.io/install.sh | sh -s -- --runner --server http://walis.internal:11120 --name pi-01 --token <key>
#
# Deliberately POSIX sh only: /bin/sh on Debian-based systems is dash, and bash syntax would blow up outright.
set -eu
umask 077

REPO="${WALIS_REPO:-walisayu/walis}"
VERSION="${WALIS_VERSION:-latest}"
BASE_URL="${WALIS_BASE_URL:-}"
RELEASE_HOST="https://releases.walis.io"
PREFIX="${WALIS_PREFIX:-}"
COMPONENT="server"
SERVICE=0
SERVER=""
NAME=""
TOKEN="${WALIS_TOKEN:-}"
ALLOW_INSECURE_HTTP=0
EXTERNAL_ISOLATION=0
SKIP_SIGNATURE=0

# The release signing key (Ed25519). deploy/release.sh signs SHA256SUMS.txt with the matching private key, and the checksum
# file is trusted only after this key verifies SHA256SUMS.txt.sig: the archive and its checksum come from the same server, so
# a hash alone would only prove the two files were replaced together. The same key is deploy/release-signing.pub in the
# repository; its SHA-256 fingerprint (of the DER public key) is printed in README → Verifying downloads.
RELEASE_PUBLIC_KEY='-----BEGIN PUBLIC KEY-----
MCowBQYDK2VwAyEAHS8hZuM6dlOGoaQ8GvLIgIMp2j2sr6qcKRvpQjQhofg=
-----END PUBLIC KEY-----'

usage() {
    cat <<'USAGE'
Walis 安裝程式

  --runner              安裝遠端執行器，而不是伺服器
  --version <版本>      指定版本，預設是最新的一版
  --prefix <目錄>       安裝位置，預設 /usr/local/walis（沒有 root 時是 ~/.walis）
  --service             一併註冊成 systemd 服務（Linux，需要 root）
  --server <網址>       執行器要連的 Walis 位址
  --name <名稱>         執行器名稱
  --token <權杖>        執行器用的 API 金鑰；建議改用環境變數 WALIS_TOKEN，命令列參數
                        同機的其他帳號看得到。--service 會把它寫進 /etc/walis/runner.env（0600）
  --allow-insecure-http 允許從 HTTP 的 --base-url 下載發行檔；僅限另有私網加密時
  --external-isolation  宣告整台 runner 已放在專用 VM／容器內（搭配 --service）；
                        伺服器要求隔離邊界時才需要，管理員也要把名稱加入
                        「已置於外部隔離的執行器」
  --repo <擁有者/名稱>  改從別的 GitHub repo 下載
  --base-url <網址>     改從指定位置下載（離線或自架時用）
  --insecure-skip-signature
                        不驗 SHA256SUMS.txt 的簽章，只比對校驗碼；僅限緊急狀況，
                        且要自己用別的管道確認校驗碼
  -h, --help            這段說明

環境變數：WALIS_VERSION、WALIS_PREFIX、WALIS_REPO、WALIS_BASE_URL、WALIS_TOKEN
USAGE
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --runner) COMPONENT="runner"; shift ;;
        --server) SERVER="${2:-}"; shift 2 ;;
        --name) NAME="${2:-}"; shift 2 ;;
        --token) TOKEN="${2:-}"; shift 2 ;;
        --allow-insecure-http) ALLOW_INSECURE_HTTP=1; shift ;;
        --external-isolation) EXTERNAL_ISOLATION=1; shift ;;
        --insecure-skip-signature) SKIP_SIGNATURE=1; shift ;;
        --version) VERSION="${2:-}"; shift 2 ;;
        --prefix) PREFIX="${2:-}"; shift 2 ;;
        --repo) REPO="${2:-}"; shift 2 ;;
        --base-url) BASE_URL="${2:-}"; shift 2 ;;
        --service) SERVICE=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "不認得的選項：$1" >&2; usage >&2; exit 1 ;;
    esac
done

die() {
    echo "錯誤：$*" >&2
    exit 1
}

validate_release_name() {
    value="$1"
    label="$2"

    [ -n "${value}" ] || die "${label}不可為空白。"
    [ "${#value}" -le 100 ] || die "${label}過長。"
    case "${value}" in
        .|..|*[!A-Za-z0-9._-]*) die "${label}只能包含 ASCII 英數、點、底線與連字號。" ;;
    esac
}

case "${REPO}" in
    */*) ;;
    *) die "repo 必須是 owner/name。" ;;
esac
[ "${#REPO}" -le 200 ] || die "repo 名稱過長。"
repo_owner="${REPO%%/*}"
repo_name="${REPO#*/}"
[ "${repo_name}" = "${repo_name%%/*}" ] || die "repo 必須是 owner/name。"
validate_release_name "${repo_owner}" "repo owner"
validate_release_name "${repo_name}" "repo name"

need() {
    command -v "$1" >/dev/null 2>&1 || die "找不到 ${1}，請先安裝。"
}

need uname
need tar

if command -v curl >/dev/null 2>&1; then
    DOWNLOAD="curl -fsSL -o"
elif command -v wget >/dev/null 2>&1; then
    DOWNLOAD="wget -qO"
else
    die "需要 curl 或 wget。"
fi

fetch() {
    # $1 URL, $2 file
    ${DOWNLOAD} "$2" "$1" || die "下載失敗：$1"
}

# verify_signature <file> <signature file> <label>: Ed25519 check against RELEASE_PUBLIC_KEY. Dies on any failure.
verify_signature() {
    [ "$(wc -c < "$2" | tr -d ' ')" -eq 64 ] || die "$3 的簽章不是 Ed25519 簽章（長度不對）。"

    command -v openssl >/dev/null 2>&1 ||
        die "找不到 openssl，無法驗證發行檔簽章。請安裝 OpenSSL 1.1.1 以上；緊急時可加 --insecure-skip-signature 並自行比對校驗碼。"
    printf '%s\n' "${RELEASE_PUBLIC_KEY}" > "${WORK}/release-signing.pub"

    # An openssl that cannot parse the Ed25519 key would fail verification for the wrong reason and look like a tampered file.
    # macOS ships LibreSSL, which is one of those; OpenSSL ≥ 1.1.1 (every current Linux) is fine.
    if ! openssl pkey -pubin -in "${WORK}/release-signing.pub" -noout >/dev/null 2>&1; then
        die "這台機器的 openssl（$(openssl version 2>/dev/null || echo '未知版本')）不支援 Ed25519，無法驗證發行檔簽章。
   請安裝 OpenSSL 1.1.1 以上（macOS：brew install openssl@3，並讓它排在 PATH 前面）；
   緊急時可加 --insecure-skip-signature，並自行用別的管道比對 SHA256SUMS.txt。"
    fi

    # pkeyutl exits non-zero on a bad signature, but the text is checked too: a tool that silently exits 0 must not pass.
    VERIFY_OUTPUT="$(openssl pkeyutl -verify -pubin -inkey "${WORK}/release-signing.pub" -rawin \
        -in "$1" -sigfile "$2" 2>&1)" || VERIFY_OUTPUT="FAILED: ${VERIFY_OUTPUT}"
    case "${VERIFY_OUTPUT}" in
        *"Signature Verified Successfully"*) echo "==> $3 簽章相符" ;;
        *) die "$3 的簽章驗證失敗：不是由 Walis 的發行金鑰簽署，或已被動過。
   ${VERIFY_OUTPUT}" ;;
    esac
}

# --- Platform detection ---
KERNEL="$(uname -s)"
MACHINE="$(uname -m)"

case "${KERNEL}" in
    Linux) OS="linux" ;;
    Darwin) OS="osx" ;;
    MINGW*|MSYS*|CYGWIN*)
        die "Windows 請到 https://github.com/${REPO}/releases 下載 win-x64 或 win-arm64 的 zip。" ;;
    *) die "不支援的系統：${KERNEL}" ;;
esac

case "${MACHINE}" in
    x86_64|amd64) ARCH="x64" ;;
    aarch64|arm64) ARCH="arm64" ;;
    armv7l|armv6l|armhf) ARCH="arm" ;;
    *) die "不支援的架構：${MACHINE}" ;;
esac

# musl systems such as Alpine need a different set of releases; there is no musl build for linux-arm.
LIBC=""
if [ "${OS}" = "linux" ] && [ "${ARCH}" != "arm" ]; then
    if [ -f /etc/alpine-release ] || (command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -qi musl); then
        LIBC="musl-"
    fi
fi

RID="${OS}-${LIBC}${ARCH}"

if [ "${OS}" = "osx" ] && [ "${ARCH}" = "arm" ]; then
    die "macOS 沒有 32 位元 ARM 的版本。"
fi

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT INT TERM

# --- Version ---
if [ -z "${BASE_URL}" ] && [ "${VERSION}" = "latest" ]; then
    TAGS="${WORK}/latest.json"

    # Official releases are served from releases.walis.io (Cloudflare R2) as well as GitHub Releases; try R2 first for the
    # default repo so installs work without hitting GitHub's anonymous API rate limit or when the public repo is offline.
    # latest.json is signed with the release key: whoever controls the bucket (or the path to it) must not be able to point
    # every new install at an older, genuinely signed release that still carries fixed vulnerabilities.
    if [ "${REPO}" = "walisayu/walis" ] && ${DOWNLOAD} "${TAGS}" "${RELEASE_HOST}/latest.json" 2>/dev/null; then
        if [ "${SKIP_SIGNATURE}" != "1" ]; then
            ${DOWNLOAD} "${TAGS}.sig" "${RELEASE_HOST}/latest.json.sig" 2>/dev/null ||
                die "下載伺服器的 latest.json 沒有簽章，無法確認最新版本。請用 --version <版本> 指定要安裝的版本。"
            verify_signature "${TAGS}" "${TAGS}.sig" "latest.json"
        fi
        VERSION="$(sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "${TAGS}" | head -1)"
        [ -n "${VERSION}" ] && BASE_URL="${RELEASE_HOST}/${VERSION}"
    elif ${DOWNLOAD} "${TAGS}" "https://api.github.com/repos/${REPO}/releases/latest" 2>/dev/null; then
        VERSION="$(sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "${TAGS}" | head -1)"
    else
        die "問不到 ${REPO} 的最新版本。可能是還沒發行過、repo 不是公開的，或這台機器連不到下載伺服器。
   已經有檔案的話：用 --version <版本> 指定，或用 --base-url <網址> 指到自己的位置。"
    fi

    [ -n "${VERSION}" ] || die "${REPO} 還沒有任何發行版本，請用 --version 或 --base-url 指定來源。"
fi

validate_release_name "${VERSION}" "版本"

if [ -z "${BASE_URL}" ]; then
    if [ "${REPO}" = "walisayu/walis" ]; then
        BASE_URL="${RELEASE_HOST}/${VERSION}"
    else
        BASE_URL="https://github.com/${REPO}/releases/download/${VERSION}"
    fi
fi
case "${BASE_URL}" in
    https://*) ;;
    http://127.0.0.1:*|http://localhost:*) ;;
    http://*)
        [ "${ALLOW_INSECURE_HTTP}" = "1" ] ||
            die "下載來源必須使用 HTTPS；可信私網才可加 --allow-insecure-http。"
        ;;
    *) die "下載來源必須是 HTTP/HTTPS 網址。" ;;
esac
BASE_URL="${BASE_URL%/}"

if [ "${COMPONENT}" = "runner" ]; then
    ARCHIVE="walis-runner-${VERSION}-${RID}.tar.gz"
    BINARY="walis-runner"
    LINK="walis-runner"
else
    ARCHIVE="walis-${VERSION}-${RID}.tar.gz"
    BINARY="Walis.Api"
    LINK="walis"
fi

# --- Install location ---
if [ -z "${PREFIX}" ]; then
    if [ "$(id -u)" = "0" ]; then
        PREFIX="/usr/local/walis"
    else
        PREFIX="${HOME}/.walis"
    fi
fi

[ -n "${PREFIX}" ] || die "安裝位置不可為空白。"
case "${PREFIX}" in
    /*) ;;
    *) die "安裝位置必須是絕對路徑。" ;;
esac
case "${PREFIX}" in
    *[!A-Za-z0-9._/-]*|*//*|*/./*|*/../*|*/.|*/..)
        die "安裝位置含有不安全的字元或路徑片段。"
        ;;
esac
[ "${PREFIX}" != "/" ] || die "安裝位置不可為檔案系統根目錄。"

BIN_DIR="/usr/local/bin"
[ "$(id -u)" = "0" ] || BIN_DIR="${HOME}/.local/bin"

echo "==> Walis ${VERSION}（${RID}）→ ${PREFIX}"

echo "==> 下載 ${ARCHIVE}"
fetch "${BASE_URL}/${ARCHIVE}" "${WORK}/${ARCHIVE}"

# --- Signature of the checksum file (must not be skipped silently) ---
fetch "${BASE_URL}/SHA256SUMS.txt" "${WORK}/SHA256SUMS.txt"

if [ "${SKIP_SIGNATURE}" = "1" ]; then
    echo "==> 警告：已略過簽章驗證（--insecure-skip-signature），只比對校驗碼；請用別的管道確認 SHA256SUMS.txt。" >&2
else
    fetch "${BASE_URL}/SHA256SUMS.txt.sig" "${WORK}/SHA256SUMS.txt.sig"
    verify_signature "${WORK}/SHA256SUMS.txt" "${WORK}/SHA256SUMS.txt.sig" "SHA256SUMS.txt"
fi

# --- Checksum ---
EXPECTED="$(awk -v archive="${ARCHIVE}" '$2 == archive || $2 == "*" archive { print $1; exit }' "${WORK}/SHA256SUMS.txt")"
case "${EXPECTED}" in
    *[!0-9A-Fa-f]*|"") die "SHA256SUMS.txt 沒有有效的 ${ARCHIVE} 校驗碼。" ;;
esac
[ "${#EXPECTED}" -eq 64 ] || die "${ARCHIVE} 的 SHA-256 長度不正確。"

if command -v sha256sum >/dev/null 2>&1; then
    ACTUAL="$(sha256sum "${WORK}/${ARCHIVE}" | awk '{print $1}')"
elif command -v shasum >/dev/null 2>&1; then
    ACTUAL="$(shasum -a 256 "${WORK}/${ARCHIVE}" | awk '{print $1}')"
else
    die "找不到 sha256sum 或 shasum，無法安全驗證下載內容。"
fi

EXPECTED="$(printf '%s' "${EXPECTED}" | tr 'A-F' 'a-f')"
[ "${ACTUAL}" = "${EXPECTED}" ] || die "校驗碼不符，下載的檔案可能被動過。"
echo "==> 校驗碼相符"

echo "==> 解壓縮"
EXTRACTED="${WORK}/$(basename "${ARCHIVE}" .tar.gz)"
ARCHIVE_ROOT="$(basename "${ARCHIVE}" .tar.gz)"

# The checksum only proves the file matches what the publisher provided; if a self-hosted source is compromised, a malicious tar
# could still overwrite files outside the install directory during extraction via ../, absolute paths or symlinks. Only a single expected
# root directory is accepted, and the archive may contain only regular files and directories.
if ! tar -tzf "${WORK}/${ARCHIVE}" | awk -v root="${ARCHIVE_ROOT}" '
    $0 == root || index($0, root "/") == 1 { next }
    { exit 1 }
'; then
    die "壓縮檔含有超出預期根目錄的路徑。"
fi
if ! tar -tvzf "${WORK}/${ARCHIVE}" | awk '
    substr($0, 1, 1) == "-" || substr($0, 1, 1) == "d" { next }
    { exit 1 }
'; then
    die "壓縮檔不可包含符號連結、硬連結或特殊檔案。"
fi

mkdir "${EXTRACTED}"
tar -xzf "${WORK}/${ARCHIVE}" --strip-components=1 -C "${EXTRACTED}"
[ -d "${EXTRACTED}" ] && [ ! -L "${EXTRACTED}" ] || die "壓縮檔的內容不如預期。"

TARGET="${PREFIX}/${COMPONENT}s/${VERSION}"
mkdir -p "${TARGET}" || die "沒有權限寫入 ${PREFIX}，請改用 --prefix 或加上 sudo。"
rm -rf "${TARGET}"
mv "${EXTRACTED}" "${TARGET}"
chmod +x "${TARGET}/${BINARY}"
# umask 077 above keeps the download private while it is being verified, but the installed tree is executed by other
# accounts (the systemd unit below runs as a DynamicUser), so every directory on the way must be traversable.
chmod -R u+rwX,go+rX "${TARGET}"
chmod go+rx "${PREFIX}" "${PREFIX}/${COMPONENT}s"

# current points at this version; upgrading only swaps the symbolic link.
ln -sfn "${TARGET}" "${PREFIX}/${COMPONENT}-current"

mkdir -p "${BIN_DIR}"
ln -sfn "${PREFIX}/${COMPONENT}-current/${BINARY}" "${BIN_DIR}/${LINK}"

echo "==> 已安裝到 ${TARGET}"

# --- Optional: register as a systemd service ---
if [ "${SERVICE}" = "1" ]; then
    if [ "${OS}" != "linux" ] || ! command -v systemctl >/dev/null 2>&1; then
        echo "==> 這台機器沒有 systemd，略過服務註冊。"
    elif [ "$(id -u)" != "0" ]; then
        echo "==> 註冊服務需要 root，略過。" >&2
    else
        UNIT="/etc/systemd/system/walis-${COMPONENT}.service"
        ENV_DIR="/etc/walis"
        ENV_FILE=""
        ENV_CONTENT=""

        if [ "${COMPONENT}" = "runner" ]; then
            [ -n "${SERVER}" ] || die "註冊執行器服務要指定 --server。"
            [ -n "${NAME}" ] || NAME="$(hostname)"
            case "${SERVER}" in
                *[!A-Za-z0-9._~:/?\&=+\-]*) die "server 網址含有不安全的 systemd 參數字元。" ;;
            esac
            case "${NAME}" in
                ""|*[!A-Za-z0-9._-]*) die "runner 名稱只能包含 ASCII 英數、點、底線與連字號。" ;;
            esac
            case "${TOKEN}" in
                *[!A-Za-z0-9._~-]*) die "token 含有不安全的 systemd 環境字元。" ;;
            esac
            EXEC="${BIN_DIR}/${LINK} --server ${SERVER} --name ${NAME} --workdir /var/lib/walis-runner"
            WORKDIR="/var/lib/walis-runner"
            [ "${ALLOW_INSECURE_HTTP}" = "0" ] || EXEC="${EXEC} --allow-insecure-http true"
            [ "${EXTERNAL_ISOLATION}" = "0" ] || EXEC="${EXEC} --external-isolation true"
            # The token goes into a root-only file read by the service manager, never into the unit itself (readable through
            # systemctl show) nor onto the command line (readable through ps). The runner drops it from its own environment once parsed.
            SERVICE_ENVIRONMENT="EnvironmentFile=${ENV_DIR}/runner.env"
            ENV_FILE="${ENV_DIR}/runner.env"
            ENV_CONTENT="WALIS_TOKEN=${TOKEN}"
            SERVICE_STORAGE="ReadWritePaths=/var/lib/walis-runner"
        else
            EXEC="${BIN_DIR}/${LINK} --urls http://0.0.0.0:11120"
            # The API takes its content root from the current directory: wwwroot (the admin UI) and data/ are looked up
            # there, so the service has to start inside the installed release, not inside the state directory.
            WORKDIR="${TARGET}"
            SERVICE_ENVIRONMENT="EnvironmentFile=${ENV_DIR}/server.env"
            ENV_FILE="${ENV_DIR}/server.env"
            ENV_CONTENT="ConnectionStrings__Walis=Data Source=/var/lib/walis-server/walis.db"
            # Artifacts/cache/backups are located relative to ContentRoot/data; bind that exact directory to the
            # DynamicUser's persistent StateDirectory, changing only the database connection string is not enough.
            [ ! -L "${TARGET}/data" ] || die "伺服器 data 目錄不可為符號連結。"
            mkdir -p "${TARGET}/data"
            [ -d "${TARGET}/data" ] || die "伺服器 data 路徑不是目錄。"
            SERVICE_STORAGE="BindPaths=/var/lib/walis-server:${TARGET}/data
ReadWritePaths=/var/lib/walis-server ${TARGET}/data"
        fi

        # Environment files are read by the service manager as root before the DynamicUser is applied, so 0600 root is enough.
        [ ! -L "${ENV_DIR}" ] || die "環境檔目錄不可為符號連結：${ENV_DIR}"
        mkdir -p "${ENV_DIR}"
        chmod 700 "${ENV_DIR}"
        [ ! -L "${ENV_FILE}" ] || die "環境檔不可為符號連結：${ENV_FILE}"
        printf '%s\n' "${ENV_CONTENT}" > "${ENV_FILE}"
        chmod 600 "${ENV_FILE}"

        # Hardening beyond DynamicUser: no capabilities, only the socket families the service needs, the system-service syscall
        # set, no kernel knobs, no SUID/SGID, no realtime, no leftover IPC. MemoryDenyWriteExecute stays off because the .NET JIT
        # needs writable-then-executable pages; DOTNET_EnableDiagnostics=0 closes the diagnostics IPC socket a build could attach to.
        cat > "${UNIT}" <<UNITFILE
[Unit]
Description=Walis ${COMPONENT}
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
DynamicUser=yes
StateDirectory=walis-${COMPONENT}
WorkingDirectory=${WORKDIR}
ExecStart=${EXEC}
${SERVICE_ENVIRONMENT}
Environment=DOTNET_EnableDiagnostics=0
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ProtectProc=ptraceable
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectKernelLogs=true
ProtectControlGroups=true
CapabilityBoundingSet=
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
RestrictNamespaces=true
RestrictSUIDSGID=true
RestrictRealtime=true
LockPersonality=true
RemoveIPC=true
MemoryDenyWriteExecute=false
SystemCallFilter=@system-service
SystemCallErrorNumber=EPERM
SystemCallArchitectures=native
${SERVICE_STORAGE}
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
UNITFILE

        chmod 600 "${UNIT}"

        systemctl daemon-reload
        systemctl enable --now "walis-${COMPONENT}"
        echo "==> 服務 walis-${COMPONENT} 已啟動（systemctl status walis-${COMPONENT}）"
    fi
fi

echo
case ":${PATH}:" in
    *":${BIN_DIR}:"*) ;;
    *) echo "提醒：${BIN_DIR} 不在 PATH 裡，請加進去。" ;;
esac

# Setting up from another machine asks for the setup code; say where it is, since under systemd nobody sees the console.
DOCS_URL="https://github.com/${REPO}/blob/main/docs/self-hosting.md"

if [ "${COMPONENT}" = "runner" ]; then
    if [ "${SERVICE}" != "1" ]; then
        echo "啟動執行器（權杖用環境變數或檔案給，不要寫在命令列上）："
        echo "  WALIS_TOKEN=<權杖> ${LINK} --server http://walis.internal:11120 --name $(hostname)"
        echo "  ${LINK} --server http://walis.internal:11120 --name $(hostname) --token-file /etc/walis/runner.token"
    else
        echo "權杖存在 /etc/walis/runner.env（只有 root 可讀）；換權杖時改那個檔案再 systemctl restart walis-runner。"
    fi
    echo "<權杖> 是在管理介面建立、綁定這個執行器名稱的 API 金鑰，做法見："
    echo "  ${DOCS_URL}#remote-runners"
else
    if [ "${SERVICE}" = "1" ]; then
        echo "用瀏覽器開 http://<這台機器>:11120 建立第一位管理員。"
        echo "從其他機器設定時要輸入設定碼："
        echo "  sudo cat /var/lib/walis-server/setup-code.txt"
    else
        echo "啟動 Walis："
        echo "  ${LINK} --urls http://0.0.0.0:11120"
        echo "然後用瀏覽器開 http://<這台機器>:11120 建立第一位管理員。"
        echo "從其他機器設定時要輸入設定碼，啟動時會印在主控台。"
    fi
    echo
    echo "注意：伺服器預設監聽所有網路介面（0.0.0.0:11120），同一個網路上的任何機器都連得到，而且是未加密的 HTTP。"
    echo "      只給本機用就改成 --urls http://127.0.0.1:11120；要對外就放在提供 HTTPS 的反向代理後面，"
    echo "      並設定 Security__RequireHttps=true 與 AllowedHosts。Production 環境監聽非 loopback 位址時，"
    echo "      沒有沙箱的執行器預設不執行建置，直到管理員在「系統設定 → 建置」明確接受風險。"
    echo "對外開放前該收緊的設定（HTTPS、主機名稱、建置隔離）見："
    echo "  ${DOCS_URL}"
fi
