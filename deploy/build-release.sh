#!/usr/bin/env bash
# Builds a new release into the service-writable staging area $PREFIX/var/staging/<timestamp> and prints its path on the last line.
# This script runs as the service user and needs no sudo. $PREFIX/releases and $PREFIX/current are root-owned: the staged
# release is validated and moved there by root (install.sh, or apply-restart.sh after the self-update job files a restart request),
# so a build script can never replace the executable the service restarts into.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PREFIX="${WALIS_PREFIX:-/usr/local/walis}"
RELEASES="${WALIS_STAGING:-$PREFIX/var/staging}"

export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"

case "$(uname -m)" in
    arm64) RID="osx-arm64" ;;
    x86_64) RID="osx-x64" ;;
    *) echo "不支援的架構：$(uname -m)" >&2; exit 1 ;;
esac

for tool in dotnet npm; do
    command -v "$tool" >/dev/null 2>&1 || { echo "找不到 ${tool}，請先安裝。" >&2; exit 1; }
done

mkdir -p "$RELEASES"
chmod 700 "$RELEASES"

RELEASE="$RELEASES/$(date +%Y%m%d-%H%M%S)"
SUFFIX=1
while [ -e "$RELEASE" ]; do
    RELEASE="$RELEASES/$(date +%Y%m%d-%H%M%S)-$SUFFIX"
    SUFFIX=$((SUFFIX + 1))
done
mkdir -p "$RELEASE"

echo "==> 發行 API（${RID}，self-contained）到暫存區 $RELEASE"
# RestoreLockedMode: the restore must match packages.lock.json (same rule as deploy/release.sh).
dotnet publish "$REPO_ROOT/source/Walis.Api/Walis.Api.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:RestoreLockedMode=true \
    -p:UseAppHost=true \
    -o "$RELEASE"

echo "==> 發行 MCP server 到 ${RELEASE}/mcp"
# The MCP server for AI agents ships with the service so its path stays stable (current/mcp).
# Self-contained like the API, so it runs on the runtime this release was built with rather than whatever the host has.
dotnet publish "$REPO_ROOT/source/Walis.Mcp/Walis.Mcp.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:RestoreLockedMode=true \
    -o "$RELEASE/mcp"

echo "==> 建置管理介面"
# Always npm ci: it installs exactly package-lock.json; npm install could resolve newer versions and rewrite the lock file.
npm --prefix "$REPO_ROOT/source/Walis.Admin" ci --no-audit --no-fund
npm --prefix "$REPO_ROOT/source/Walis.Admin" run build

rm -rf "$RELEASE/wwwroot"
mkdir -p "$RELEASE/wwwroot"
cp -R "$REPO_ROOT/source/Walis.Admin/dist/." "$RELEASE/wwwroot/"

# Artifacts, cache and backups default to ContentRoot/data, but the release changes every time, so the data directory
# is set to the shared $PREFIX/data. It must be written into the settings and cannot be a symbolic link: the private-directory check at startup rejects links.
rm -rf "$RELEASE/data"
RELEASE="$RELEASE" PREFIX="$PREFIX" python3 - <<'PY'
import json, os
path = os.path.join(os.environ["RELEASE"], "appsettings.json")
with open(path, encoding="utf-8") as handle:
    settings = json.load(handle)
settings.setdefault("Walis", {})["DataDirectory"] = os.path.join(os.environ["PREFIX"], "data")
# Action packs live outside the data directory: the data directory is read-only inside the build sandbox, and the "action pack update" job must be able to git pull there.
settings["Walis"]["ActionPacks"] = [os.path.join(os.environ["PREFIX"], "action-packs")]
with open(path, "w", encoding="utf-8") as handle:
    json.dump(settings, handle, ensure_ascii=False, indent=2)
    handle.write("\n")
PY

echo "==> 完成"
echo "$RELEASE"
