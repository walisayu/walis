#!/usr/bin/env bash
# Registers the jobs this machine should have on the Walis service: updates a job with the same name if it exists, creates it otherwise.
#
#   WALIS_TOKEN=<admin API key> ./deploy/register-job.sh [--api http://127.0.0.1:11120]
#   ./deploy/register-job.sh --token <token>        (kept for compatibility; the token then shows in the process list)
#
# The token must belong to an administrator: the self-update job is registered as a trusted job (no sandbox), which only
# administrators may set.
#
# 1. "Self-update": deploy/update.sh pulls the code, builds a new release into $PREFIX/var/staging and files a restart request;
#    com.walisayu.walis-restarter (root, outside the service's process tree) validates the staged release, moves it into the
#    root-owned releases/, switches current and restarts. The job runs without the sandbox because it writes $PREFIX/var and
#    reads the maintainer's repository, both of which the sandbox denies to ordinary jobs.
# 2. "Website install.sh check": runs deploy/sync-install-sh.sh --online daily; when the online
#    https://walis.io/install.sh drifts from deploy/get-walis.sh it fails and sends a notification.
# 3. "Action pack update": runs git pull in $PREFIX/action-packs/walis-actions daily;
#    the service detects pack directory changes every minute and reloads automatically, so the pull takes effect immediately. Clones first if the directory is missing.
#    The pack directory is denied inside the sandbox (its JSON becomes build shell), so this job is a trusted job as well.
# All three are admin-only to view and run: their scripts, working directories and logs describe this machine's layout.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
API="${WALIS_API:-http://127.0.0.1:11120}"
TOKEN="${WALIS_TOKEN:-}"
JOB_NAME="${WALIS_JOB_NAME:-自我更新}"
PACKS_DIR="${WALIS_ACTION_PACKS:-/usr/local/walis/action-packs}"
PACK_REPO="https://github.com/walisayu/walis-actions.git"

while [ $# -gt 0 ]; do
    case "$1" in
        --api) API="$2"; shift 2 ;;
        --token) TOKEN="$2"; shift 2 ;;
        --name) JOB_NAME="$2"; shift 2 ;;
        *) echo "未知的參數：$1" >&2; exit 1 ;;
    esac
done

if [ ! -d "$PACKS_DIR/walis-actions/.git" ]; then
    mkdir -p "$PACKS_DIR"
    git clone -q "$PACK_REPO" "$PACKS_DIR/walis-actions"
    echo "已 clone 套件到 $PACKS_DIR/walis-actions"
fi

REPO_ROOT="$REPO_ROOT" API="$API" TOKEN="$TOKEN" JOB_NAME="$JOB_NAME" PACKS_DIR="$PACKS_DIR" PACK_REPO="$PACK_REPO" python3 <<'PY'
import json
import os
import urllib.error
import urllib.request

api = os.environ["API"].rstrip("/")
token = os.environ["TOKEN"]
repo = os.environ["REPO_ROOT"]
name = os.environ["JOB_NAME"]


def call(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(f"{api}{path}", data=data, method=method)
    request.add_header("Content-Type", "application/json")
    if token:
        request.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = response.read().decode()
            return json.loads(payload) if payload else None
    except urllib.error.HTTPError as error:
        raise SystemExit(f"{method} {path} 失敗：{error.code} {error.read().decode()}")


jobs = [
    {
        "name": name,
        "project": "Walis",
        "description": "拉最新程式碼、建置新的 release 到暫存區，再交給 walis-restarter 驗證、切換 current 並重啟服務。信任任務：不套用沙箱。",
        "repository": None,
        "branch": "main",
        "runnerName": None,
        "workingDirectory": repo,
        "command": "./deploy/update.sh",
        "timeoutMinutes": 30,
        "artifactPath": None,
        "triggerKind": "manual",
        "cron": None,
        "notifyOnFailure": True,
        "isDisabled": False,
        "scriptMode": "source",
        "actions": None,
        "allowConcurrentBuilds": False,
        # Only administrators can run it and only administrators can keep this flag; the build writes outside the sandbox.
        # Only administrators see it either: its script, working directory and logs describe the deployment layout.
        "runRole": "admin",
        "viewRole": "admin",
        "trustedUnsandboxed": True,
    },
    {
        "name": "官網 install.sh 檢查",
        "project": "Walis",
        "description": "比對線上的 https://walis.io/install.sh 與 deploy/get-walis.sh，走鐘就失敗；修法是在有官網 repo 的機器跑 deploy/sync-install-sh.sh --commit。",
        "repository": None,
        "branch": "main",
        "runnerName": None,
        "workingDirectory": repo,
        "command": "./deploy/sync-install-sh.sh --online",
        "timeoutMinutes": 5,
        "artifactPath": None,
        "triggerKind": "cron",
        "cron": "0 9 * * *",
        "notifyOnFailure": True,
        "isDisabled": False,
        "scriptMode": "source",
        "actions": None,
        "allowConcurrentBuilds": False,
        "runRole": "admin",
        "viewRole": "admin",
    },
    {
        "name": "動作套件更新",
        "project": "Walis",
        "description": f"每天從 {os.environ['PACK_REPO']} 拉最新的共用動作套件；服務每分鐘偵測目錄變動後自動重載。",
        "repository": None,
        "branch": "main",
        "runnerName": None,
        "workingDirectory": os.path.join(os.environ["PACKS_DIR"], "walis-actions"),
        "command": "git pull --ff-only origin main",
        "timeoutMinutes": 5,
        "artifactPath": None,
        "triggerKind": "cron",
        "cron": "0 8 * * *",
        "notifyOnFailure": True,
        "isDisabled": False,
        "scriptMode": "source",
        "actions": None,
        "allowConcurrentBuilds": False,
        # The pack directory (Walis:ActionPacks) is denied to every sandboxed build, because its JSON is shell that every job
        # using the action runs. This job must still write its own clone, so it is a trusted job, admin-only to run and see.
        "runRole": "admin",
        "viewRole": "admin",
        "trustedUnsandboxed": True,
    },
]

current = call("GET", "/api/jobs") or []

for payload in jobs:
    existing = next((job for job in current if job["name"] == payload["name"]), None)

    if existing:
        job = call("PUT", f"/api/jobs/{existing['id']}", payload)
        print(f"已更新任務 {payload['name']}：{job['id']}")
    else:
        job = call("POST", "/api/jobs", payload)
        print(f"已建立任務 {payload['name']}：{job['id']}")

    if payload["triggerKind"] == "manual":
        print(f"  手動觸發：curl -X POST {api}/api/jobs/{job['id']}/builds")
    else:
        print(f"  排程：{payload['cron']}")
PY
