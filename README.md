# Walis

**A CI/CD server that fits on one machine.**

[繁體中文](README.zh-TW.md) · [walis.io](https://walis.io)

Walis runs your build scripts, keeps the logs, and tells you when something breaks.
No repository is required, no containers are required, and there is no runtime to install —
the release is a single self-contained binary with the web UI baked in. It can even update itself.

```bash
./Walis.Api --urls http://0.0.0.0:11120
# open http://<this machine>:11120
```

---

## Why another CI server

| | Jenkins | Woodpecker | Walis |
| --- | --- | --- | --- |
| Prerequisites | JVM, plugin ecosystem | Git forge + containers | a machine and a shell |
| A job can be | a pipeline in a repo | a pipeline in a repo | a shell script, or a repo pipeline |
| Install size | hundreds of MB + plugins | container images | one binary |
| Plugins | Java, runs in the server | none | JavaScript, sandboxed, returns shell |

Walis is for the case Jenkins is too heavy for and a cron job is too little for: a handful of
machines, a handful of jobs, and someone who wants to see what actually ran.

## Install

A fresh install works the first time: open it from any machine over plain HTTP, create the first
administrator, run a build. Setting up from another machine asks for the setup code the server
prints when it starts. [docs/self-hosting.md](docs/self-hosting.md) covers that, and what to
tighten — HTTPS, host names, build isolation — before more than a trusted network can reach it.

### One line

```bash
curl -fsSL https://walis.io/install.sh | sh
```

It detects the platform, downloads the matching release, verifies the Ed25519 signature on
`SHA256SUMS.txt` ([Verifying downloads](#verifying-downloads)), checks the archive against it
and installs it. For a build agent — a Raspberry Pi, say — install the runner instead and
register it as a service:

```bash
curl -fsSL https://walis.io/install.sh | sudo sh -s -- \
  --runner --service --server http://walis.internal:11120 --name pi-01 --token <key>
```

`--service` writes a systemd unit and starts it. Without it, nothing is registered and you
start the binary yourself. Windows is not covered by the installer — download the zip below.

`<key>` is an API key bound to the runner's name; [Remote runners](docs/self-hosting.md#remote-runners)
shows where to create it.

### Download a release

Every archive is self-contained — the target machine does not need .NET installed.

| Platform | Server | Runner |
| --- | --- | --- |
| Windows x64 | `walis-<version>-win-x64.zip` | `walis-runner-<version>-win-x64.zip` |
| Windows ARM64 | `walis-<version>-win-arm64.zip` | `walis-runner-<version>-win-arm64.zip` |
| macOS Intel | `walis-<version>-osx-x64.tar.gz` | `walis-runner-<version>-osx-x64.tar.gz` |
| macOS Apple Silicon | `walis-<version>-osx-arm64.tar.gz` | `walis-runner-<version>-osx-arm64.tar.gz` |
| Linux x64 | `walis-<version>-linux-x64.tar.gz` | `walis-runner-<version>-linux-x64.tar.gz` |
| Linux ARM64 — including Raspberry Pi 4 / 5 on a 64-bit OS | `walis-<version>-linux-arm64.tar.gz` | `walis-runner-<version>-linux-arm64.tar.gz` |
| Linux ARM 32-bit — Raspberry Pi on a 32-bit OS | `walis-<version>-linux-arm.tar.gz` | `walis-runner-<version>-linux-arm.tar.gz` |
| Alpine / musl x64 | `walis-<version>-linux-musl-x64.tar.gz` | `walis-runner-<version>-linux-musl-x64.tar.gz` |
| Alpine / musl ARM64 | `walis-<version>-linux-musl-arm64.tar.gz` | `walis-runner-<version>-linux-musl-arm64.tar.gz` |

```bash
tar -xzf walis-v1.0.0-linux-arm64.tar.gz
cd walis-v1.0.0-linux-arm64
./Walis.Api --urls http://0.0.0.0:11120
```

A Raspberry Pi makes a good build agent: run the server on a desktop or a VM, and put the
**runner** on the Pi so builds that need real ARM hardware run there.

```bash
WALIS_TOKEN=<key> ./walis-runner --server http://walis.internal:11120 --name pi-01 --labels linux,arm64
```

### Verifying downloads

Every release ships `SHA256SUMS.txt`, its Ed25519 signature `SHA256SUMS.txt.sig`, and
`RUNTIME.txt` (the .NET runtime embedded in the archives). The installer verifies the signature
before it trusts a checksum; to do it by hand you need OpenSSL 1.1.1 or newer (macOS's built-in
LibreSSL cannot handle Ed25519 — `brew install openssl@3`).

The public key is [`deploy/release-signing.pub`](deploy/release-signing.pub). Its SHA-256
fingerprint (of the DER-encoded key) is:

```
2cecab2e647ee5e43c5f80a74961196af50d0eb195fb2a8563a96aade5aebbbf
```

```bash
# 1. Check the key you have is the one above
openssl pkey -pubin -in release-signing.pub -outform DER | openssl dgst -sha256
# 2. Verify the checksum file, then the archive
openssl pkeyutl -verify -pubin -inkey release-signing.pub -rawin -in SHA256SUMS.txt -sigfile SHA256SUMS.txt.sig
sha256sum -c --ignore-missing SHA256SUMS.txt      # shasum -a 256 -c on macOS
```

The archive, its checksum and the signature come from the same server, so without the
signature a replaced release could ship a matching `SHA256SUMS.txt`. The key is held offline by
the maintainer; a change of key will be announced in the changelog and in a release signed by the
previous key.

To build the archives yourself:

```bash
./deploy/release.sh                                   # every platform
./deploy/release.sh --targets linux-arm64,linux-arm   # just the Raspberry Pi ones
./deploy/release.sh --runner-only --targets linux-arm64
./deploy/release.sh --list
```

### From source

Requires the .NET 10 SDK and Node.js 20+.

```bash
dotnet run --project source/Walis.Api --no-launch-profile -- --urls http://127.0.0.1:11126
npm --prefix source/Walis.Admin run dev     # UI on http://127.0.0.1:11124
```

### Containers

There is no ready-made compose file yet. The Dockerfiles used during development, and what they
expect, are described in [docs/self-hosting.md](docs/self-hosting.md#containers).

## What it does

**Jobs and builds**

- A job is a shell script, or a sequence of actions assembled in the UI. Both compile down to one script.
- Builds are split into **steps**, each with its own status, output, exit code and duration. A failed
  step marks the rest as skipped unless it is set to continue on error.
- Output **streams live** while the build runs — locally and on remote runners alike.
- **Matrix builds**: `{"NODE":["20","22"],"OS":["mac"]}` fans one trigger out into several builds,
  each with its combination injected as environment variables.
- **Parallel steps**: mark a step to run alongside the previous one. Grouped steps share a working
  directory but get their own environment file.
- **Caching**: listed paths are saved after a successful build and restored before the next one.
- **Artifacts**: matching files are copied out of the workspace and kept with the build.

**Triggers**

Import existing Jenkins jobs: paste a Jenkinsfile or config.xml, review the mapped steps and warnings, then save.

Manual, cron, or an authenticated push webhook that accepts GitHub, GitLab and a plain
`{repository, branch}` body. A repository and shared secret are mandatory, and replay/rate state is
shared across API nodes.

Downstream triggers are limited by the upstream job's run role: an operator job cannot start an
admin-only deployment, even when an admin manually starts the upstream build. Protect the upstream
job with the admin run role to allow that chain. Only admins can assign that role; permission changes
are checked again when the chain advances.

**Runners**

The server has a built-in local runner. `walis-runner` on another machine polls for work assigned to
it, reports each step, and uploads artifacts. Both use the same step loop, so behaviour matches.

**Isolation**

Each build is preflighted before cleanup or cache I/O: the workspace must be explicitly allowed for
privileged operations, symlink/reparse-point escapes are rejected, and controller data, backup,
cache and artifact roots cannot overlap the workspace. On macOS, steps also run in `sandbox-exec`.
Where no OS sandbox is available — Linux, Windows and remote runners — a build runs unisolated by
default and says so in its log. An administrator can require an isolation boundary instead: such
platforms then refuse to run builds unless the runner sits inside a dedicated VM or container that
both sides declare; see [Build isolation](docs/self-hosting.md#build-isolation). The child
environment is allowlisted rather than inheriting controller secrets.

**Plugins**

A plugin is JavaScript, executed by Jint inside the server with a time, memory, statement and
recursion budget. It **returns a shell snippet** rather than performing actions, so you can read
exactly what will run before installing it. Network access is limited to domains declared in the
manifest, and secrets can only be injected into headers — the plugin never sees the value.
Write them with [`@walisayu/plugin-sdk`](https://www.npmjs.com/package/@walisayu/plugin-sdk).

**For AI agents**

The API speaks MCP at `/mcp`, so Claude Code, Codex or any MCP client can trigger a build, wait for
it and read the log with nothing installed locally — just a URL and an API key. New agent keys default
to the `mcp` route audience, runner keys are bound to one runner, and high-entropy secrets are stored
as SHA-256 lookup hashes. Legacy PBKDF2 API keys are disabled during upgrade and must be rotated once.

**Operations**

Sign-in is fail-closed by default; first-admin setup is local or protected by a one-time setup code
printed at startup. Browsers use HttpOnly/SameSite cookies plus CSRF validation. Role-based access
(admin / operator / viewer) includes per-job view/run roles, TOTP, audit log, admin-only Prometheus
metrics at `/metrics`, notifications by email, Slack or webhook, and a UI in
Traditional Chinese, English and Japanese with translations editable in the app.

## Databases and multiple nodes

SQLite by default — one file, nothing to set up. For high availability, point Walis at
**PostgreSQL, MySQL/MariaDB or SQL Server** and run several API nodes against it:

```bash
Database__Provider=postgresql
Database__Node=web-01
ConnectionStrings__Walis='Host=db.internal;Database=walis;Username=walis;Password=…'
```

Nodes do not talk to each other. A build is claimed with a conditional UPDATE, so only one node can
take it; each node only recovers its own interrupted builds; and cron, offline detection, cleanup and
scheduled backups are held by a lease so the cluster does them once. See [docs/database.md](docs/database.md).

## Backup and restore

Scheduled backups pack the database, artifacts and any extra paths you list, then require a
passphrase and encrypt them with Walis' versioned, chunked AES-256-GCM format. Every chunk and the
end marker are authenticated, so modification and truncation are detected before restore. Legacy
OpenSSL `Salted__` CBC files remain decryptable offline for migration, but the restore API accepts
only the authenticated format. Backups can be uploaded to local disk, S3-compatible storage,
Dropbox, Google Drive or OneDrive.

The database is exported in a **database-independent format**, which means the same backup restores
into any supported engine — that is also how you migrate from SQLite to PostgreSQL. Restore from the
UI or `POST /api/backup/restore`.

## Project layout

| Path | What it is |
| --- | --- |
| `source/Walis.Api` | Minimal API, HTTP endpoints, build workers, MCP server |
| `source/Walis.Core` | Domain model, database layer, dialects, step execution, plugins, backup |
| `source/Walis.Admin` | Vue 3 + TypeScript admin UI |
| `source/Walis.Runner` | `walis-runner`, the remote agent |
| `source/Walis.Mcp` | stdio MCP bridge for clients that cannot speak HTTP |
| `source/walis-plugin-sdk` | `@walisayu/plugin-sdk` — types, test harness, bundler, CLI |
| `source/Walis.Tests` | xUnit tests |
| `deploy/` | Installers, release packaging, service definitions |

## Development

```bash
dotnet build Walis.slnx
dotnet test source/Walis.Tests/Walis.Tests.csproj
npm --prefix source/Walis.Admin run typecheck
npm --prefix source/Walis.Admin run build
./deploy/lint.sh
```

Database tests run against SQLite by default. Set `WALIS_TEST_POSTGRES`, `WALIS_TEST_MYSQL` or
`WALIS_TEST_SQLSERVER` and the same cases run against those engines too.

## Documentation

| Document | Contents |
| --- | --- |
| [docs/database.md](docs/database.md) | Databases, dialects, multi-node, migration |
| [docs/self-hosting.md](docs/self-hosting.md) | First administrator, remote access and HTTPS, build isolation, remote runners |
| [docs/actions.md](docs/actions.md) | Action templates and how to write one |
| [docs/plugins.md](docs/plugins.md) | Plugin design and the reasoning behind it |
| [docs/plugin-development.md](docs/plugin-development.md) | Writing plugins, threat model |
| [docs/plugin-sdk.md](docs/plugin-sdk.md) | The npm SDK |
| [docs/ci-comparison.md](docs/ci-comparison.md) | What Jenkins and Woodpecker get wrong |

## License

MIT. See [LICENSE](LICENSE).
