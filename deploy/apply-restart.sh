#!/bin/bash
# Run by the com.walisayu.walis-restarter LaunchDaemon (root).
#
# The self-update build is itself a child process of the Walis service; if it restarted the service itself,
# it would be killed together with the service before the build result is written back to the database. So instead
# the build only touches a trigger file, and this script, outside the service's process tree, does the real restart a few seconds later.
#
# The build also cannot install what it built: $PREFIX/releases and $PREFIX/current are root-owned. The build leaves the new
# release in the service-writable $PREFIX/var/staging; this script validates it, moves it into releases (root-owned from then on),
# switches current and prunes old releases. A staged release that fails validation is deleted and the service restarts unchanged.
#
#   apply-restart.sh <prefix> <label> <state dir>        (the daemon)
#   WALIS_PROMOTE_ONLY=1 apply-restart.sh <prefix> <label> <state dir>
#                                                        (install.sh: promote the staged release, no restart)
set -euo pipefail

PREFIX="${1:-/usr/local/walis}"
LABEL="${2:-com.walisayu.walis}"
STATE_DIR="${3:-/var/db/walis-restarter}"
DELAY="${WALIS_RESTART_DELAY:-8}"
KEEP_RELEASES=3
umask 077

TRIGGER="$PREFIX/var/restart.request"
STAMP="$STATE_DIR/restart.applied"
STAGING="$PREFIX/var/staging"
RELEASES="$PREFIX/releases"

log() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] $1"
}

die() {
    log "$1" >&2
    exit 1
}

# Every path root operates on must be a real directory owned by root that nobody else can write, all the way up; otherwise the
# service account could swap a component for a symbolic link between the check and the operation.
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

state_type="$(stat -f %HT "$STATE_DIR" 2>/dev/null || true)"
state_uid="$(stat -f %u "$STATE_DIR" 2>/dev/null || echo -1)"
state_mode="$(stat -f %OLp "$STATE_DIR" 2>/dev/null || echo 0)"
if [ "$state_type" != "Directory" ] || [ "$state_uid" != "0" ] || [ $((8#$state_mode & 0022)) -ne 0 ]; then
    echo "不安全的 restarter state 目錄：$STATE_DIR" >&2
    exit 1
fi

# The service UID is recorded by install.sh in the root-only state directory; a staged release must be owned by it.
service_uid_record="$STATE_DIR/service-uid"
[ -f "$service_uid_record" ] && [ ! -L "$service_uid_record" ] ||
    die "缺少服務 UID 紀錄：${service_uid_record}（請重跑 sudo ./deploy/install.sh）"
[ "$(stat -f %u "$service_uid_record")" = "0" ] || die "服務 UID 紀錄不是 root 擁有：$service_uid_record"
SERVICE_UID="$(< "$service_uid_record")"
case "$SERVICE_UID" in
    ''|*[!0-9]*) die "服務 UID 紀錄無效：$SERVICE_UID" ;;
esac
[ "${#SERVICE_UID}" -le 10 ] || die "服務 UID 紀錄過大。"
[ "$SERVICE_UID" -ne 0 ] || die "服務 UID 不可為 0。"

# Picks the newest staged release by name; empty when there is nothing to promote. Only the name is taken from the
# service-writable directory, and it must look like what build-release.sh produces.
find_staged_release() {
    STAGED=""
    [ -d "$STAGING" ] && [ ! -L "$STAGING" ] || return 0

    local candidate name
    while IFS= read -r candidate; do
        name="$(basename "$candidate")"
        case "$name" in
            [0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]|\
            [0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]-[0-9]*) ;;
            *) continue ;;
        esac
        [ -d "$candidate" ] && [ ! -L "$candidate" ] || continue
        STAGED="$candidate"
    done < <(find "$STAGING" -mindepth 1 -maxdepth 1 2>/dev/null | sort)
}

# Moves the staged release into the root-owned releases directory and makes it root-owned and read-only for everyone else.
# Order matters: the move happens first so the service account can no longer swap the top-level entry, the ownership change
# second so it can no longer touch the contents, and the checks last so a link planted during the window still gets caught.
promote_release() {
    local staged="$1"
    local name target
    name="$(basename "$staged")"
    target="$RELEASES/$name"

    validate_root_only_ancestry "$RELEASES"
    [ ! -e "$target" ] && [ ! -L "$target" ] || die "releases 裡已經有同名版本，拒絕覆蓋：$target"

    [ -d "$staged" ] && [ ! -L "$staged" ] || die "暫存的版本必須是實體目錄：$staged"
    [ "$(stat -f %u "$staged")" = "$SERVICE_UID" ] || die "暫存的版本不是服務帳號擁有，拒絕採用：$staged"
    [ -f "$staged/Walis.Api" ] && [ ! -L "$staged/Walis.Api" ] && [ -x "$staged/Walis.Api" ] ||
        die "暫存的版本缺少可執行的 Walis.Api：$staged"
    if [ -n "$(find "$staged" -type l -print -quit 2>/dev/null)" ]; then
        die "暫存的版本含有符號連結，拒絕採用：$staged"
    fi

    mv "$staged" "$target"
    chown -R root:wheel "$target"
    chmod -R go-w "$target"
    chmod 755 "$target"

    # Re-check after the tree is root-owned: nothing may have been swapped during the window above.
    [ -d "$target" ] && [ ! -L "$target" ] || die "版本目錄在搬移後發生變化：$target"
    if [ -n "$(find "$target" -type l -print -quit)" ] || [ -n "$(find "$target" ! -user root -print -quit)" ]; then
        rm -rf "$target"
        die "版本目錄在搬移期間被動過，已移除：$target"
    fi
    [ -f "$target/Walis.Api" ] && [ -x "$target/Walis.Api" ] || { rm -rf "$target"; die "搬移後缺少 Walis.Api：$target"; }

    PROMOTED="$target"
}

# current is replaced with rename(2) so there is never a moment without it; PREFIX is root-owned, so the temporary link cannot be swapped.
switch_current() {
    local target="$1"
    local link="$PREFIX/current"
    local temporary

    validate_root_only_ancestry "$PREFIX"
    [ ! -e "$link" ] || [ -L "$link" ] || die "current 必須是符號連結：$link"
    temporary="$PREFIX/.current.$$"
    rm -f "$temporary"
    ln -s "$target" "$temporary"
    mv -f "$temporary" "$link"
}

prune_releases() {
    local current total old
    current="$(readlink "$PREFIX/current" 2>/dev/null || true)"
    total="$(find "$RELEASES" -mindepth 1 -maxdepth 1 -type d | wc -l | tr -d ' ')"
    [ "$total" -gt "$KEEP_RELEASES" ] || return 0

    find "$RELEASES" -mindepth 1 -maxdepth 1 -type d | sort | head -n "$((total - KEEP_RELEASES))" |
        while IFS= read -r old; do
            [ "$old" = "$current" ] && continue
            [ -L "$old" ] && continue
            log "移除舊版本 $old"
            rm -rf "$old"
        done
}

promote_if_staged() {
    PROMOTED=""
    find_staged_release

    [ -n "$STAGED" ] || return 0

    log "發現暫存的版本 ${STAGED}，驗證後搬進 $RELEASES"
    promote_release "$STAGED"
    switch_current "$PROMOTED"
    log "current -> $PROMOTED"
    prune_releases

    # Anything else left in staging is a build that lost the race; it will never be promoted.
    find "$STAGING" -mindepth 1 -maxdepth 1 -exec rm -rf {} + 2>/dev/null || true
}

if [ "${WALIS_PROMOTE_ONLY:-0}" = "1" ]; then
    promote_if_staged
    [ -n "$PROMOTED" ] || die "暫存區沒有可採用的版本：$STAGING"
    exit 0
fi

# The request is intentionally service-writable, but root only observes its
# metadata.  Reject links and non-regular files so the daemon never follows an
# attacker-controlled pathname.
trigger_type="$(stat -f %HT "$TRIGGER" 2>/dev/null || true)"
[ "$trigger_type" = "Regular File" ] || exit 0

requested="$(stat -f %m "$TRIGGER")"
applied="$(cat "$STAMP" 2>/dev/null || echo 0)"
case "$requested" in
    ''|*[!0-9]*) exit 0 ;;
esac
case "$applied" in
    ''|*[!0-9]*) applied=0 ;;
esac

# launchd may also fire once when loading WatchPaths; the timestamp filters out duplicate restarts.
if [ "$requested" -le "$applied" ]; then
    exit 0
fi

temporary_stamp="$(mktemp "$STATE_DIR/.restart.applied.XXXXXX")"
trap 'rm -f "$temporary_stamp"' EXIT
printf '%s' "$requested" > "$temporary_stamp"
chmod 600 "$temporary_stamp"
mv -f "$temporary_stamp" "$STAMP"
trap - EXIT

log "收到重啟要求，${DELAY} 秒後重啟 $LABEL"
sleep "$DELAY"

# A staged release that fails validation is reported and the service simply restarts into the version it already runs; the
# subshell keeps die() inside promotion from aborting the restart itself.
if ! (promote_if_staged); then
    log "暫存的版本未通過驗證，服務維持在目前版本" >&2
fi

if launchctl kickstart -k "system/$LABEL"; then
    log "已重啟 $LABEL"
    exit 0
fi

# If the service is not in launchd (e.g. unloaded during an install and never loaded back), load it while we are here.
PLIST="/Library/LaunchDaemons/$LABEL.plist"
if [ -f "$PLIST" ] && launchctl bootstrap system "$PLIST"; then
    log "$LABEL 不在 launchd 裡，已重新載入"
    exit 0
fi

log "重啟 $LABEL 失敗" >&2
exit 1
