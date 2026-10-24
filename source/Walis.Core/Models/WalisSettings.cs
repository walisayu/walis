using Walis.Core.Backup;

namespace Walis.Core.Models;

/// <summary>System settings; stored as a single JSON row in the Settings table.</summary>
public sealed class WalisSettings
{
    // General
    public string SiteName { get; set; } = "Walis 建置管理中心";
    public string BaseUrl { get; set; } = "http://127.0.0.1:11126";
    public string TimeZone { get; set; } = "Asia/Taipei";
    public string Locale { get; set; } = "zh-Hant";

    // Builds
    public int MaxConcurrentBuilds { get; set; } = 2;
    public int DefaultTimeoutMinutes { get; set; } = 30;
    public int LogRetentionDays { get; set; } = 90;
    public string? DefaultWorkingDirectory { get; set; }
    public string? DefaultShell { get; set; }
    public bool KeepArtifacts { get; set; } = true;
    public bool CleanWorkspace { get; set; }

    /// <summary>
    ///     Clears the workspace after every build, success or failure, once artifacts and the cache have been saved, so checked-out
    ///     sources, tokens written by the script and password parameters do not linger between builds. Jobs may override it.
    /// </summary>
    public bool CleanWorkspaceAfterBuild { get; set; }

    // Security
    public bool RequireSignIn { get; set; } = true;

    /// <summary>Requires every account to set up a one-time password; accounts without one are sent to the enrollment flow after sign-in.</summary>
    public bool RequireOtp { get; set; }

    public bool AuditLog { get; set; } = true;

    /// <summary>
    ///     How long audit rows are kept, separate from the build history: an investigation usually reaches further back than the
    ///     build logs anyone wants to keep, and a short build retention must not quietly erase the audit trail. At least 30 days.
    /// </summary>
    public int AuditRetentionDays { get; set; } = DefaultAuditRetentionDays;

    public const int DefaultAuditRetentionDays = 365;
    public const int MinimumAuditRetentionDays = 30;
    public const int MaximumAuditRetentionDays = 3650;

    public bool MaskSecrets { get; set; } = true;

    /// <summary>
    ///     Whether operators may use the AI features that send job scripts and build logs to the configured provider.
    ///     Administrators are never affected; turning this off keeps that data flow in admin hands only.
    /// </summary>
    public bool AllowOperatorAi { get; set; } = true;
    public string CorsOrigins { get; set; } = "http://127.0.0.1:11124\nhttp://localhost:11124";

    /// <summary>The required shared secret for the push webhook; when unset the endpoint fails closed and accepts no events.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    ///     Source addresses allowed to reach /mcp, one IPv4/IPv6 address or CIDR range per line. Empty means unrestricted;
    ///     once set, every other peer is refused before any credential is looked at, and loopback is not implied.
    /// </summary>
    public string McpAllowedAddresses { get; set; } = string.Empty;

    // Build isolation
    /// <summary>Wraps every step in the operating-system sandbox where the platform has one (macOS).</summary>
    public bool IsolateBuilds { get; set; } = true;

    /// <summary>
    ///     Whether a script may run where no isolation boundary exists. On by default so a fresh install runs builds on every
    ///     platform; the build log and the settings page say when a build ran unisolated. An admin turns it off to require a
    ///     boundary, and then a runner without a sandbox or approved external isolation refuses the build instead.
    /// </summary>
    public bool AllowUnisolatedBuilds { get; set; } = true;

    /// <summary>
    ///     An administrator has read the explanation that without an isolation boundary anyone who can edit or run a job
    ///     reads every secret, and accepted it. Shown once on the settings page and the first-admin screen until set.
    /// </summary>
    public bool UnisolatedBuildsAcknowledged { get; set; }

    /// <summary>When and by whom the risk was accepted; stamped by the server, never taken from the request.</summary>
    public DateTime? UnisolatedBuildsAcknowledgedAt { get; set; }

    public string? UnisolatedBuildsAcknowledgedBy { get; set; }

    /// <summary>
    ///     macOS sandbox: also deny signals to other processes, Mach service lookups and reading other processes' information
    ///     (<see cref="Execution.BuildIsolation.ProcessControlRules" />). On by default; off restores the paths-only profile.
    /// </summary>
    public bool SandboxDenyProcessControl { get; set; } = true;

    /// <summary>
    ///     Names of remote runners the deployer has placed inside a dedicated VM/container isolation boundary, one per line.
    ///     Only consulted when a boundary is required (<see cref="AllowUnisolatedBuilds" /> off). The runner must still declare
    ///     external itself; isolation counts as satisfied only when server and runner agree.
    /// </summary>
    public string ExternallyIsolatedRunners { get; set; } = string.Empty;

    /// <summary>Paths besides the data directory that builds must not access, one per line.</summary>
    public string SandboxDeniedPaths { get; set; } = string.Empty;

    /// <summary>
    ///     Allowed workspaces, one per line. When empty, ordinary commands are unrestricted; when clean/cache/artifact
    ///     file I/O done by the host process is enabled it must be set (except the program's own per-job default workspace).
    /// </summary>
    public string AllowedWorkingDirectories { get; set; } = string.Empty;

    // Backups
    /// <summary>When enabled, BackupWorker runs backups on the Cron schedule.</summary>
    public bool BackupEnabled { get; set; }

    public string BackupCron { get; set; } = "30 3 * * *";

    /// <summary>The local directory for backup files; empty means backups under the data directory.</summary>
    public string? BackupDirectory { get; set; }

    public int BackupKeepDays { get; set; } = 14;

    /// <summary>
    ///     The encryption passphrase. Backups contain password hashes and assorted credentials, so every backup is forced through
    ///     Walis's versioned, chunked AES-256-GCM authenticated encryption format.
    /// </summary>
    public string? BackupPassphrase { get; set; }

    /// <summary>Paths to pack alongside Walis's own data, one per line.</summary>
    public string BackupPaths { get; set; } = string.Empty;

    /// <summary>Whether to back up PostgreSQL with pg_dump as well.</summary>
    public bool BackupPostgresEnabled { get; set; }

    public string BackupPostgresHost { get; set; } = "127.0.0.1";

    public int BackupPostgresPort { get; set; } = 5432;

    public string? BackupPostgresUser { get; set; }

    public string? BackupPostgresPassword { get; set; }

    /// <summary>The databases to back up, one per line; empty backs up everything except templates.</summary>
    public string BackupPostgresDatabases { get; set; } = string.Empty;

    /// <summary>The location of pg_dump; empty searches PATH.</summary>
    public string? BackupPgDumpPath { get; set; }

    /// <summary>Backup destinations.</summary>
    public List<BackupTarget> BackupTargets { get; set; } = [];

    // Notifications
    public bool NotifyOnFailure { get; set; } = true;
    public bool NotifyOnRecovery { get; set; } = true;
    public bool NotifyOnSuccess { get; set; }
    public bool NotifyOnTimeout { get; set; } = true;
    public bool NotifyOnRunnerOffline { get; set; } = true;

    /// <summary>Account lockouts, auditing switched off, administrator accounts created or promoted. Sent through every enabled channel, also during quiet hours.</summary>
    public bool NotifyOnSecurityAlert { get; set; } = true;

    public string QuietHoursStart { get; set; } = "23:00";
    public string QuietHoursEnd { get; set; } = "08:00";

    /// <summary>
    ///     HMAC-SHA256 key for outgoing webhook notifications. When set every webhook request carries
    ///     <c>Walis-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c> over "&lt;t&gt;.&lt;raw body&gt;"; when unset requests stay unsigned as before.
    /// </summary>
    public string? NotificationSigningSecret { get; set; }

    // SMTP settings needed to send email; when unset nothing is sent and only the notification record is kept.
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }
    public string? SmtpFrom { get; set; }

    public bool IsEventEnabled(string eventKind)
    {
        return eventKind switch
        {
            NotificationEventKind.BuildFailed => NotifyOnFailure,
            NotificationEventKind.BuildRecovered => NotifyOnRecovery,
            NotificationEventKind.BuildSucceeded => NotifyOnSuccess,
            NotificationEventKind.BuildTimedOut => NotifyOnTimeout,
            NotificationEventKind.RunnerOffline => NotifyOnRunnerOffline,
            NotificationEventKind.SecurityAlert => NotifyOnSecurityAlert,
            _ => false
        };
    }

    /// <summary>During quiet hours only failure and timeout notifications are sent.</summary>
    public bool IsQuietHour(TimeOnly localTime)
    {
        if (!TimeOnly.TryParse(QuietHoursStart, out var start) ||
            !TimeOnly.TryParse(QuietHoursEnd, out var end)) return false;

        return start <= end
            ? localTime >= start && localTime < end
            : localTime >= start || localTime < end;
    }
}