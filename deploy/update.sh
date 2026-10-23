#!/usr/bin/env bash
# The script run by the Walis "self-update" job: pulls the latest code, builds a new release into the staging area
# $PREFIX/var/staging, and leaves a restart request for walis-restarter. The restarter (root, outside the service's process tree)
# validates the staged release, moves it into the root-owned $PREFIX/releases, switches current and restarts the service.
#
# This script is run by Walis itself, so it must never restart the service directly; and because $PREFIX/releases and
# $PREFIX/current are root-owned, it could not switch current even if it tried. The job must be registered as a trusted job
# (register-job.sh does): the build writes $PREFIX/var, which the sandbox denies to ordinary jobs.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PREFIX="${WALIS_PREFIX:-/usr/local/walis}"

export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"

# Walis points HOME at the workspace (this repo) while running a build, so dotnet and npm would write their NuGet
# and npm caches into the repo, hundreds of MB at a time. Point it at a fixed directory outside the release so the caches are reused across runs.
export HOME="${WALIS_BUILD_HOME:-$PREFIX/var/home}"
mkdir -p "$HOME"

cd "$REPO_ROOT"

echo "==> 儲存庫：${REPO_ROOT}（分支 $(git rev-parse --abbrev-ref HEAD)）"

# HOME inside the build is changed by Walis to the workspace, so git cannot find the service account's keychain; HTTPS
# credentials therefore come from the job's secret environment variables: WALIS_GIT_TOKEN (a Gitea access token) and the optional
# WALIS_GIT_USER (defaults to the running account). When unset, git's own configuration is used as before.
if [ -n "${WALIS_GIT_TOKEN:-}" ]; then
    export GIT_CONFIG_COUNT=2
    export GIT_CONFIG_KEY_0="credential.helper" GIT_CONFIG_VALUE_0=""
    export GIT_CONFIG_KEY_1="credential.helper"
    export GIT_CONFIG_VALUE_1='!f() { printf "username=%s\npassword=%s\n" "${WALIS_GIT_USER:-$USER}" "$WALIS_GIT_TOKEN"; }; f'
fi
export GIT_TERMINAL_PROMPT=0

if [ "${WALIS_SKIP_GIT_PULL:-0}" = "1" ]; then
    echo "    WALIS_SKIP_GIT_PULL=1，略過 git pull"
elif git rev-parse --abbrev-ref --symbolic-full-name '@{u}' >/dev/null 2>&1; then
    git pull --ff-only
else
    echo "    目前分支沒有上游，略過 git pull"
fi

echo "==> 目前版本：$(git log -1 --pretty='%h %s')"

BUILD_LOG="$(mktemp -t walis-build)"
trap 'rm -f "$BUILD_LOG"' EXIT

if ! WALIS_PREFIX="$PREFIX" "$REPO_ROOT/deploy/build-release.sh" > "$BUILD_LOG" 2>&1; then
    cat "$BUILD_LOG"
    echo "建置失敗，沒有送出重啟要求，服務維持在舊版本。" >&2
    exit 1
fi

cat "$BUILD_LOG"
RELEASE="$(tail -n 1 "$BUILD_LOG")"

if [ ! -x "$RELEASE/Walis.Api" ]; then
    echo "建置輸出不正確：$RELEASE" >&2
    exit 1
fi

# Older staged builds that were never promoted (a failed restart, a build that raced another) only waste disk; the restarter
# promotes the newest one, so everything else can go.
find "$PREFIX/var/staging" -mindepth 1 -maxdepth 1 -type d ! -path "$RELEASE" -exec rm -rf {} +

PREVIOUS="$(readlink "$PREFIX/current" 2>/dev/null || echo '（無）')"
echo "==> 已建置到暫存區：${RELEASE}（目前的 current：${PREVIOUS}）"

touch "$PREFIX/var/restart.request"
echo "==> 已送出重啟要求；walis-restarter 會驗證暫存的版本、搬進 releases、切換 current，再於幾秒後以新版本重新啟動。"
