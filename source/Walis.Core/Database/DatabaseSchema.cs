namespace Walis.Core.Database;

/// <summary>Abstract column types; the concrete SQL type is decided by each dialect.</summary>
public enum ColumnKind
{
    /// <summary>Identifier: text in SQLite, uuid in PostgreSQL, uniqueidentifier in SQL Server.</summary>
    Guid,

    /// <summary>Unbounded text (scripts, logs, JSON).</summary>
    Text,

    /// <summary>Text with a length cap; indexes and primary keys must use this, MySQL rejects keys without a length.</summary>
    ShortText,

    /// <summary>Times are always stored as UTC.</summary>
    Timestamp,

    Int,

    BigInt,

    Bool
}

public sealed record ColumnDefinition(
    string Name,
    ColumnKind Kind,
    bool Nullable = true,
    string? Default = null,
    int Length = 200,
    bool CaseInsensitive = false);

public sealed record ForeignKeyDefinition(string Column, string ReferencedTable, string ReferencedColumn);

public sealed record TableDefinition(
    string Name,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<ForeignKeyDefinition>? ForeignKeys = null,
    IReadOnlyList<IReadOnlyList<string>>? Unique = null,
    string? Check = null);

public sealed record IndexDefinition(string Name, string Table, IReadOnlyList<string> Columns, bool Unique = false);

/// <summary>
///     One schema shared by all four databases. Schema changes go here: add new tables directly, add columns to existing tables directly;
///     <see cref="DatabaseInitializer" /> compares with the current state and fills the gaps, so no separate migration scripts are needed.
/// </summary>
public static class DatabaseSchema
{
    public static IReadOnlyList<TableDefinition> Tables { get; } =
    [
        new("Jobs",
            [
                Id(),
                Short("Name", 200, false),
                Short("Project"),
                Text("Description"),
                Text("Command", false),
                Text("WorkingDirectory"),
                Short("Repository", 500),
                Short("Branch", 200, false, "'main'"),
                Short("RunnerName"),
                Number("TimeoutMinutes", false, "30"),
                Text("ArtifactPath"),
                Short("TriggerKind", 40, false, "'manual'"),
                Short("Cron", 120),
                Flag("NotifyOnFailure", true),
                Flag("IsDisabled"),
                Short("ScriptMode", 40, false, "'source'"),
                Text("ActionsJson"),
                Text("CachePaths"),

                // Nullable on purpose: null means the job follows the system setting, so existing jobs keep their behaviour.
                new ColumnDefinition("CleanWorkspace", ColumnKind.Bool),
                new ColumnDefinition("CleanWorkspaceAfterBuild", ColumnKind.Bool),
                Text("MatrixJson"),
                Short("ViewRole", 40, false, "'viewer'"),
                Short("RunRole", 40, false, "'operator'"),
                Text("ParametersJson"),
                Flag("AllowConcurrentBuilds", true),
                Text("DownstreamJson"),
                Flag("DenyNetwork"),
                Flag("TrustedUnsandboxed"),
                Time("CreatedAt", false),
                Time("UpdatedAt", false)
            ],
            ["Id"]),

        new("Builds",
            [
                Id(),
                new ColumnDefinition("JobId", ColumnKind.Guid, false),
                Number("Number", false),
                Short("Status", 40, false),
                Short("TriggeredBy", 200, false),
                Short("RunnerName"),
                Time("QueuedAt", false),
                Time("StartedAt"),
                Time("FinishedAt"),
                Number("ExitCode"),
                Text("Log", false, "''"),
                Short("MatrixLabel"),
                Text("MatrixValues"),
                Text("ParametersJson"),
                Text("SecretParametersJson"),
                // When several nodes share the database, records which node is running this build;
                // a restarting node must only reclaim its own builds and never mark someone else's running build as failed.
                Short("Node"),
                // Idempotency reference from an external caller; not unique because a matrix trigger shares one reference across its builds.
                Short("ExternalRef", Models.BuildExternalRefs.MaximumLength),
                Short("TriggerHash", 64),
                // Set when someone cancels a build that is already running; the executor (local worker or remote runner)
                // polls it and winds the build up as Canceled.
                Time("CancelRequestedAt")
            ],
            ["Id"],
            [new ForeignKeyDefinition("JobId", "Jobs", "Id")],
            [["JobId", "Number"]]),

        new("BuildSteps",
            [
                Id(),
                new ColumnDefinition("BuildId", ColumnKind.Guid, false),
                Number("Number", false),
                Short("Name", 200, false),
                Text("Command", false),
                Flag("ContinueOnError"),
                Flag("Parallel"),
                Short("RunWhen", 40, false, "'success'"),
                Number("Retries", false, "0"),
                Short("StepKey", 40),
                Text("DependsOnJson"),
                Text("Condition"),
                Text("OutputsJson"),
                Short("Status", 40, false),
                Time("StartedAt"),
                Time("FinishedAt"),
                Number("ExitCode"),
                Text("Log", false, "''")
            ],
            ["Id"],
            [new ForeignKeyDefinition("BuildId", "Builds", "Id")],
            [["BuildId", "Number"]]),

        new("Runners",
            [
                Id(),
                Short("Name", 200, false),
                Short("OperatingSystem", 300, false, "''"),
                Short("Version", 60, false, "''"),
                Short("Labels", 500, false, "''"),
                Number("TotalSlots", false, "1"),
                Flag("IsPaused"),
                Flag("IsLocal"),
                Time("LastSeenAt", false),
                Time("CreatedAt", false),

                // The lowest job RunRole this runner accepts; admin reserves the host for administrator jobs.
                Short("MinimumRunRole", 40, false, "'operator'")
            ],
            ["Id"],
            Unique: [["Name"]]),

        new("EnvironmentVariables",
            [
                Id(),
                Short("Name", 200, false),
                Text("Value", false),
                Flag("IsSecret"),
                new ColumnDefinition("JobId", ColumnKind.Guid),
                Time("UpdatedAt", false)
            ],
            ["Id"],
            [new ForeignKeyDefinition("JobId", "Jobs", "Id")]),

        new("NotificationChannels",
            [
                Id(),
                Short("Kind", 40, false),
                Short("Name", 200, false),
                Text("Target", false, "''"),
                Flag("IsEnabled"),
                Time("UpdatedAt", false)
            ],
            ["Id"]),

        new("NotificationLogs",
            [
                Id(),
                Time("SentAt", false),
                Short("Event", 80, false),
                Short("ChannelKind", 40, false),
                Short("Target", 400, false),
                Flag("Succeeded"),
                Text("Detail")
            ],
            ["Id"]),

        new("Settings",
            [
                Number("Id", false),
                Text("Payload", false),
                Time("UpdatedAt", false)
            ],
            ["Id"],
            Check: "Id = 1"),

        new("Locales",
            [
                Short("Code", 40, false),
                Short("Name", 100, false),
                Short("NativeName", 100, false),
                Flag("IsEnabled", true),
                Number("SortOrder", false, "0"),
                Time("UpdatedAt", false)
            ],
            ["Code"]),

        new("Translations",
            [
                Short("Locale", 40, false),
                Short("Source", 400, false),
                Text("Value", false),
                Time("UpdatedAt", false)
            ],
            ["Locale", "Source"],
            [new ForeignKeyDefinition("Locale", "Locales", "Code")]),

        new("ActionDefinitions",
            [
                Short("Id", 120, false),
                Text("Manifest", false),
                Time("InstalledAt", false)
            ],
            ["Id"]),

        new("AiProviders",
            [
                Id(),
                Short("Kind", 40, false),
                Short("Name", 100, false),
                Text("ApiKey"),
                Short("Model", 120, false, "''"),
                Short("BaseUrl", 400),
                Flag("IsEnabled"),
                Flag("IsDefault"),
                Time("UpdatedAt", false)
            ],
            ["Id"],
            Unique: [["Kind"]]),

        new("AiRequests",
            [
                Id(),
                Time("StartedAt", false),
                Short("ProviderKind", 40, false),
                Short("Model", 120, false),
                Short("Purpose", 80, false),
                new ColumnDefinition("BuildId", ColumnKind.Guid),
                new ColumnDefinition("JobId", ColumnKind.Guid),
                Short("Actor", 200, false, "'system'"),
                Number("PromptChars", false, "0"),
                Number("InputTokens"),
                Number("OutputTokens"),
                Number("DurationMs", false, "0"),
                Flag("Succeeded"),
                Text("Error")
            ],
            ["Id"]),

        // The first setup can only succeed once. A separate unique claim keeps two API nodes from creating two initial admins at the same time.
        new("SecurityClaims",
            [
                Short("Name", 80, false),
                Short("Owner", 120, false),
                Time("ClaimedAt", false)
            ],
            ["Name"]),

        // Sign-in throttling must survive across nodes and restarts, otherwise an attacker resets the count by rotating nodes or restarting the service.
        new("SignInRateLimits",
            [
                Short("BucketKey", 240, false),
                Number("Failures", false),
                Time("FirstFailureAt", false),
                Time("LockedUntil", false)
            ],
            ["BucketKey"]),

        // Webhook deliveries and per-source counts also live in the shared database so HA nodes cannot each accept the same event.
        new("WebhookDeliveries",
            [
                Short("DeliveryHash", 80, false),
                Time("ExpiresAt", false)
            ],
            ["DeliveryHash"]),

        new("WebhookRateLimits",
            [
                Short("ClientKey", 80, false),
                Time("WindowStartedAt", false),
                Number("RequestCount", false)
            ],
            ["ClientKey"]),

        new("Users",
            [
                Id(),
                new ColumnDefinition("Username", ColumnKind.ShortText, false, Length: 120, CaseInsensitive: true),
                Short("DisplayName", 200, false),
                Text("PasswordHash", false),
                Short("Role", 40, false, "'viewer'"),
                Flag("IsDisabled"),
                Short("TotpSecret", 120),
                Time("TotpEnabledAt"),
                new ColumnDefinition("LastTotpCounter", ColumnKind.BigInt),
                Text("RecoveryCodes"),
                Time("CreatedAt", false),
                Time("LastLoginAt")
            ],
            ["Id"],
            Unique: [["Username"]]),

        new("Sessions",
            [
                Short("Token", 120, false),
                new ColumnDefinition("UserId", ColumnKind.Guid, false),
                Time("CreatedAt", false),
                Time("ExpiresAt", false),
                // A short-lived session handed to a CLI as a bearer token; never accepted from the cookie, and vice versa.
                Flag("IsBearer")
            ],
            ["Token"],
            [new ForeignKeyDefinition("UserId", "Users", "Id")]),

        new("Plugins",
            [
                Id(),
                Short("PluginId", 120, false),
                Text("Manifest", false),
                Text("Code", false),
                Flag("IsDisabled"),
                Flag("IsTrusted"),
                Time("InstalledAt", false),
                Time("UpdatedAt", false)
            ],
            ["Id"],
            Unique: [["PluginId"]]),

        new("ApiKeys",
            [
                Id(),
                Short("Name", 200, false),
                Short("Prefix", 40, false),
                Text("Hash", false),
                Short("Role", 40, false),
                // Upgraded existing keys keep their original API capability; new keys default to mcp at the endpoint for safety.
                Short("Scope", 40, false, "'api'"),
                Flag("IsDisabled"),
                Time("CreatedAt", false),
                Short("CreatedBy"),
                Time("ExpiresAt"),
                Time("LastUsedAt"),

                // When set, this key may only act as that runner: claim, heartbeat and report results; every other API is refused.
                Short("RunnerName"),

                // Optional source allowlist (addresses or CIDR ranges, one per line); the middleware refuses the key from anywhere else.
                Text("AllowedAddresses")
            ],
            ["Id"],
            Unique: [["Prefix"]]),

        new("AuditLogs",
            [
                Id(),
                Time("OccurredAt", false),
                Short("Actor", 200, false),
                Short("Action", 80, false),
                Short("EntityType", 80, false),
                Short("EntityId", 120),
                Text("Summary", false),
                Short("RemoteAddress", 64),
                Short("Outcome", 16, false, "'success'"),
                Short("RequestId", 64),
                new ColumnDefinition("JobId", ColumnKind.Guid)
            ],
            ["Id"]),

        new("BuildArtifacts",
            [
                Id(),
                new ColumnDefinition("BuildId", ColumnKind.Guid, false),
                Short("RelativePath", 500, false),
                new ColumnDefinition("SizeBytes", ColumnKind.BigInt, false),
                Time("CreatedAt", false)
            ],
            ["Id"],
            [new ForeignKeyDefinition("BuildId", "Builds", "Id")]),

        new("BackupRuns",
            [
                Id(),
                Time("StartedAt", false),
                Time("FinishedAt"),
                Short("Status", 40, false),
                Short("TriggeredBy", 200, false),
                new ColumnDefinition("SizeBytes", ColumnKind.BigInt, false, "0"),
                Short("FileName", 400),
                Text("Log", false, "''")
            ],
            ["Id"]),

        // The brief state after the password passed but the one-time password has not been entered yet. Stored in the database rather than memory
        // because with several nodes the second step may be routed to another node, and a challenge missing from memory would block the sign-in.
        new("OtpChallenges",
            [
                Short("Token", 120, false),
                new ColumnDefinition("UserId", ColumnKind.Guid, false),
                Time("CreatedAt", false),
                Time("ExpiresAt", false)
            ],
            ["Token"],
            [new ForeignKeyDefinition("UserId", "Users", "Id")]),

        // With several nodes only one should run scheduling and cleanup; the leases in this table decide which.
        new("Leases",
            [
                Short("Name", 80, false),
                Short("Owner", 200, false),
                Time("AcquiredAt", false),
                Time("ExpiresAt", false)
            ],
            ["Name"])
    ];

    public static IReadOnlyList<IndexDefinition> Indexes { get; } =
    [
        // Downstream triggers, MCP and imports find jobs by name; two jobs with one name made that ambiguous. The initializer
        // renames older duplicates before creating it.
        new("UX_Jobs_Name", "Jobs", ["Name"], Unique: true),
        new("IX_Builds_JobId_QueuedAt", "Builds", ["JobId", "QueuedAt DESC"]),
        new("IX_Builds_Status", "Builds", ["Status"]),
        new("IX_Builds_JobId_ExternalRef", "Builds", ["JobId", "ExternalRef"]),
        new("IX_BuildSteps_BuildId", "BuildSteps", ["BuildId", "Number"]),
        new("IX_NotificationLogs_SentAt", "NotificationLogs", ["SentAt DESC"]),
        new("IX_AiRequests_StartedAt", "AiRequests", ["StartedAt DESC"]),
        new("IX_AuditLogs_OccurredAt", "AuditLogs", ["OccurredAt DESC"]),
        new("IX_AuditLogs_Entity", "AuditLogs", ["EntityType", "EntityId"]),
        new("IX_AuditLogs_Actor", "AuditLogs", ["Actor"]),
        new("IX_BuildArtifacts_BuildId", "BuildArtifacts", ["BuildId"]),
        new("IX_BackupRuns_StartedAt", "BackupRuns", ["StartedAt DESC"]),
        new("IX_SignInRateLimits_FirstFailureAt", "SignInRateLimits", ["FirstFailureAt"]),
        new("IX_WebhookDeliveries_ExpiresAt", "WebhookDeliveries", ["ExpiresAt"]),
        new("IX_WebhookRateLimits_WindowStartedAt", "WebhookRateLimits", ["WindowStartedAt"])
    ];

    private static ColumnDefinition Id()
    {
        return new ColumnDefinition("Id", ColumnKind.Guid, false);
    }

    private static ColumnDefinition Text(string name, bool nullable = true, string? @default = null)
    {
        return new ColumnDefinition(name, ColumnKind.Text, nullable, @default);
    }

    private static ColumnDefinition Short(string name, int length = 200, bool nullable = true, string? @default = null)
    {
        return new ColumnDefinition(name, ColumnKind.ShortText, nullable, @default, length);
    }

    private static ColumnDefinition Time(string name, bool nullable = true)
    {
        return new ColumnDefinition(name, ColumnKind.Timestamp, nullable);
    }

    private static ColumnDefinition Flag(string name, bool value = false)
    {
        return new ColumnDefinition(name, ColumnKind.Bool, false, value ? "1" : "0");
    }

    private static ColumnDefinition Number(string name, bool nullable = true, string? @default = null)
    {
        return new ColumnDefinition(name, ColumnKind.Int, nullable, @default);
    }
}