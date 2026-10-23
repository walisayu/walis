# Self-hosting Walis

[繁體中文](self-hosting.zh-TW.md)

A fresh install is meant to work the first time: it can be opened from another machine over
plain HTTP, and it runs builds on every platform. This page describes what those defaults are,
and what to tighten once Walis is reachable by more than the people you trust.

Messages printed by the server, the runner and the installer are currently in Traditional Chinese.
They are quoted verbatim below so you can match them against what you see.

## Start it

```bash
./Walis.Api --urls http://0.0.0.0:11120
```

Open `http://<this machine>:11120` from any machine on the network and create the first
administrator.

Data (the SQLite database, artifacts, cache, backups) lives in `data/` next to the binary. Set
`Walis__DataDirectory` to an absolute path to keep it somewhere else.

## The first administrator

Until an administrator exists, anyone who can reach the port can see the setup screen. So that
the first one to open it does not simply become the owner, setting up from another machine needs
a **setup code** that only the person who installed Walis can read.

- **In Production** (the release archives, the installers and the services), the setup screen always
  shows a **Setup code** field, on the server itself as well. The server prints the code on its console
  when it starts, and saves it as `setup-code.txt` in the data directory:

  | Installed as | Where to read the code |
  | --- | --- |
  | Started by hand | The console, or `data/setup-code.txt` next to the binary |
  | `install.sh --service` | `sudo cat /var/lib/walis-server/setup-code.txt`, or `journalctl -u walis-server` |

  It can be typed in either case, with or without the dashes. The code stays the same across
  restarts and is deleted as soon as the first administrator is created. API clients send it as
  the `X-Walis-Bootstrap-Token` header on `POST /api/auth/setup`.

> ［本版修改］Previously the code was asked for only from other machines. A reverse proxy on the same machine that rewrites
> `Host` to `127.0.0.1:<port>` and forwards nothing (nginx with only `proxy_pass`) made a remote request look local, so
> Production now asks everywhere.

Three details:

- `Security__RequireSetupCode=false` brings back the shortcut for the browser on the server itself
  (`http://localhost:11120` needs no code); Development does this by default. With the shortcut on, a
  connection that arrives from loopback under a host name other than `localhost` — a reverse
  proxy on the same machine that does not forward the client address, for instance — is treated as
  remote and needs the code too. Once the server listens on an address other machines can reach
  (`0.0.0.0`, `[::]`, an interface address; the systemd install does), a loopback request is only
  taken as local when it carries no `X-Forwarded-*`/`Forwarded` header at all and its `Host` port
  is one Walis actually listens on. The browser on the server opening `http://localhost:11120` still
  passes; a proxy that rewrote `Host` to `localhost` without forwarding the client, or that listens
  on another port, does not. When this applies and no administrator exists yet, the startup log says
  `首次設定需要設定碼`.
- `Security__RequireSetupCode=true` (the Production default) removes the local shortcut altogether: the
  code (or `Security__BootstrapToken`) is required from every address, this machine included.
- Each node generates its own code. For several nodes behind one address, set
  `Security__BootstrapToken` (32 to 1,024 characters) on all of them instead; that value is then
  what the setup screen expects, and no code is generated.

## The defaults, and how to tighten them

| Default | So that | Tighten it with |
| --- | --- | --- |
| Plain HTTP is accepted from any address | the first connection works without a certificate | TLS, then `Security__RequireHttps=true` |
| Any host name is accepted (`AllowedHosts` is `*`) | the machine's address or name works as typed | `AllowedHosts="walis.example.com;localhost"` |
| A platform without a sandbox runs builds unisolated | the first build works on Linux and Windows | **Settings → Build → Refuse to run without an isolation boundary** |

On a network you trust, the defaults are a reasonable place to stay. Before Walis is reachable
from the internet, do at least the first one.

The third default has one exception: a server started with `ASPNETCORE_ENVIRONMENT=Production`
that listens on a non-loopback address (`--urls http://0.0.0.0:11120`, `*`, a host name) is
reachable by others before anyone has reviewed the isolation page, so a **brand-new database**
on such a server starts with **Refuse to run without an isolation boundary** already on. A
database that already stored settings keeps whatever it says. The first-administrator screen
explains the risk and lets you switch it, and **Settings → Build** keeps asking until an
administrator accepts it; the page then records who accepted and when.

### HTTPS

Over plain HTTP, passwords, session cookies, API keys and the secrets handed to runners cross the
network unencrypted. Pick one of the following to add TLS.

**A reverse proxy on the same machine.** The simplest setup: Walis listens on loopback and the
proxy terminates TLS. With [Caddy](https://caddyserver.com), which obtains the certificate by itself:

```
walis.example.com {
    reverse_proxy 127.0.0.1:11120
}
```

```bash
./Walis.Api --urls http://127.0.0.1:11120
```

Walis trusts forwarded headers only from the addresses in `Security__TrustedProxies`. For a proxy on
the same machine, list loopback:

```bash
Security__TrustedProxies="127.0.0.1,::1" ./Walis.Api --urls http://127.0.0.1:11120
```

> ［本版修改］Previously a proxy connecting from loopback was trusted automatically. Any process on the machine, a build
> script included, could then claim another client address or HTTPS. With `Security__TrustedProxies` empty, forwarded
> headers are now ignored; the macOS installer sets `127.0.0.1,::1` for its Caddy (`--trusted-proxies` changes it).

Whatever proxy you use, it must pass
the original `Host` header through, send `X-Forwarded-Proto: https`, and send `X-Forwarded-For`
with the client address. Caddy does all three by default. For nginx:

```nginx
location / {
    proxy_pass http://127.0.0.1:11120;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    client_max_body_size 66m;   # runners upload artifacts of up to 64 MB
}
```

**A reverse proxy on another machine.** Walis has to be told which proxy to believe:

```bash
Security__TrustedProxies="10.0.0.5" ./Walis.Api --urls http://0.0.0.0:11120
```

`Security__TrustedProxies` takes exact IP addresses separated by commas, semicolons or spaces; it
does not take a network range. With more than one proxy in the chain, list each of them and set
`Security__ForwardLimit` to the number of hops (1 to 3). Forwarded headers from an address that is
not listed are ignored: Walis then sees every request as plain HTTP coming from the proxy, so
cookies are not marked `Secure`, and the sign-in throttle, the rate limits and the audit log's
`remoteAddress` column all see the proxy's address instead of the client's. With the proxy listed,
the audit log records the client address the proxy forwarded.

**TLS in Walis itself.** No proxy; Walis serves HTTPS with a PEM certificate and key:

```bash
Kestrel__Certificates__Default__Path=/etc/walis/fullchain.pem \
Kestrel__Certificates__Default__KeyPath=/etc/walis/privkey.pem \
./Walis.Api --urls https://0.0.0.0:11120
```

The certificate has to be trusted by the browsers and by every machine that runs `walis-runner`.

**Then require it.** Once HTTPS works, set `Security__RequireHttps=true`. From then on a request
to `/api`, `/mcp` or `/metrics` that comes from another machine without TLS is answered with `426`
and `此伺服器已設定 Security:RequireHttps，遠端 API 連線必須使用 HTTPS。`, and every cookie is
marked `Secure`. Loopback stays available over plain HTTP for maintenance.

### Sign-in protection

Passwords need at least 12 characters and are stored with PBKDF2-SHA256 (600,000 iterations). Failed
sign-ins lock the account after 5 tries and the source after 100 within 15 minutes; IPv6 sources are
counted per /64. Sign-in, one-time-password and setup requests have their own per-source ceiling of
30 a minute (`Security__AuthRateLimitPerMinute`).

Administrator actions whose effect outlives the session ask for the current password again: creating
an API key, resetting another account's password or one-time password, granting the admin role,
turning sign-in or auditing off, and lifting a lockout. API clients send it as the `X-Walis-Reauth`
header; without it the answer is `403` with `code: reauth_required`. Changing your own password ends
every session of the account (the browser receives a new one), and **Sign out everywhere** in the user
menu (`POST /api/auth/logout-all`) ends them all.

### Audit log and security alerts

Every audit row carries the actor, the client address (`remoteAddress`, after the trusted
forwarded headers above), the outcome (`success`, `denied` or `failed`), Kestrel's request id and,
for build events, the job id. Failed sign-ins, one-time-password failures, rejected setup codes
and lockouts are recorded with `denied` and the submitted account name as the actor; the same
events are written as warnings to the server log. Audit rows have their own retention
(`AuditRetentionDays`, default 365, at least 30) separate from build history, and a restore
merges the archive's audit rows into the ones already present instead of replacing them.
`GET /api/audit/page` filters by actor, target and time range; `GET /api/audit/export.csv`
downloads the same selection (both admin-only).

Refused requests are recorded as `access-denied` (missing or rejected credentials, API keys used from
another address or for another endpoint, CSRF failures, insufficient roles, job run-role refusals,
rejected push webhooks), at most once a minute per actor and reason; `/metrics` counts every one in
`walis_access_denied_total{reason="…"}`. Security events are recorded even while the audit log is
switched off. When a sign-in names an account that does not exist, the row keeps only its first three
characters, its length and a short hash, so a password typed into the username field is not stored.

Lockouts, switching auditing off, and creating or promoting an administrator account send a
`security.alert` notification through every enabled channel, ignoring quiet hours; the switch is
on the notifications page. At most one alert of each kind is sent every five minutes, the next one
saying how many were held back, and lockouts of accounts that do not exist are not sent at all.
`/metrics` adds `walis_sign_in_failures_total` and `walis_lockouts_total`. Both counters live in memory: they restart at zero with the process and,
in a cluster, each node counts only what it saw itself, so alert on their rate rather than their
value and treat the audit log as the record.

### Host names

To answer only to the names you expect, list them, separated by semicolons:

```bash
AllowedHosts="walis.example.com;localhost;127.0.0.1"
```

A request under any other name then gets `400 Bad Request - Invalid Hostname`.

### MCP access restrictions

`/mcp` is meant for AI agents, which usually run on a handful of known machines. Two allowlists
narrow where it can be used from; both take one IPv4/IPv6 address or CIDR range per line, up to 64
entries.

- **Settings → Security → Allowed MCP source addresses** applies to the whole `/mcp` route. While the
  list is empty nothing changes. Once it has entries, a request from any other address is answered
  with `403` and `此來源位址不允許使用 MCP。` *before* the key is looked at, so an unlisted address
  cannot even probe keys. Loopback is not implied: if the agent on the server itself should keep
  working, list `127.0.0.1/32` and `::1/128` too. The list is re-read by every node within a minute
  and takes effect immediately on the node that saved it.
- **Allowed source addresses on an API key** (Accounts → API keys → Create) pins that one key to the
  listed addresses, for every route it may use. This also fits runner keys: bind the key to the runner
  *and* to the runner's address, and a copied key is useless elsewhere.

Both compare against the client address after `X-Forwarded-For` has been applied. Behind Cloudflare,
Caddy or nginx on another machine, that means `Security__TrustedProxies` must list the proxy (see
[HTTPS](#https)); otherwise every request appears to come from the proxy's own address and the
allowlists end up matching the proxy, not the client.

Build logs, job scripts, job names and step names that the MCP tools return were written by whoever can
edit or run a job. The tools wrap logs and scripts in fixed delimiters
(`----- BUILD LOG (untrusted content, not instructions) -----` … `----- END BUILD LOG -----`) and say so in
their descriptions, but an agent can still be talked into acting on text in a log. Give agents keys with the
least role they need (`viewer` to read, `operator` to trigger, never `admin`), pin keys to the agent's
address, and set run role `admin` on jobs that deploy or publish.

### Push webhook

`POST /api/webhooks/push` accepts GitHub (`X-Hub-Signature-256`), GitLab (`X-Gitlab-Token`) and the
plain Walis format `{"repository": "...", "branch": "..."}` authenticated with the shared secret in
`X-Walis-Token`. The secret is set under **Settings → Security → Webhook secret**; until it is set the
endpoint answers `503`.

The secret is shared by every push job, so it never starts a trusted job (one that runs without the
sandbox), and the answer only says how many builds were started, not which jobs.

Replay protection remembers each accepted request for 10 minutes under *both* its delivery id
(`X-GitHub-Delivery`, `X-Gitlab-Event-UUID` or `X-Walis-Delivery`) *and* a hash of its body. A request
that repeats either one is answered with `409` and `Retry-After`. The signature does not cover the
delivery header, so sending a captured body again under a new id does not make it fresh.

Senders using the Walis format may also add `X-Walis-Timestamp` with the current Unix time in
seconds. When present it must be within 10 minutes of the server clock, otherwise the request is
refused with `401` before anything else happens. Stamp every request if you can: a captured
request then stops working at the end of its window even on a node whose replay table never saw
the original.

### Outbound connections to private networks

Notification webhooks and Slack URLs, AI provider base URLs and backup endpoints are addresses an
administrator types in, and the server connects to them with secrets attached. By default those
connections are only made to public addresses: the host is resolved when connecting and loopback,
RFC 1918, link-local (including the cloud metadata address `169.254.169.254`) and the other
reserved ranges are skipped, so a leaked admin credential cannot turn Walis into a probe for the
internal network. A refused destination fails with `不允許連線到私有網段：<host>`, which the
notification log and the backup test show.

A literal private address or `localhost` in the settings is refused as well. If a destination
genuinely lives on the internal network (a MinIO on the LAN, a Slack-compatible relay, a local
model server), set `Security__OutboundAllowPrivateNetwork=true` on the server. Plugins are not
affected: their `http.fetch` has its own manifest-based rule.

### Settings on a systemd service

`install.sh --service` registers `walis-server`, listening on `http://0.0.0.0:11120` with its data in
`/var/lib/walis-server`. Add settings with a drop-in instead of editing the unit file:

```bash
sudo systemctl edit walis-server
```

```ini
[Service]
Environment="Security__RequireHttps=true"
Environment="AllowedHosts=walis.example.com;localhost;127.0.0.1"
```

```bash
sudo systemctl restart walis-server
```

## Secrets at rest

Walis encrypts the secrets it stores before they reach the database: AI provider keys, environment variables marked
secret, the passwords and keys inside the system settings (webhook secret, notification signing secret, SMTP password,
backup passphrase, PostgreSQL password, cloud destination credentials), password-type build parameters and TOTP
secrets, and Slack and webhook notification URLs. Each value is AES-256-GCM ciphertext prefixed with `enc:v2:`, with the
table and column it belongs to bound in, so a value copied into another column does not decrypt; everything else in the
database stays readable. Values written as `enc:v1:` by the previous version are still read and become `enc:v2:` the next
time they are saved.

The master key comes from one of two places:

- `Security:EncryptionKey` (environment variable `Security__EncryptionKey`), 32 to 1,024 characters. Use this on a
  multi-node cluster — every node must hold the same key — and whenever you want the key managed outside the data directory.
  The service removes the variable from its own environment once it has read it.
- Otherwise Walis generates a random key on first start into `<data directory>/secret.key` (owner-only permissions,
  never a symbolic link) and logs where it put it. Nothing else to do on a single server.

Values written by an older Walis in plaintext keep working and become ciphertext the next time they are saved; no
migration step is needed.

**Backups do not contain the key**, even when an extra backup path covers the data directory (`secret.key` and
`setup-code.txt` are always left out). A backup archive holds the ciphertext exactly as stored, so restoring it on another
machine only works if that machine has the same key: copy `secret.key` into the new data directory before the first
start, or set the same `Security__EncryptionKey`. If the key is missing, startup logs an error naming the values that
cannot be decrypted, the affected values read as unset, and **Settings → Security → Secret encryption** (or
`POST /api/settings/secrets/reset-undecryptable`, admin only) clears exactly those values so they can be entered again.
A TOTP secret that cannot be read is removed together with its recovery codes; the user enrols again.

## Build isolation

A build is a script, and it runs with the permissions of whoever runs it. Put plainly: **on
Linux, on Windows and on every remote runner, anyone who can edit or run a job can read every
secret** the service account can read — the database with its password hashes and API key
hashes, every environment variable including the secret ones, AI provider keys and backup
credentials — and can change the database to make themselves an administrator. The
job-level view and run roles limit what the API shows; they do not limit what a script can do
on disk. Treat an operator on such a runner as an administrator.

| Where the build runs | What happens by default |
| --- | --- |
| The server's built-in `local` runner, on macOS | Every step runs inside `sandbox-exec`. The profile is a **deny list**: it denies the Walis data directory, the SQLite directory, backup locations, the installation (`current`, `releases`, `var`), the service account's home directory and the paths listed under **Additional denied paths**, then allows the job's own workspace again. It also denies signals to other processes, Mach service lookups and reading other processes' information (switch: **The sandbox also denies signals, Mach service lookups and reading other processes' information**). Everything else — the network, `/usr`, `/opt`, the temp directory — stays allowed. |
| The built-in `local` runner, on Linux or Windows | The script runs directly, as the account Walis runs as. It can read Walis' data directory — the database, password hashes and cloud credentials. |
| `walis-runner` on another machine | The script runs directly, as the account the runner runs as. |

A build that ran without a sandbox says so at the top of its log
(`未套用沙箱：腳本以執行器帳號的權限直接執行。`), and **Settings → Build** shows a warning when
that is the case for the built-in runner. On Linux and Windows, let only people you trust as much
as an administrator edit jobs — or require a boundary.

What the macOS sandbox does not do:

- It is not an allow list. A deny list stops a script from casually reading the database or the
  installation; it does not stop persistence. `crontab`, `launchctl` and `ssh localhost` hand a
  command to another daemon that runs it **outside** the sandbox, as the service account. If that
  matters to you, run builds on a remote runner inside a dedicated VM or container.
- It does not apply to anything the Docker daemon does. If the service account can talk to
  Docker (the `docker` group on Linux, Docker Desktop on macOS), `container.run` and any `docker`
  command in a script run as root on Linux and bypass the sandbox on macOS: giving the service
  account Docker access is the same as giving every job editor root. Use rootless Docker or a
  remote runner for container work.
- A job can opt out of the network: **Deny network** in the job editor adds `(deny network*)` to
  its profile. A job can also opt out of the sandbox entirely: **Trusted job: no sandbox** is
  for the self-update job and anything else that genuinely must write the installation. Only an
  administrator can enable, modify or delete a trusted job, or change its environment variables.
  Operators may still trigger it according to its run role, but modifying it returns `403` even
  when the request removes the trusted flag. The first line of every step log says `信任任務：…` when it applies.

> ［本版修改］Previously operators could remove the trusted flag to edit a job, and environment changes used the run permission. Trusted jobs and their environment now require an administrator to modify them.

### The service account

Run Walis under a dedicated account that holds nothing else: no SSH keys, no `~/.gitconfig`
credential helper, no `gh`/npm/NuGet logins, no publishing rights anywhere. The sandbox denies
the home directory, but a sandbox is a mitigation; an account with nothing in it is the boundary.
`deploy/install.sh --user <account>` already supports this on macOS, and the systemd units from
the installer use `DynamicUser`. On macOS the installer keeps `$PREFIX`, `$PREFIX/releases` and
`$PREFIX/current` owned by root, so a build cannot replace the executable or `wwwroot` the
service restarts into; the self-update job builds into `$PREFIX/var/staging` and the root
restarter validates, promotes and restarts.

Deployment secrets handed over through the environment (`Security__BootstrapToken`,
`ConnectionStrings__Walis`, the runner's `WALIS_TOKEN`) are removed from the process
environment as soon as the program has read them, so a build sharing the account cannot read
them back with `ps -E` or `/proc/<pid>/environ`. The systemd units keep them in
`/etc/walis/*.env` (root, `0600`) rather than in the unit file.

### Requiring an isolation boundary

In **Settings → Build**, turn on **Refuse to run without an isolation boundary**. From then on:

- The built-in runner on Linux or Windows no longer runs builds; they fail with
  `系統要求隔離建置，但此執行器沒有可套用的沙箱；已拒絕執行，避免靜默降級。` Assign jobs to a remote
  runner instead.
- A remote runner is handed work only when it lives on a machine, VM or container that does
  nothing except run Walis builds, and both sides say so: add its name to **Externally isolated
  runners** on the same page, and start it with `--external-isolation true` (or
  `WALIS_EXTERNAL_ISOLATION=true`). Walis cannot check the boundary for you; do not declare it for
  a runner on a shared or general-purpose host.

**Isolate builds**, the switch above it, is separate: it decides whether the sandbox is used where
one exists. Leave it on.

### Working directories

When **Settings → Build** names a default working directory, each job without its own gets a
subdirectory of it named after the job id, never the directory itself; `walis-runner --workdir` works
the same way. A job's own working directory may not overlap the directory of a job the person saving
it cannot modify (an administrator's job or a trusted job), and the same check runs again before the
build starts. When neither the job nor **Settings → Build** names a working directory, the built-in runner gives
each job a private directory of its own (`workspaces/<job id>` under the data directory; directories
left behind by deleted jobs are reclaimed when the service starts). **Clean the working directory
after each build** (a system setting that jobs can override) empties that directory once a build ends,
whether it succeeded or failed, after artifacts and the cache have been saved; turn it on for jobs with
password parameters or job-specific secrets so checked-out sources and credentials written by the
script do not linger until the next build. If you point jobs at a directory of your choosing and use
workspace cleaning, caching or artifact collection, list that directory under **Allowed working directories**.
Ordinary jobs also need this administrator allowlist when their workspace overlaps the service account's
home or the installation. Allow only the project's directory, rather than an entire home or installation.
System-managed per-job workspaces are automatically allowed. A missing required allowlist stops the build
before its script runs, with `使用此工作目錄前必須設定允許的工作目錄。`.

> ［本版修改］Previously the guide mentioned cleaning and caching, and an ordinary job could reopen a workspace inside the home or installation without administrator approval. Those locations now require an explicit allowlist; artifact collection also requires it.

> ［本版修改］Previously every job without a directory shared the default working directory, and two jobs could name the same directory.

Step files (`$WALIS_ENV_FILE`, `$WALIS_OUTPUT_FILE`) live in a private `.walis/` directory inside the
workspace for the duration of the build, ignored by git. On macOS the sandbox keeps `/opt/homebrew`,
`/usr/local` and every directory on the service's PATH that the service account can write to readable
but not writable, and denies the action pack directories, because each of them holds something that
later runs outside the sandbox.

## Remote runners

1. **Create a key for the runner.** In **Accounts → API key**, choose **Create key** and put the
   runner's name in **Bound runner**. A bound key can only register, claim and report as that one
   runner. The key is shown once.

2. **Start the runner.**

   ```bash
   WALIS_TOKEN=<key> ./walis-runner \
     --server http://walis.internal:11120 \
     --name pi-01 --labels linux,arm64
   ```

   Or install it as a systemd service in one line:

   ```bash
   curl -fsSL https://walis.io/install.sh | sudo WALIS_TOKEN=<key> sh -s -- \
     --runner --service --server http://walis.internal:11120 \
     --name pi-01
   ```

   The installer writes the key to `/etc/walis/runner.env` (root, `0600`) and the unit reads it
   with `EnvironmentFile=`; to rotate it, edit that file and `systemctl restart walis-runner`.
   Starting the runner by hand, give the key as `WALIS_TOKEN=<key>` or `--token-file <file>`
   rather than `--token <key>`: a command-line argument is visible to every account on the
   machine through `ps`. The file must not be readable by other accounts (`chmod 600`), or the
   runner refuses it; the same applies to `--password-file` (or `WALIS_PASSWORD`).

   With an `http://` address on another machine, the runner starts with a warning that the key
   and job secrets travel unencrypted. Use the `https://` address once the server has one.

3. **Assign jobs to it.** The runner registers itself the first time it connects; after that, pick
   it in the job's runner field. Jobs with no runner chosen run on the built-in `local` runner.
   A runner runs scripts without Walis's sandbox; on a host that holds deployment credentials, set
   **Accepted jobs** to **Administrator jobs only** in the runner's settings, and only jobs saved by an
   administrator with run role `admin` can be sent to it.

If the server requires an isolation boundary, also add `--external-isolation true` (for the
installer, `--external-isolation`) and list the runner's name on the server, as described above.

All options are listed in [`source/Walis.Runner/README.md`](../source/Walis.Runner/README.md).

## Containers

`source/Dockerfile` builds the API image and `source/Walis.Admin/Dockerfile` builds the admin UI
behind nginx. They are the images used during development, not a ready-made deployment, and this
repository does not ship a compose file. If you build on them:

- The API image is built from the repository root (`docker build -f source/Dockerfile .`) so the
  restore can read `global.json`, `nuget.config` and the committed `packages.lock.json` files; base
  images are pinned by digest and the restore runs in locked mode.

- The API listens on port 8080, keeps its data in `/app/data` and expects workspaces in `/workspace`.
- The UI image's `nginx.conf` forwards `/api` to a host named `walis-dev-api` on port 8080.
- The first request does not come from loopback, so setup needs the setup code; it is in the API
  container's log and in `/app/data/setup-code.txt`.
- Set `Security__TrustedProxies` to the exact address of the proxy container.
- A container is Linux: builds on the built-in runner run unisolated inside it, and can read `/app/data`.
  With an external database, the connection string you pass is removed from the process environment
  after startup, but the script still reaches the database through Walis itself; prefer a remote
  runner for untrusted jobs.

For a single machine, the release archive is the shorter path: one binary that also serves the UI.

## Troubleshooting

| What you see | Where | Why | Fix |
| --- | --- | --- | --- |
| `403`, `從其他機器進行首次設定需要設定碼。…` | Setup screen | The first administrator is being created from another machine without the right code. | [Find the setup code](#the-first-administrator). |
| `426`, `此伺服器已設定 Security:RequireHttps，遠端 API 連線必須使用 HTTPS。` | Browser, any client | `Security__RequireHttps` is on and the request is plain HTTP, or it came through a proxy whose forwarded headers are not trusted. | Use HTTPS; check `X-Forwarded-Proto` and `Security__TrustedProxies`. |
| `400 Bad Request - Invalid Hostname` | Browser, any client | `AllowedHosts` was restricted and does not include the name in the address. | [Add the host name](#host-names). |
| `建置前安全檢查失敗：系統要求隔離建置，但此執行器沒有可套用的沙箱；已拒絕執行，避免靜默降級。` | Build log | An isolation boundary is required and the built-in runner has no sandbox on this platform. | [Requiring an isolation boundary](#requiring-an-isolation-boundary). |
| `系統要求隔離建置，但此 runner 僅宣告未隔離模式；工作未被認領。…` (409) | Runner output | An isolation boundary is required and the runner did not declare external isolation. | Start it with `--external-isolation true` and list it on the server. |
| `此 runner 未列入管理員核准的外部隔離清單；工作未被認領。…` (403) | Runner output | The runner declared external isolation but its name is not listed on the server. | Add it to **Externally isolated runners**. |
| `找不到執行器「…」。` (409) | Triggering a build | The job names a runner that has never connected. | Start the runner once so that it registers. |
| `有 N 筆已加密的機密無法用目前的金鑰解密…` | Server log at startup | A backup was restored on a machine without the original `secret.key`, or `Security__EncryptionKey` changed. | [Secrets at rest](#secrets-at-rest): put the key back, or clear the values in Settings → Security and enter them again. |
