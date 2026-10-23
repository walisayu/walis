#!/usr/bin/env bash
# Syncs deploy/get-walis.sh to public/install.sh in the website repo (which is what https://walis.io/install.sh serves).
#
# The website copy differs from the source only by three provenance lines (after the shebang); the website repo's scripts/check-install-sh.mjs
# strips those three lines and compares line by line, so the content produced here must follow that exact format.
#
#   ./deploy/sync-install-sh.sh            # generate (overwrite) public/install.sh in the website repo
#   ./deploy/sync-install-sh.sh --check    # compare with the local website repo; drift, or synced but not committed/pushed, all count as failure
#                                          # skipped when no website repo is named (--strict fails instead)
#   ./deploy/sync-install-sh.sh --commit   # generate, commit and push the website repo (does nothing when unchanged)
#   ./deploy/sync-install-sh.sh --online   # fetch the online https://walis.io/install.sh to compare, no website repo needed;
#                                          # suited to a Walis scheduled job, so it shouts when local looks fine but online is stale
#
# This repo and the website repo are independent: nothing here looks for the website on its own. The website repo is used
# only when WALIS_WEBSITE_ROOT names it; without it, everything except --online is skipped.
# The online URL is read from WALIS_INSTALL_URL, defaulting to https://walis.io/install.sh.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE="${REPO_ROOT}/deploy/get-walis.sh"
WEBSITE="${WALIS_WEBSITE_ROOT:-}"
MODE="write"
STRICT=0
PUSH=1
INSTALL_URL="${WALIS_INSTALL_URL:-https://walis.io/install.sh}"

while [ "$#" -gt 0 ]; do
    case "$1" in
        --check) MODE="check"; shift ;;
        --commit) MODE="commit"; shift ;;
        --online) MODE="online"; shift ;;
        --strict) STRICT=1; shift ;;
        --no-push) PUSH=0; shift ;;
        -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
        *) echo "未知的參數：$1" >&2; exit 1 ;;
    esac
done

GENERATED="$(mktemp -t walis-install-sh)"
ONLINE="$(mktemp -t walis-install-sh-online)"
trap 'rm -f "$GENERATED" "$ONLINE"' EXIT

{
    head -n 1 "${SOURCE}"
    printf '%s\n' \
        '# 這份檔案由 https://walis.io/install.sh 提供，來源是 walis repo 的 deploy/get-walis.sh，' \
        '# 改了要兩邊一起更新。' \
        '#'
    tail -n +2 "${SOURCE}"
} > "${GENERATED}"

if [ "${MODE}" = "online" ]; then
    if ! curl -fsSL --max-time 30 "${INSTALL_URL}" -o "${ONLINE}"; then
        echo "抓不到 ${INSTALL_URL}。" >&2
        exit 1
    fi

    if cmp -s "${GENERATED}" "${ONLINE}"; then
        echo "線上的 ${INSTALL_URL} 與 deploy/get-walis.sh 一致。"
        exit 0
    fi

    echo "線上的 ${INSTALL_URL} 已經跟 deploy/get-walis.sh 走鐘：" >&2
    diff -u "${ONLINE}" "${GENERATED}" | head -40 >&2 || true
    echo "以 WALIS_WEBSITE_ROOT 指定官網 repo 後跑 ./deploy/sync-install-sh.sh --commit 同步並推送。" >&2
    exit 1
fi

if [ -z "${WEBSITE}" ]; then
    if [ "${STRICT}" = "1" ]; then
        echo "沒有指定官網 repo，用 WALIS_WEBSITE_ROOT 指定它的位置。" >&2
        exit 1
    fi

    echo "install.sh 同步略過：沒有指定官網 repo（WALIS_WEBSITE_ROOT）。"
    exit 0
fi

if [ ! -f "${WEBSITE}/package.json" ] || [ ! -d "${WEBSITE}/public" ]; then
    if [ "${STRICT}" = "1" ]; then
        echo "找不到官網 repo（${WEBSITE}），用 WALIS_WEBSITE_ROOT 指定。" >&2
        exit 1
    fi

    echo "install.sh 同步略過：找不到官網 repo（${WEBSITE}）。"
    exit 0
fi

TARGET="${WEBSITE}/public/install.sh"

if [ -f "${TARGET}" ] && cmp -s "${GENERATED}" "${TARGET}"; then
    if [ "${MODE}" = "check" ] && git -C "${WEBSITE}" rev-parse --git-dir >/dev/null 2>&1; then
        # A correct local file is not enough: synced but not committed, or committed but not pushed, leaves the online copy stale while this keeps saying all is fine.
        if [ -n "$(git -C "${WEBSITE}" status --porcelain -- public/install.sh)" ]; then
            echo "官網 repo 的 public/install.sh 已同步但還沒提交；到 ${WEBSITE} 提交並推送，或跑 ./deploy/sync-install-sh.sh --commit。" >&2
            exit 1
        fi

        if git -C "${WEBSITE}" fetch --quiet origin 2>/dev/null; then
            UPSTREAM="$(git -C "${WEBSITE}" rev-parse --abbrev-ref '@{upstream}' 2>/dev/null || echo origin/main)"
            if [ -n "$(git -C "${WEBSITE}" log --oneline "${UPSTREAM}..HEAD" -- public/install.sh 2>/dev/null)" ]; then
                echo "官網 repo 的 public/install.sh 已提交但還沒推送到 ${UPSTREAM}；到 ${WEBSITE} 執行 git push。" >&2
                exit 1
            fi
        else
            echo "（連不到官網 repo 的遠端，略過「是否已推送」的檢查；要確認線上版本請跑 --online）"
        fi
    fi

    echo "install.sh 與官網一致。"
    exit 0
fi

if [ "${MODE}" = "check" ]; then
    echo "官網的 public/install.sh 已經跟 deploy/get-walis.sh 走鐘：" >&2
    diff -u "${TARGET}" "${GENERATED}" | head -40 >&2 || true
    echo "執行 ./deploy/sync-install-sh.sh 同步過去（加 --commit 會直接提交並推送官網 repo）。" >&2
    exit 1
fi

install -m 644 "${GENERATED}" "${TARGET}"
echo "已更新 ${TARGET}"

if [ "${MODE}" = "commit" ]; then
    REVISION="$(git -C "${REPO_ROOT}" rev-parse --short HEAD 2>/dev/null || echo unknown)"
    git -C "${WEBSITE}" add public/install.sh
    git -C "${WEBSITE}" commit --quiet -m "同步 install.sh 到 walis ${REVISION}" -m "=> 來源是 walis repo 的 deploy/get-walis.sh，由 deploy/sync-install-sh.sh 產生"
    echo "已在官網 repo 提交（walis ${REVISION}）"

    if [ "${PUSH}" = "1" ]; then
        git -C "${WEBSITE}" push --quiet
        echo "已推送官網 repo"
    fi
fi
