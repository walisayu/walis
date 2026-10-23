#!/usr/bin/env bash
# Checks the scripts under deploy. Run with: ./deploy/lint.sh
#
# Besides syntax, it specifically blocks a pattern this project has tripped over twice: a CJK character directly after a variable.
# The bash 3.2 bundled with macOS, under zh_TW.UTF-8, merges the first byte of the CJK character into the variable name:
#
#   $ LANG=zh_TW.UTF-8 /bin/bash -c 'set -u; A=x; echo "$A（y）"'
#   /bin/bash: A\xef: unbound variable
#
# So in that position always write ${VAR}.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FAILED=0

while IFS= read -r script; do
    if ! bash -n "$script"; then
        echo "語法錯誤：$script" >&2
        FAILED=1
    fi
done < <(find "$ROOT" "$ROOT/../internal" -name '*.sh')

# The online https://walis.io/install.sh is a copy of get-walis.sh kept in the website repo; if a change here is not synced over,
# the old version keeps being served. The two repos are independent, so this compares only when WALIS_WEBSITE_ROOT names the website repo and is skipped otherwise.
if ! "$ROOT/sync-install-sh.sh" --check; then
    FAILED=1
fi

# BSD grep's character classes do not accept \x00-\x7f, so this part is checked with python.
if ! python3 - "$ROOT" <<'CHECK'
import pathlib
import re
import sys

pattern = re.compile(r'\$[A-Za-z_][A-Za-z0-9_]*(?=[^\x00-\x7f])')
bad = False

for script in sorted(pathlib.Path(sys.argv[1]).rglob('*.sh')):
    for number, line in enumerate(script.read_text(encoding='utf-8').splitlines(), 1):
        if line.lstrip().startswith('#'):
            continue
        for match in pattern.finditer(line):
            bad = True
            print(f'{script}:{number}: {match.group(0)} 後面直接接了非 ASCII 字元，請改成 ${{{match.group(0)[1:]}}}')

sys.exit(1 if bad else 0)
CHECK
then
    FAILED=1
fi

# These inputs must be rejected before any root file operation, so even when lint runs as CI's root
# account it never creates, changes or deletes files.
expect_rejected() {
    description="$1"
    expected="$2"
    shift 2

    if output="$("$@" 2>&1)"; then
        echo "安全測試失敗（竟然接受輸入）：$description" >&2
        FAILED=1
        return
    fi

    case "$output" in
        *"$expected"*) ;;
        *)
            echo "安全測試失敗（錯誤原因不符）：$description" >&2
            echo "$output" >&2
            FAILED=1
            ;;
    esac
}

expect_rejected "install 空白 prefix" "不可為空白" \
    env WALIS_PREFIX= WALIS_PORT=11120 /bin/bash "$ROOT/install.sh" --no-build
expect_rejected "install 根目錄 prefix" "根目錄" \
    env WALIS_PREFIX=/ WALIS_PORT=11120 /bin/bash "$ROOT/install.sh" --no-build
expect_rejected "install 含空白 prefix" "只能包含" \
    env "WALIS_PREFIX=/tmp/walis unsafe" WALIS_PORT=11120 /bin/bash "$ROOT/install.sh" --no-build
expect_rejected "install 路徑穿越" "不可包含" \
    env WALIS_PREFIX=/tmp/walis/../walis WALIS_PORT=11120 /bin/bash "$ROOT/install.sh" --no-build
expect_rejected "install 連接埠零" "必須介於" \
    env WALIS_PREFIX=/tmp/walis-lint WALIS_PORT=0 /bin/bash "$ROOT/install.sh" --no-build
expect_rejected "install 連接埠過大" "必須介於" \
    env WALIS_PREFIX=/tmp/walis-lint WALIS_PORT=65536 /bin/bash "$ROOT/install.sh" --no-build
expect_rejected "install 缺少連接埠參數" "缺少數值" \
    /bin/bash "$ROOT/install.sh" --port
expect_rejected "uninstall 根目錄 purge" "根目錄" \
    env WALIS_PREFIX=/ /bin/bash "$ROOT/uninstall.sh" --purge
expect_rejected "uninstall 含空白 prefix" "只能包含" \
    env "WALIS_PREFIX=/tmp/walis unsafe" /bin/bash "$ROOT/uninstall.sh" --purge
expect_rejected "uninstall 不具產品辨識的 prefix" "必須包含 walis" \
    env WALIS_PREFIX=/tmp/application /bin/bash "$ROOT/uninstall.sh" --purge
expect_rejected "uninstall 缺少 prefix 參數" "缺少路徑" \
    /bin/bash "$ROOT/uninstall.sh" --prefix
expect_rejected "get-walis 版本路徑穿越" "版本只能包含" \
    /bin/sh "$ROOT/get-walis.sh" --version ../escape
expect_rejected "get-walis repo 路徑穿越" "repo owner只能包含" \
    /bin/sh "$ROOT/get-walis.sh" --repo ../walis --version v1
expect_rejected "get-walis 相對安裝路徑" "安裝位置必須是絕對路徑" \
    /bin/sh "$ROOT/get-walis.sh" --version v1 --prefix relative/walis
expect_rejected "release 版本路徑穿越" "版本只能包含" \
    /bin/bash "$ROOT/release.sh" --runner-only --targets linux-x64 --version ../escape

if ! grep -q '^DynamicUser=yes$' "$ROOT/get-walis.sh" ||
   ! grep -q '找不到 sha256sum 或 shasum' "$ROOT/get-walis.sh"; then
    echo "安全測試失敗：get-walis 必須以 DynamicUser 啟動服務，且不得略過下載校驗。" >&2
    FAILED=1
fi

# The installer must verify the Ed25519 signature on SHA256SUMS.txt with the committed public key before trusting a checksum,
# and the key embedded in the script must be the one in deploy/release-signing.pub.
if ! grep -q 'Signature Verified Successfully' "$ROOT/get-walis.sh" ||
   ! grep -q 'SHA256SUMS.txt.sig' "$ROOT/get-walis.sh"; then
    echo "安全測試失敗：get-walis 必須驗證 SHA256SUMS.txt.sig 才比對校驗碼。" >&2
    FAILED=1
fi
embedded_key="$(sed -n '/^RELEASE_PUBLIC_KEY=/,/END PUBLIC KEY/p' "$ROOT/get-walis.sh" | grep -E '^[A-Za-z0-9+/=]+$' | tr -d '\n')"
committed_key="$(grep -v -- '-----' "$ROOT/release-signing.pub" | tr -d '\n')"
if [ -z "$embedded_key" ] || [ "$embedded_key" != "$committed_key" ]; then
    echo "安全測試失敗：get-walis 內嵌的公鑰與 deploy/release-signing.pub 不一致。" >&2
    FAILED=1
fi
# release.sh must refuse to pack a runtime older than its MIN_RUNTIME and must not fall back to npm install.
if ! grep -q '^MIN_RUNTIME="10\.0\.[0-9]*"$' "$ROOT/release.sh" ||
   grep -q 'npm --prefix .* install' "$ROOT/release.sh" "$ROOT/build-release.sh"; then
    echo "安全測試失敗：release.sh 必須宣告 MIN_RUNTIME，且發行腳本只能用 npm ci。" >&2
    FAILED=1
fi

# Credentials belong in a root-only EnvironmentFile; an Environment= line in the unit is readable through systemctl show.
if grep -q 'Environment="WALIS_TOKEN' "$ROOT/get-walis.sh" ||
   ! grep -q 'EnvironmentFile=${ENV_DIR}/runner.env' "$ROOT/get-walis.sh" ||
   ! grep -q '^SystemCallFilter=@system-service$' "$ROOT/get-walis.sh" ||
   ! grep -q '^Environment=DOTNET_EnableDiagnostics=0$' "$ROOT/get-walis.sh"; then
    echo "安全測試失敗：get-walis 的 unit 必須用 EnvironmentFile 傳權杖，並保留 systemd 硬化指令。" >&2
    FAILED=1
fi

# The service account must never own the executable the service restarts into: install.sh keeps releases/ and current root-owned,
# and the self-update job builds into the staging area the restarter promotes.
if ! grep -q 'install -d -o root -g wheel -m 755 "$RELEASES_DIR"' "$ROOT/install.sh" ||
   grep -q 'ln -sfn "$RELEASE" "$PREFIX/current"' "$ROOT/update.sh" ||
   ! grep -q 'WALIS_PROMOTE_ONLY' "$ROOT/apply-restart.sh" ||
   ! grep -q '"trustedUnsandboxed": True' "$ROOT/register-job.sh" ||
   ! grep -q 'DOTNET_EnableDiagnostics' "$ROOT/install.sh"; then
    echo "安全測試失敗：releases/current 必須由 root 擁有、update.sh 不得切換 current、自我更新任務必須是信任任務。" >&2
    FAILED=1
fi

xml_escape_definition="$(sed -n '/^xml_escape() {$/,/^}$/p' "$ROOT/install.sh")"
escaped_xml="$(/bin/bash -c 'eval "$1"; xml_escape "$2"' \
    bash "$xml_escape_definition" "a&<>\"'")"
if [ "$escaped_xml" != 'a&amp;&lt;&gt;&quot;&apos;' ]; then
    echo "安全測試失敗：plist XML escape 結果不正確：$escaped_xml" >&2
    FAILED=1
fi

if [ "$FAILED" = "0" ]; then
    echo "deploy 底下的腳本檢查通過"
fi

exit "$FAILED"
