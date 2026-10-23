#!/usr/bin/env bash
# Packs downloadable, ready-to-run releases: the API (with the admin UI) and the runner, one per platform.
#
#   ./deploy/release.sh                      # every platform
#   ./deploy/release.sh --targets linux-arm64,linux-arm
#   ./deploy/release.sh --runner-only --targets linux-arm64   # only the runner for Raspberry Pi
#   ./deploy/release.sh --list
#
# Output goes to dist/, one archive per platform, with SHA256SUMS.txt, its Ed25519 signature SHA256SUMS.txt.sig and
# RUNTIME.txt (the .NET runtime embedded in the archives) at the end.
# Everything is self-contained (mcp/ included): the target machine does not need .NET installed.
#
# Supply-chain checks, in order: the SDK must be at least the one pinned in global.json; packages are restored in locked mode
# (packages.lock.json); the admin UI is installed with npm ci only; every runtimeconfig.json is checked against MIN_RUNTIME
# before packing; SHA256SUMS.txt is signed with the key in WALIS_SIGNING_KEY (deploy/get-walis.sh verifies it with
# deploy/release-signing.pub). --unsigned skips only the signature.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT="${REPO_ROOT}/dist"
VERSION=""
TARGETS=""
BUILD_API=1
BUILD_RUNNER=1
BUILD_UI=1
SIGN=1
SIGNING_KEY="${WALIS_SIGNING_KEY:-${HOME}/.config/walis/release-signing.key}"
PUBLIC_KEY="${REPO_ROOT}/deploy/release-signing.pub"

# The oldest .NET runtime an archive may embed. 10.0.13 fixes the October 2026 Kestrel advisories (CVE-2026-88092 and
# CVE-2026-70188 among 17); raise it when a newer servicing release fixes something that matters.
MIN_RUNTIME="10.0.13"

export PATH="/opt/homebrew/bin:/usr/local/bin:${PATH}"

# Supported platforms. An RPi 4/5 running a 64-bit system is linux-arm64;
# one still running 32-bit Raspberry Pi OS is linux-arm.
# Keep in sync with WalisReleaseRuntimeIdentifiers in Directory.Build.props: a RID missing there is not in the lock files
# and the locked-mode restore refuses it.
ALL_TARGETS="win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64 linux-arm linux-musl-x64 linux-musl-arm64"

describe_target() {
    case "$1" in
        win-x64) echo "Windows（Intel／AMD 64 位元）" ;;
        win-arm64) echo "Windows（ARM64）" ;;
        osx-x64) echo "macOS（Intel）" ;;
        osx-arm64) echo "macOS（Apple Silicon）" ;;
        linux-x64) echo "Linux（Intel／AMD 64 位元）" ;;
        linux-arm64) echo "Linux ARM64（含 Raspberry Pi 4／5 的 64 位元系統）" ;;
        linux-arm) echo "Linux ARM 32 位元（Raspberry Pi 的 32 位元系統）" ;;
        linux-musl-x64) echo "Alpine／musl（Intel／AMD 64 位元）" ;;
        linux-musl-arm64) echo "Alpine／musl（ARM64）" ;;
        *) echo "" ;;
    esac
}

usage() {
    cat <<'USAGE'
用法：./deploy/release.sh [選項]

  --version <版本>   版本字串，預設用 git describe，取不到就用日期
  --targets <清單>   逗號分隔的平台，預設全部；用 --list 看有哪些
  --output <目錄>    輸出目錄，預設 dist/
  --api-only         只打包 API（含管理介面）
  --runner-only      只打包執行器
  --skip-ui          沿用現有的管理介面建置產物，不重跑 npm
  --unsigned         不簽 SHA256SUMS.txt（只給本機測試用；正式發佈一律要簽）
  --list             列出支援的平台
  -h, --help         這段說明

環境變數：WALIS_SIGNING_KEY  簽章用的 Ed25519 私鑰（PEM），預設 ~/.config/walis/release-signing.key
USAGE
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --version) VERSION="${2:-}"; shift 2 ;;
        --targets) TARGETS="$(echo "${2:-}" | tr ',' ' ')"; shift 2 ;;
        --output) OUTPUT="${2:-}"; shift 2 ;;
        --api-only) BUILD_RUNNER=0; shift ;;
        --runner-only) BUILD_API=0; BUILD_UI=0; shift ;;
        --skip-ui) BUILD_UI=0; shift ;;
        --unsigned) SIGN=0; shift ;;
        --list)
            for target in ${ALL_TARGETS}; do
                printf '  %-18s %s\n' "${target}" "$(describe_target "${target}")"
            done
            exit 0
            ;;
        -h|--help) usage; exit 0 ;;
        *) echo "不認得的選項：$1" >&2; usage >&2; exit 1 ;;
    esac
done

[ -n "${TARGETS}" ] || TARGETS="${ALL_TARGETS}"

for target in ${TARGETS}; do
    if [ -z "$(describe_target "${target}")" ]; then
        echo "不支援的平台：${target}（用 --list 看可用的）" >&2
        exit 1
    fi
done

command -v dotnet >/dev/null 2>&1 || { echo "找不到 dotnet，請先安裝 .NET 10 SDK。" >&2; exit 1; }
command -v python3 >/dev/null 2>&1 || { echo "找不到 python3（檢查 runtimeconfig.json 要用）。" >&2; exit 1; }

# --- SDK gate: at least the version pinned in global.json ----------------------------------------------------------------
# global.json has rollForward=latestPatch, so `dotnet --version` already fails when only an older patch is installed; the
# explicit comparison gives a readable message and also covers an SDK that ignores global.json (e.g. run from another directory).
REQUIRED_SDK="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([0-9][0-9.]*\)".*/\1/p' "${REPO_ROOT}/global.json" | head -n 1)"
[ -n "${REQUIRED_SDK}" ] || { echo "讀不到 global.json 的 sdk.version。" >&2; exit 1; }
# When no installed SDK satisfies global.json, `dotnet --version` fails and prints the list of installed SDKs instead of a
# version, so only a bare x.y.z(-prerelease) first line is accepted.
ACTUAL_SDK="$(cd "${REPO_ROOT}" && dotnet --version 2>/dev/null | head -n 1 || true)"
case "${ACTUAL_SDK%%-*}" in
    ""|*[!0-9.]*|*..*|.*|*.)
        echo "dotnet --version 沒有回傳可用的 SDK：" >&2
        (cd "${REPO_ROOT}" && dotnet --version 2>&1 | sed 's/^/    /' | head -n 8) >&2 || true
        echo "global.json 要求 ${REQUIRED_SDK} 或更新的修正版，請升級 .NET SDK。" >&2
        exit 1
        ;;
esac

# $1 < $2 for dotted numeric versions (pre-release suffixes are dropped).
version_lt() {
    local a="${1%%-*}" b="${2%%-*}"
    local -a x y
    IFS=. read -r -a x <<< "${a}"
    IFS=. read -r -a y <<< "${b}"
    local i
    for i in 0 1 2 3; do
        local p="${x[i]:-0}" q="${y[i]:-0}"
        if [ "${p}" -lt "${q}" ]; then return 0; fi
        if [ "${p}" -gt "${q}" ]; then return 1; fi
    done
    return 1
}

if version_lt "${ACTUAL_SDK}" "${REQUIRED_SDK}"; then
    echo "這台機器的 .NET SDK 是 ${ACTUAL_SDK}，global.json 要求至少 ${REQUIRED_SDK}；請先升級再發行。" >&2
    exit 1
fi

# The runtime a self-contained publish embeds is the one bundled with the SDK (BundledNETCoreAppPackageVersion), not whatever
# `dotnet --list-runtimes` happens to show. Read it from MSBuild and refuse to build anything below MIN_RUNTIME.
RUNTIME_VERSION="$(cd "${REPO_ROOT}" && dotnet msbuild source/Walis.Api/Walis.Api.csproj -getProperty:BundledNETCoreAppPackageVersion -nologo 2>/dev/null | tr -d '[:space:]')"
case "${RUNTIME_VERSION}" in
    [0-9]*.[0-9]*.[0-9]*) ;;
    *) echo "讀不到 SDK 內嵌的 .NET 執行階段版本（BundledNETCoreAppPackageVersion）。" >&2; exit 1 ;;
esac
if version_lt "${RUNTIME_VERSION}" "${MIN_RUNTIME}"; then
    echo "SDK ${ACTUAL_SDK} 會內嵌 .NET 執行階段 ${RUNTIME_VERSION}，低於要求的 ${MIN_RUNTIME}；請升級 SDK。" >&2
    exit 1
fi
echo "==> .NET SDK ${ACTUAL_SDK}（global.json 要求 ≥ ${REQUIRED_SDK}），發行檔內嵌執行階段 ${RUNTIME_VERSION}（要求 ≥ ${MIN_RUNTIME}）"

if [ "${BUILD_UI}" = "1" ]; then
    command -v npm >/dev/null 2>&1 || { echo "找不到 npm；不重建介面的話請加 --skip-ui。" >&2; exit 1; }
fi

# --- Signing key: checked before the long build, so a missing key fails in seconds, not after nine platforms ------------
# macOS /usr/bin/openssl is LibreSSL and cannot load Ed25519 keys; Homebrew's OpenSSL (first in PATH above) can.
OPENSSL=""
if [ "${SIGN}" = "1" ]; then
    [ -f "${PUBLIC_KEY}" ] || { echo "找不到公鑰 ${PUBLIC_KEY}。" >&2; exit 1; }
    if [ ! -f "${SIGNING_KEY}" ]; then
        echo "找不到簽章私鑰 ${SIGNING_KEY}；用 WALIS_SIGNING_KEY 指定，或只在本機測試時加 --unsigned。" >&2
        exit 1
    fi
    for candidate in "${WALIS_OPENSSL:-}" openssl /opt/homebrew/opt/openssl@3/bin/openssl /usr/local/opt/openssl@3/bin/openssl; do
        [ -n "${candidate}" ] || continue
        if command -v "${candidate}" >/dev/null 2>&1 && "${candidate}" pkey -pubin -in "${PUBLIC_KEY}" -noout >/dev/null 2>&1; then
            OPENSSL="$(command -v "${candidate}")"
            break
        fi
    done
    [ -n "${OPENSSL}" ] || { echo "找不到支援 Ed25519 的 openssl（macOS 內建的是 LibreSSL；brew install openssl@3）。" >&2; exit 1; }
    # The private key must be the one the committed public key belongs to, otherwise every installer would reject the release.
    DERIVED="$("${OPENSSL}" pkey -in "${SIGNING_KEY}" -pubout 2>/dev/null | tr -d '\n')"
    [ "${DERIVED}" = "$(tr -d '\n' < "${PUBLIC_KEY}")" ] ||
        { echo "私鑰 ${SIGNING_KEY} 與 ${PUBLIC_KEY} 不是同一對金鑰。" >&2; exit 1; }
fi

if [ -z "${VERSION}" ]; then
    VERSION="$(git -C "${REPO_ROOT}" describe --tags --always --dirty 2>/dev/null || true)"
    [ -n "${VERSION}" ] || VERSION="$(date +%Y%m%d)"
fi

# VERSION goes into both the staging path and the output file name; accepting ../ would let the later rm -rf stage escape
# the random staging root. Even though the version usually comes from a git tag, CLI/CI input must still be validated independently.
case "${VERSION}" in
    ""|.|..|*[!A-Za-z0-9._+-]*)
        echo "版本只能包含 ASCII 英數、點、底線、加號與連字號。" >&2
        exit 1
        ;;
esac
[ "${#VERSION}" -le 100 ] || { echo "版本字串不可超過 100 個字元。" >&2; exit 1; }

# MSBuild's Version only accepts the 1.2.3.4 form, which git describe output usually is not.
ASSEMBLY_VERSION_ARG=""
NUMERIC_VERSION="${VERSION#v}"
NUMERIC_VERSION="${NUMERIC_VERSION%%-*}"
case "${NUMERIC_VERSION}" in
    *[!0-9.]*|"") ;;
    *) ASSEMBLY_VERSION_ARG="-p:Version=${NUMERIC_VERSION}" ;;
esac

STAGING="$(mktemp -d "${TMPDIR:-/tmp}/walis-release-XXXXXX")"
trap 'rm -rf "${STAGING}"' EXIT

mkdir -p "${OUTPUT}"
# Packing changes directory before writing the archive, so a relative --output has to be made absolute first.
OUTPUT="$(cd "${OUTPUT}" && pwd)"

# The admin UI is built once and the same copy goes into every platform's API.
UI_DIST="${REPO_ROOT}/source/Walis.Admin/dist"

if [ "${BUILD_API}" = "1" ] && [ "${BUILD_UI}" = "1" ]; then
    echo "==> 建置管理介面"
    # Always npm ci: it installs exactly package-lock.json and fails if package.json disagrees, whereas npm install may
    # resolve something newer and rewrite the lock file in the middle of a release.
    npm --prefix "${REPO_ROOT}/source/Walis.Admin" ci --no-audit --no-fund
    npm --prefix "${REPO_ROOT}/source/Walis.Admin" run build
fi

if [ "${BUILD_API}" = "1" ] && [ ! -d "${UI_DIST}" ]; then
    echo "找不到管理介面的建置產物 ${UI_DIST}，請拿掉 --skip-ui。" >&2
    exit 1
fi

# Pack into archives: zip for Windows, tar.gz for the rest. The result goes into PACKED
# rather than being returned via $(...), which would swallow dotnet's error messages and hide failures.
PACKED=""

pack() {
    local source_dir="$1"
    local name="$2"
    local target="$3"

    case "${target}" in
        win-*)
            local archive="${OUTPUT}/${name}.zip"
            rm -f "${archive}"
            if command -v zip >/dev/null 2>&1; then
                (cd "$(dirname "${source_dir}")" && zip -rq "${archive}" "$(basename "${source_dir}")")
            else
                python3 - "${source_dir}" "${archive}" <<'ZIP'
import pathlib
import shutil
import sys

source = pathlib.Path(sys.argv[1])
archive = pathlib.Path(sys.argv[2])
shutil.make_archive(str(archive.with_suffix('')), 'zip', source.parent, source.name)
ZIP
            fi
            PACKED="${archive}"
            ;;
        *)
            local archive="${OUTPUT}/${name}.tar.gz"
            rm -f "${archive}"
            # macOS tar otherwise stores AppleDouble entries (._name) and xattr pax headers; GNU tar lists the former as a
            # top-level file, which the installer rightly refuses, and warns about the latter.
            COPYFILE_DISABLE=1 tar --no-xattrs --no-mac-metadata -czf "${archive}" -C "$(dirname "${source_dir}")" "$(basename "${source_dir}")"
            PACKED="${archive}"
            ;;
    esac
}

publish() {
    local project="$1"
    local target="$2"
    local destination="$3"

    # DebugType=none: releases do not need pdb files, saving almost a third of the size.
    # RestoreLockedMode: the restore must reproduce packages.lock.json exactly (NU1004 otherwise), so a release never ships a
    # dependency graph that differs from the committed one.
    dotnet publish "${project}" \
        -c Release \
        -r "${target}" \
        --self-contained true \
        -p:RestoreLockedMode=true \
        -p:UseAppHost=true \
        -p:DebugType=none \
        -p:DebugSymbols=false \
        -p:InformationalVersion="${VERSION}" \
        ${ASSEMBLY_VERSION_ARG} \
        -o "${destination}" \
        --nologo \
        -v quiet
}

# Reads <stage>/<name>.runtimeconfig.json and fails unless every embedded framework is at least MIN_RUNTIME.
# The versions of the first checked file are also recorded for RUNTIME.txt.
RUNTIME_FRAMEWORKS=""
check_runtime() {
    local config="$1"
    local found
    [ -f "${config}" ] || { echo "找不到 ${config}，發行檔不是自含式的。" >&2; exit 1; }
    found="$(MIN_RUNTIME="${MIN_RUNTIME}" python3 - "${config}" <<'PY'
import json, os, sys

minimum = tuple(int(part) for part in os.environ["MIN_RUNTIME"].split("."))
with open(sys.argv[1], encoding="utf-8") as handle:
    options = json.load(handle)["runtimeOptions"]
frameworks = options.get("includedFrameworks")
if not frameworks:
    sys.stderr.write(f"{sys.argv[1]}: 沒有 includedFrameworks（框架相依，不是自含式）\n")
    sys.exit(1)
for framework in frameworks:
    version = tuple(int(part) for part in framework["version"].split("-")[0].split("."))
    if version < minimum:
        sys.stderr.write(f"{sys.argv[1]}: {framework['name']} {framework['version']} 低於要求的 {os.environ['MIN_RUNTIME']}\n")
        sys.exit(1)
    print(f"{framework['name']} {framework['version']}")
PY
)"
    [ -n "${RUNTIME_FRAMEWORKS}" ] || RUNTIME_FRAMEWORKS="${found}"
}

build_api() {
    local target="$1"
    local name="walis-${VERSION}-${target}"
    local stage="${STAGING}/${name}"

    echo "==> API：${target}（$(describe_target "${target}")）"
    rm -rf "${stage}"
    publish "${REPO_ROOT}/source/Walis.Api/Walis.Api.csproj" "${target}" "${stage}"

    mkdir -p "${stage}/wwwroot"
    cp -R "${UI_DIST}/." "${stage}/wwwroot/"

    # The MCP bridge is for clients that only support stdio; the API's own /mcp does not need it.
    # It is published self-contained like everything else: a framework-dependent copy would run on whatever .NET the user's
    # machine has, and the runtime version in the archive would no longer be the publisher's to vouch for.
    publish "${REPO_ROOT}/source/Walis.Mcp/Walis.Mcp.csproj" "${target}" "${stage}/mcp"

    check_runtime "${stage}/Walis.Api.runtimeconfig.json"
    check_runtime "${stage}/mcp/Walis.Mcp.runtimeconfig.json"
    write_readme "${stage}" "${target}" "api"
    pack "${stage}" "${name}" "${target}"
    BUILT="${BUILT} ${PACKED}"
}

build_runner() {
    local target="$1"
    local name="walis-runner-${VERSION}-${target}"
    local stage="${STAGING}/${name}"

    echo "==> 執行器：${target}（$(describe_target "${target}")）"
    rm -rf "${stage}"
    publish "${REPO_ROOT}/source/Walis.Runner/Walis.Runner.csproj" "${target}" "${stage}"

    check_runtime "${stage}/walis-runner.runtimeconfig.json"
    write_readme "${stage}" "${target}" "runner"
    pack "${stage}" "${name}" "${target}"
    BUILT="${BUILT} ${PACKED}"
}

write_readme() {
    local stage="$1"
    local target="$2"
    local kind="$3"
    local start="./Walis.Api"

    case "${target}" in
        win-*) start=".\\Walis.Api.exe" ;;
    esac

    if [ "${kind}" = "runner" ]; then
        case "${target}" in
            win-*) start=".\\walis-runner.exe --server http://walis.internal:11120 --name builder-01 --token-file .\\runner.token" ;;
            *) start="./walis-runner --server http://walis.internal:11120 --name builder-01 --token-file ./runner.token" ;;
        esac
    fi

    # The same guide the installer points at: where the setup code is, and what to tighten before exposing the server.
    local guide="https://github.com/walisayu/walis/blob/main/docs/self-hosting.md"

    {
        echo "Walis ${VERSION} — $(describe_target "${target}")"
        echo "內嵌 .NET 執行階段 ${RUNTIME_VERSION}（完整清單見發行頁的 RUNTIME.txt）"
        echo
        if [ "${kind}" = "api" ]; then
            echo "這是 API 與管理介面，已包含 .NET 執行環境，目標機器不需要另外安裝。"
            echo
            echo "啟動："
            echo
            echo "    ${start} --urls http://0.0.0.0:11120"
            echo
            echo "然後用瀏覽器開 http://<這台機器>:11120，建立第一位管理員。"
            echo "從其他機器設定時要輸入設定碼：啟動時會印在主控台，也存在 data/setup-code.txt。"
            echo
            echo "對外開放前該收緊的設定（HTTPS、主機名稱、建置隔離）見："
            echo "${guide}"
            echo
            echo "資料預設放在執行檔旁邊的 data/；要換成 PostgreSQL、MySQL 或 SQL Server，"
            echo "設定 Database__Provider 與 ConnectionStrings__Walis 這兩個環境變數即可。"
            echo
            echo "mcp/ 是給只支援 stdio 的 AI 用戶端的轉接程式，同樣已包含 .NET 執行環境；"
            echo "API 本身的 /mcp 端點不需要它。"
            echo
            echo "驗證下載：發行頁的 SHA256SUMS.txt 以 Ed25519 簽章（SHA256SUMS.txt.sig），公鑰在"
            echo "原始碼的 deploy/release-signing.pub，指紋與驗證步驟見 README 的「Verifying downloads」。"
        else
            echo "這是遠端執行器，已包含 .NET 執行環境，目標機器不需要另外安裝。"
            echo
            echo "啟動："
            echo
            echo "    ${start}"
            echo
            echo "它會向 API 認領指派給這個名稱的建置，並回報結果。"
            echo "<金鑰> 是在管理介面的「帳號管理 → API 金鑰」建立、綁定這個執行器名稱的金鑰。"
            echo "伺服器有 HTTPS 之後，請改用 https:// 的位址。詳細說明見："
            echo "${guide}#remote-runners"
        fi
        echo
        echo "文件：https://walis.io"
    } > "${stage}/README.txt"

}

BUILT=""

for target in ${TARGETS}; do
    if [ "${BUILD_API}" = "1" ]; then
        build_api "${target}"
    fi

    if [ "${BUILD_RUNNER}" = "1" ]; then
        build_runner "${target}"
    fi
done

echo "==> 產生官方發行物料清單（SBOM）"
SBOM_FILE="${OUTPUT}/walis-${VERSION}.cdx.json"
# Scan only the current release's staging tree for runtimes. Scanning the repository would mix old dist/bin artifacts
# into the release inventory and can misreport both patched and vulnerable runtime versions.
COMMIT_SHA="$(git -C "${REPO_ROOT}" rev-parse HEAD 2>/dev/null || echo "unknown")"
python3 -I "${REPO_ROOT}/deploy/generate-sbom.py" "${REPO_ROOT}" "${COMMIT_SHA}" "${SBOM_FILE}" "${VERSION}" "${STAGING}"

echo "==> 計算校驗碼"
CHECKSUMS="${OUTPUT}/SHA256SUMS.txt"
: > "${CHECKSUMS}"

for archive in ${BUILT}; do
    if command -v shasum >/dev/null 2>&1; then
        (cd "${OUTPUT}" && shasum -a 256 "$(basename "${archive}")" >> "${CHECKSUMS}")
    else
        (cd "${OUTPUT}" && sha256sum "$(basename "${archive}")" >> "${CHECKSUMS}")
    fi
done

if [ -f "${SBOM_FILE:-}" ]; then
    if command -v shasum >/dev/null 2>&1; then
        (cd "${OUTPUT}" && shasum -a 256 "$(basename "${SBOM_FILE}")" >> "${CHECKSUMS}")
    else
        (cd "${OUTPUT}" && sha256sum "$(basename "${SBOM_FILE}")" >> "${CHECKSUMS}")
    fi
fi

# RUNTIME.txt tells readers (and internal/release.sh, which copies it into the release notes) which .NET runtime the archives
# embed, so a servicing gap like v1.0.1's 10.0.10 is visible without unpacking anything.
RUNTIME_FILE="${OUTPUT}/RUNTIME.txt"
{
    echo "Walis ${VERSION}"
    echo "SDK ${ACTUAL_SDK}"
    echo "${RUNTIME_FRAMEWORKS}"
} > "${RUNTIME_FILE}"

if [ "${SIGN}" = "1" ]; then
    echo "==> 簽章 SHA256SUMS.txt"
    SIGNATURE="${CHECKSUMS}.sig"
    rm -f "${SIGNATURE}"
    "${OPENSSL}" pkeyutl -sign -inkey "${SIGNING_KEY}" -rawin -in "${CHECKSUMS}" -out "${SIGNATURE}"
    # Verify with the committed public key, exactly as get-walis.sh will.
    "${OPENSSL}" pkeyutl -verify -pubin -inkey "${PUBLIC_KEY}" -rawin -in "${CHECKSUMS}" -sigfile "${SIGNATURE}" >/dev/null ||
        { echo "簽章後用 ${PUBLIC_KEY} 驗證失敗。" >&2; exit 1; }

    # latest.json tells deploy/get-walis.sh which version "latest" is; it is signed too, so the release host cannot point
    # new installs at an older (still validly signed) release. Upload it together with the archives.
    echo "==> 產生並簽章 latest.json"
    LATEST="${OUTPUT}/latest.json"
    printf '{"tag_name":"%s","released_at":"%s"}\n' "${VERSION}" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "${LATEST}"
    rm -f "${LATEST}.sig"
    "${OPENSSL}" pkeyutl -sign -inkey "${SIGNING_KEY}" -rawin -in "${LATEST}" -out "${LATEST}.sig"
    "${OPENSSL}" pkeyutl -verify -pubin -inkey "${PUBLIC_KEY}" -rawin -in "${LATEST}" -sigfile "${LATEST}.sig" >/dev/null ||
        { echo "latest.json 簽章後驗證失敗。" >&2; exit 1; }
else
    echo "==> 未簽章（--unsigned）：這份輸出不能發佈，deploy/get-walis.sh 會拒絕沒有簽章的來源。"
fi

echo
echo "==> 完成，輸出在 ${OUTPUT}"
for archive in ${BUILT}; do
    printf '  %-46s %s\n' "$(basename "${archive}")" "$(du -h "${archive}" | cut -f1 | tr -d ' ')"
done
if [ -f "${SBOM_FILE:-}" ]; then
    printf '  %-46s %s\n' "$(basename "${SBOM_FILE}")" "$(du -h "${SBOM_FILE}" | cut -f1 | tr -d ' ')"
fi
echo "  SHA256SUMS.txt"
if [ "${SIGN}" = "1" ]; then echo "  SHA256SUMS.txt.sig"; echo "  latest.json + latest.json.sig"; fi
echo "  RUNTIME.txt"
sed 's/^/    /' "${RUNTIME_FILE}"
