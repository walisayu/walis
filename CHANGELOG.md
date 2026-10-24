# Changelog

## 1.1.1 — 2026-10-24

- The header search button no longer throws `Cannot read properties of undefined (reading 'toLowerCase')` on pages with password fields. CoreUI's `CSearchButton` read `event.key` on every keyboard event, and Chrome's password autofill sends keyboard events without a key; the header now uses its own button with the same look.
- The search shortcut shown on the button (⌘/ on macOS, Ctrl+/ elsewhere) now opens the search dialog; before, only clicking did.
- `install.sh` tells you that the first administrator always needs the setup code in Production, also on the server itself.

## 1.1.0 — 2026-10-23

Security and reliability changes following the 2026-10-21 and 2026-10-23 reviews.

> ［本版修改］The previous introduction described all 41 earlier findings as addressed. The follow-up review identified additional authorization and reliability defects; the entries below describe the corrections.

### Third review on 2026-10-23

The third review (50 findings, `R3-SEC-001`…`050`) is addressed below. Several entries change behaviour on upgrade; read the ones marked **Upgrade**.

- **Upgrade:** Production now asks for the setup code for the first administrator even on the server itself. A same-machine nginx with only `proxy_pass http://127.0.0.1:…` made a remote request look local. The code is printed to the console and saved as `setup-code.txt`; `Security:RequireSetupCode=false` restores the local shortcut (still under the strict rule when listening on a non-loopback address).
- **Upgrade:** Loopback is no longer a trusted proxy by default; forwarded headers are processed only from the addresses in `Security:TrustedProxies`, and not at all when it is empty. A build on the same machine could otherwise forge `X-Forwarded-For` past the MCP allowlist, per-key address limits and throttling. `deploy/install.sh` sets `127.0.0.1,::1` for the same-machine Caddy (`--trusted-proxies` changes it).
- **Upgrade:** Jobs without their own working directory no longer share `DefaultWorkingDirectory`; each gets `<DefaultWorkingDirectory>/<job id>`. `walis-runner --workdir` works the same way. A custom working directory may not overlap the directory of a job the caller cannot modify (checked on save and before the build).
- **Upgrade:** Job names are unique, compared without case. On upgrade, later jobs with a duplicate name are renamed `name (2)`, `name (3)`…
- **Upgrade:** Changing your own password ends every session, the current one included; the browser receives a new cookie session, a bearer client signs in again. `POST /api/auth/logout-all` ends every session of the signed-in account.
- **Upgrade:** New passwords need at least 12 characters. Password hashes use PBKDF2-SHA256 with 600,000 iterations; older hashes are rewritten at the next successful sign-in.
- The macOS sandbox keeps `/opt/homebrew`, `/usr/local` and every directory on the service's PATH that the service account can write to readable but not writable, and denies the action pack directories (`Walis:ActionPacks`) entirely. The action pack update job is registered as an admin-only trusted job; the self-update and website-check jobs are admin-only to view.
- Step `$WALIS_ENV_FILE` and `$WALIS_OUTPUT_FILE` live in a private `.walis/` directory inside the workspace (with a `.gitignore`) instead of the shared temp directory.
- Administrator actions whose effect outlives the session ask for the current password again (`X-Walis-Reauth`, 403 `code: reauth_required` otherwise): creating an API key, resetting another account's password or one-time password, granting the admin role, turning sign-in or auditing off, lifting a lockout. The admin UI asks and retries.
- Runners gain `MinimumRunRole`: a runner set to `admin` only takes administrator jobs, so operators cannot send scripts to a host that holds deployment credentials.
- Refused requests (`401`/`403` in the middleware, job run-role refusals, webhook signature, replay and rate-limit refusals) are audited as `access-denied`, at most once a minute per actor and reason, and counted in `walis_access_denied_total{reason}`. Security alerts are sent at most once per kind every five minutes with the number held back; lockouts of accounts that do not exist are audited but not notified. Security events are recorded even while auditing is off. A name that matches no account is stored as its first three characters, length and a short hash.
- IPv6 sources are counted per /64 for rate limits and sign-in throttling. A full throttle table evicts the oldest unlocked rows instead of refusing every new identity. Sign-in, OTP and setup have their own per-source ceiling (`Security:AuthRateLimitPerMinute`, default 30). The account bucket is keyed by the matched account, so accent- or width-insensitive collations no longer give each spelling its own five attempts.
- Stored secrets use `enc:v2:` with the table and column bound as AES-GCM associated data; `enc:v1:` values are still read and rewritten on the next write. Slack and webhook notification URLs are encrypted and shown masked. `Security:EncryptionKey` is removed from the process environment after startup. Password-type parameters no longer enter the unsalted trigger hash, and must be at least 4 characters.
- SMTP refuses private and loopback hosts unless `Security:OutboundAllowPrivateNetwork=true`. Claude requests go through the guarded `ai` HTTP client and honour the provider's base URL.
- Restores normalize the restored settings (audit retention floor) and record what changed; audit rows merged from an archive are marked `［還原自備份］` and cannot be dated after the restore; user rows need a valid role (the column default is now `viewer`). Extra backup paths never include `secret.key` or `setup-code.txt`. The backup header's iteration count is capped.
- Push webhooks never start trusted jobs and answer with the number of builds only. Runner report endpoints answer 404 for builds the caller cannot see. A caller-supplied `triggeredBy` is stored as `manual:<text>`. Imports apply the job form's rules (no credentials in repository URLs, run role at least operator, trusted jobs changed by administrators only). `GET /api/ai/requests` is operator-only and omits the actor; viewers only learn whether a provider is usable.
- MCP tools wrap logs and scripts in fixed "untrusted content" delimiters, strip ANSI and control characters, and say in their descriptions that returned text is data, not instructions.
- `latest.json` is signed (`latest.json.sig`); `install.sh` verifies it before using it to pick the latest version. `walis-runner` accepts `WALIS_PASSWORD` and `--password-file`, and refuses credential files readable by other accounts; its cache lives under the account's own application data directory. The stdio MCP bridge removes `WALIS_API_KEY` from its environment.
- Upgrading disabled no legacy PBKDF2 API keys because of a malformed `LIKE` pattern; they are now disabled.
- Microsoft.Data.Sqlite 10.0.13. The native SQLite stays at 3.53.3 (`SQLitePCLRaw.bundle_e_sqlite3` 2.1.13 pinned): 2.1.14 carries SQLite 3.54.0, whose 32-bit heap overflow fixed in 3.54.1 would reach the `linux-arm` archive.

### Follow-up review on 2026-10-23

> ［本版新增］The previous release did not include the following corrections.

- Require an administrator workspace allowlist before an ordinary job can reopen a directory inside the service account's home or installation.
- Restrict changes to trusted jobs and their environment variables to administrators, while preserving the configured run permission.
- Complete startup recovery before accepting new work, so a newly claimed build cannot be mistaken for an interrupted build.
- Preserve `Skipped` when a remote runner reports a conditional step result.
- Make OSV operational errors fail the build, and reject unsupported severity thresholds. The new `all` policy uses Trivy or OSV; Grype continues to support severity thresholds.
- Generate Microsoft SBOM output using its manifest directory and explicitly selected SPDX 2.2 format; reject unsupported format combinations.
- Encode NuGet SHA-512 digests as hexadecimal in CycloneDX, exclude unresolved project dependency references, and derive embedded runtime versions from the current release staging directory.

### Isolation and the service account

- Builds without an isolation boundary are refused by default on a fresh Production install that listens on a non-loopback address; the first-administrator screen and the settings page explain that an unisolated build lets anyone who can edit or run a job read every secret, and record who accepted it (`UnisolatedBuildsAcknowledged*`). Existing databases keep their stored setting.
- The macOS sandbox now also denies the installation directory (content root, `releases/`, `current`, `$PREFIX/var`) and the service account's home directory, adds `(deny signal)`, `(deny mach-lookup)` and `(deny process-info*)` (`SandboxDenyProcessControl`), and lets the workspace through explicitly. Jobs gain "deny network" and the admin-only "trusted job (no sandbox)" option; the self-update job is registered as a trusted job.
- Every build step runs in its own process group (`setsid` on Linux, a `perl setpgrp` wrapper on macOS, a Job Object on Windows) and leftover background processes are killed when the step ends.
- Running builds can be cancelled (`POST /api/builds/{id}/cancel`); remote runners are told through the `Walis-Cancel-Requested` header.
- The managed workspace is now a fixed `data/workspaces/<job id>` directory reclaimed at startup; a new "clear the workspace after every build" setting (system default plus per-job override) cleans up after artifacts and caches are saved.
- macOS install: `releases/` and `current` are root-owned; the self-update job builds into `var/staging` and the root restarter validates and promotes the release. Re-run `sudo ./deploy/install.sh` and `./deploy/register-job.sh` after upgrading.
- The API and `walis-runner` remove `Security:BootstrapToken`, the connection string and `WALIS_TOKEN` from their own process environment after reading them; the systemd units use `EnvironmentFile=/etc/walis/*.env`, gain the remaining hardening directives and `DOTNET_EnableDiagnostics=0`; `pg_dump` receives its password through a private `PGPASSFILE`; `walis-runner --token-file`.
- Environment variable names may no longer start with `WALIS_`; `$WALIS_ENV_FILE` cannot overwrite `WALIS_*`, `HOME` or `ZDOTDIR`.
- `.dockerignore` excludes `data/`, `*.db` and `setup-code.txt`.

### Secrets

- Secrets stored in the database (AI provider keys, secret variables, settings passwords and cloud credentials, password parameters, TOTP secrets) are encrypted with AES-256-GCM. The key comes from `Security:EncryptionKey` or is generated once into `<data directory>/secret.key`; restoring a backup on another machine needs that key, and the settings page can clear values that no longer decrypt.
- Build-log masking also covers base64, URL, JSON and HTML encodings of a secret and the credentials embedded in repository URLs; the server re-masks remote runner reports with password parameters too; a disabled masker is announced on the first log line.
- Repository URLs with embedded credentials are rejected when saving a job (use a secret variable and a credential helper; see docs/actions.md).
- The browser sign-in response no longer returns the session token; CLI clients request `?bearer=true` and receive a separate one-hour bearer session. `walis-runner --user/--password` re-signs in automatically.
- AI prompts are redacted before they leave the server; the new "allow operators to use AI" setting can restrict AI features to administrators.

### Authentication and API

- MCP source-address allowlist: `McpAllowedAddresses` (IPs or CIDR ranges) rejects other addresses before authentication; API keys can carry their own allowed addresses (all audiences, including runner keys).
- `Security:RequireSetupCode`; when the server listens on a non-loopback address in Production, the code-free local shortcut for the first administrator also requires that no forwarding headers are present and that the Host port equals the listening port.
- Enabling a one-time password requires the current password.
- Push webhook replay protection keys on both the delivery id and the body hash; `X-Walis-Timestamp` is enforced within ten minutes when present.
- `Security:OutboundAllowPrivateNetwork` (default false): notifications, AI and backup clients no longer connect to private networks.
- `GET /api/settings` no longer returns the default working directory to viewers; the job run role cannot be lowered below operator; deleting a runner disables the API keys bound to it, a paused runner cannot be re-registered, and keys cannot be bound to `local`; a duplicate build result is answered with 409 and does not re-notify.

### Audit log

- Audit rows carry the source address, outcome, request id and job id; sign-in failures, OTP failures, setup-code failures and lockouts are recorded; build triggers from every source (REST, MCP, schedule, push, downstream), cancellation, artifact downloads, notification channels, runner pause, locales, exports, `reveal=true` and the plugin trust flag are audited.
- Settings changes name the changed fields; disabling the audit log, changing retention or security settings leaves its own entry before the change; `AuditRetentionDays` (default 365, minimum 30) is separate from build retention; restores merge the audit table instead of replacing it.
- `GET /api/audit/page` with filters, `GET /api/audit/export.csv`; security alerts (lockouts, audit disabled, admin created or promoted) can be sent to notification channels; `/metrics` gains `walis_sign_in_failures_total` and `walis_lockouts_total`.
- Names are rejected when they contain control characters, and audit summaries and log lines are sanitized.

### Supply chain and SBOM

- Release archives embed .NET 10.0.13 (`global.json` pins SDK 10.0.402; `deploy/release.sh` refuses an older runtime and writes `RUNTIME.txt`); `mcp/` is self-contained.
- `SHA256SUMS.txt` is signed with Ed25519 (`deploy/release-signing.pub`); `install.sh` verifies the signature before trusting a checksum.
- NuGet lock files with locked restore and `NuGetAudit`, container base images pinned by digest, `npm ci` only, vite 8.2.4+ and source-map-js 1.2.2+; `internal/check-dependencies.sh` and `internal/check-sqlite-version.sh` run before a release.
- Added built-in actions `sbom.generate` (CycloneDX 1.5 JSON and SPDX 2.3 JSON support with tool fallback) and `sbom.scan` (Grype, Trivy, and OSV-Scanner support with vulnerability quality gates).
- `deploy/release.sh` now automatically generates an official CycloneDX 1.5 SBOM (`walis-${VERSION}.cdx.json`) alongside release archives, signed and recorded in `SHA256SUMS.txt`.

## 1.0.1 — 2026-10-21

- The row action menu in the jobs and accounts tables is no longer clipped by the table container.

## 1.0.0 — 2026-10-20

First public release.

- One self-contained binary per platform (Windows, macOS, Linux, Alpine; x64 and ARM64, plus 32-bit ARM for Raspberry Pi) with the admin UI built in; `walis-runner` for remote build agents.
- Jobs as shell scripts or action flows, compiled to one script; steps with their own status, log, exit code and duration; parallel steps and step dependencies; matrix builds; build parameters; caching and artifacts.
- Triggers: manual, cron, authenticated push webhooks (GitHub, GitLab, plain JSON) and downstream jobs.
- Import from Jenkins: Jenkinsfile and `config.xml` converted into draft jobs.
- A fresh install works the first time: reachable over plain HTTP from any machine, a setup code for creating the first administrator remotely, builds on every platform. `Security__RequireHttps`, `AllowedHosts` and the isolation-boundary switch tighten it afterwards.
- Sign-in with roles and per-job view/run roles, one-time passwords, API keys scoped to MCP, REST or one runner, audit log, Prometheus metrics.
- MCP endpoint at `/mcp` for AI agents, plus a stdio bridge.
- Plugins in sandboxed JavaScript that return shell, with `@walisayu/plugin-sdk`.
- SQLite by default; PostgreSQL, MySQL/MariaDB and SQL Server for multi-node deployments.
- Encrypted, engine-independent backups to local disk, S3-compatible storage, Dropbox, Google Drive or OneDrive; restore into any supported engine.
- Notifications by email, Slack or signed webhooks; UI in Traditional Chinese, English and Japanese.
