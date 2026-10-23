using System.Data;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dapper;
using Walis.Core.Models;

namespace Walis.Core.Database;

public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Creates and upgrades the schema from <see cref="DatabaseSchema" /> and writes the default data when the database is brand new.
///     All four databases share one definition: missing tables are created, missing columns ALTERed in, missing indexes added, existing data untouched.
/// </summary>
public sealed class DatabaseInitializer(IDbConnectionFactory connectionFactory) : IDatabaseInitializer
{
    /// <summary>
    ///     The settings row written when the database is brand new. The host decides the isolation default from its environment
    ///     and listening address; a database that already has a row is never touched.
    /// </summary>
    public WalisSettings InitialSettings { get; init; } = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var dialect = connectionFactory.Dialect;
        using var connection = connectionFactory.Create();

        if (connection.State != ConnectionState.Open) connection.Open();

        foreach (var table in DatabaseSchema.Tables)
        {
            var existing = await ListColumnsAsync(connection, dialect, table.Name, cancellationToken);

            if (existing.Count == 0)
            {
                await ExecuteAsync(connection, dialect.CreateTable(table), cancellationToken);
                continue;
            }

            // Existing database: only add new columns. A new column must be nullable or have a default, otherwise existing rows cannot be filled.
            foreach (var column in table.Columns.Where(column => !existing.Contains(column.Name)))
                await ExecuteAsync(connection, dialect.AddColumn(table.Name, column), cancellationToken);
        }

        var indexes = new HashSet<string>(
            await connection.QueryAsync<string>(new CommandDefinition(
                dialect.ListIndexesSql, cancellationToken: cancellationToken)),
            StringComparer.OrdinalIgnoreCase);

        if (!indexes.Contains("UX_Jobs_Name")) await RenameDuplicateJobNamesAsync(connection, cancellationToken);

        foreach (var index in DatabaseSchema.Indexes.Where(index => !indexes.Contains(index.Name)))
            await ExecuteAsync(connection, dialect.CreateIndex(index), cancellationToken);

        foreach (var statement in dialect.AdditionalIndexes())
        {
            var name = IndexNameOf(statement);
            if (name is null || indexes.Contains(name)) continue;

            await ExecuteAsync(connection, statement, cancellationToken);
        }

        // API keys used to be password-hashed with an intentionally expensive
        // PBKDF2 verifier. Their public prefixes let an attacker select that
        // work repeatedly, so legacy keys are revoked once and must be rotated
        // to the high-entropy token hash format.
        await connection.ExecuteAsync(new CommandDefinition(
            // PostgreSQL boolean does not accept 1/0, so boolean literals always go through the dialect.
            // Anything that is not the current sha256$ lookup format. (This used to be LIKE 'pbkdf2$$%', which in an interpolated
            // string is two literal dollar signs and never matched the pbkdf2$<iterations>$... format.)
            $"UPDATE ApiKeys SET IsDisabled = {dialect.Boolean(true)} WHERE IsDisabled = {dialect.Boolean(false)} AND Hash NOT LIKE 'sha256$%';",
            cancellationToken: cancellationToken));

        // Session and OTP challenge tokens are now stored only as 64-character
        // SHA-256 lookup hashes. Revoke legacy plaintext rows during upgrade.
        await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM Sessions WHERE {dialect.Length("Token")} <> 64;",
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM OtpChallenges WHERE {dialect.Length("Token")} <> 64;",
            cancellationToken: cancellationToken));

        await BackfillInitialAdminClaimAsync(connection, dialect, cancellationToken);
        await SeedLocalesAsync(connection, dialect, cancellationToken);
        await SeedSettingsAsync(connection, InitialSettings, cancellationToken);
        await SeedLocalRunnerAsync(connection, dialect, cancellationToken);
        await SeedNotificationChannelsAsync(connection, cancellationToken);
        await SeedAiProvidersAsync(connection, dialect, cancellationToken);
        await SeedJobsAsync(connection, cancellationToken);
    }

    private static Task ExecuteAsync(IDbConnection connection, string sql, CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    private static async Task BackfillInitialAdminClaimAsync(
        IDbConnection connection,
        ISqlDialect dialect,
        CancellationToken cancellationToken)
    {
        var hasUsers = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM Users;",
            cancellationToken: cancellationToken));

        if (hasUsers == 0) return;

        var sql = dialect.Upsert(
            "SecurityClaims",
            [
                UpsertColumn.Parameter("Name"),
                UpsertColumn.Parameter("Owner"),
                UpsertColumn.Parameter("ClaimedAt")
            ],
            ["Name"],
            null);

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Name = "initial-admin",
                Owner = "database-upgrade",
                ClaimedAt = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));
    }

    private static async Task<HashSet<string>> ListColumnsAsync(
        IDbConnection connection,
        ISqlDialect dialect,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = await connection.QueryAsync<string>(new CommandDefinition(
            dialect.ListColumnsSql,
            new { Table = table },
            cancellationToken: cancellationToken));

        return new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Extracts the index name from CREATE [UNIQUE] INDEX name ON … to decide whether it needs creating.</summary>
    /// <summary>
    ///     Job names were not unique before UX_Jobs_Name. The oldest job keeps its name and later ones get " (2)", " (3)"…,
    ///     compared without case because MySQL and SQL Server collations are case-insensitive and would refuse the index.
    /// </summary>
    private async Task RenameDuplicateJobNamesAsync(IDbConnection connection, CancellationToken cancellationToken)
    {
        var jobs = (await connection.QueryAsync<(Guid Id, string Name, DateTime CreatedAt)>(new CommandDefinition(
            "SELECT Id, Name, CreatedAt FROM Jobs;", cancellationToken: cancellationToken))).ToList();
        var taken = new HashSet<string>(jobs.Select(job => job.Name.Trim()), StringComparer.OrdinalIgnoreCase);

        foreach (var group in jobs.GroupBy(job => job.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            foreach (var job in group.OrderBy(job => job.CreatedAt).ThenBy(job => job.Id).Skip(1))
            {
                string candidate;
                var suffix = 2;
                do
                {
                    var tail = $" ({suffix++})";
                    var stem = job.Name.Trim();
                    candidate = (stem.Length + tail.Length > 200 ? stem[..(200 - tail.Length)] : stem) + tail;
                } while (!taken.Add(candidate));

                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE Jobs SET Name = @Name WHERE Id = @Id;",
                    new { Name = candidate, job.Id },
                    cancellationToken: cancellationToken));
            }
        }
    }

    private static string? IndexNameOf(string statement)
    {
        var words = statement.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var marker = Array.FindIndex(words, word => word.Equals("INDEX", StringComparison.OrdinalIgnoreCase));
        return marker >= 0 && marker + 1 < words.Length ? words[marker + 1] : null;
    }

    private static async Task SeedLocalesAsync(
        IDbConnection connection,
        ISqlDialect dialect,
        CancellationToken cancellationToken)
    {
        var sql = dialect.Upsert(
            "Locales",
            [
                UpsertColumn.Parameter("Code"),
                UpsertColumn.Parameter("Name"),
                UpsertColumn.Parameter("NativeName"),
                UpsertColumn.Parameter("IsEnabled"),
                UpsertColumn.Parameter("SortOrder"),
                UpsertColumn.Parameter("UpdatedAt")
            ],
            ["Code"],
            null);

        var now = DateTime.UtcNow;
        var locales = new[]
        {
            new
            {
                Code = "zh-Hant", Name = "繁體中文", NativeName = "繁體中文", IsEnabled = true, SortOrder = 0, UpdatedAt = now
            },
            new { Code = "en", Name = "英文", NativeName = "English", IsEnabled = true, SortOrder = 1, UpdatedAt = now },
            new { Code = "ja", Name = "日文", NativeName = "日本語", IsEnabled = true, SortOrder = 2, UpdatedAt = now }
        };

        await connection.ExecuteAsync(new CommandDefinition(sql, locales, cancellationToken: cancellationToken));
    }

    private static async Task SeedSettingsAsync(
        IDbConnection connection,
        WalisSettings initialSettings,
        CancellationToken cancellationToken)
    {
        var exists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM Settings WHERE Id = 1;", cancellationToken: cancellationToken));

        if (exists != 0) return;

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO Settings (Id, Payload, UpdatedAt) VALUES (1, @Payload, @UpdatedAt);",
            new { Payload = JsonSerializer.Serialize(initialSettings), UpdatedAt = DateTime.UtcNow },
            cancellationToken: cancellationToken));
    }

    private static async Task SeedLocalRunnerAsync(
        IDbConnection connection,
        ISqlDialect dialect,
        CancellationToken cancellationToken)
    {
        var sql = dialect.Upsert(
            "Runners",
            [
                UpsertColumn.Parameter("Id"),
                UpsertColumn.Parameter("Name"),
                UpsertColumn.Parameter("OperatingSystem"),
                UpsertColumn.Parameter("Version"),
                UpsertColumn.Parameter("Labels"),
                UpsertColumn.Parameter("TotalSlots"),
                UpsertColumn.Parameter("IsPaused"),
                UpsertColumn.Parameter("IsLocal"),
                new UpsertColumn("LastSeenAt", "@Now"),
                new UpsertColumn("CreatedAt", "@Now")
            ],
            ["Name"],
            null);

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = Guid.NewGuid(),
                Name = Runner.LocalName,
                OperatingSystem = RuntimeInformation.OSDescription,
                Version = typeof(DatabaseInitializer).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                Labels = BuildLocalLabels(),
                TotalSlots = 2,
                IsPaused = false,
                IsLocal = true,
                Now = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));
    }

    private static string BuildLocalLabels()
    {
        var platform = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : "linux";
        return $"{platform},{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()},local";
    }

    private static async Task SeedNotificationChannelsAsync(IDbConnection connection,
        CancellationToken cancellationToken)
    {
        var exists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM NotificationChannels;", cancellationToken: cancellationToken));

        if (exists != 0) return;

        const string sql = """
                           INSERT INTO NotificationChannels (Id, Kind, Name, Target, IsEnabled, UpdatedAt)
                           VALUES (@Id, @Kind, @Name, '', @IsEnabled, @UpdatedAt);
                           """;

        var now = DateTime.UtcNow;
        var channels = new[]
        {
            new
            {
                Id = Guid.NewGuid(), Kind = NotificationChannelKind.Email, Name = "電子郵件", IsEnabled = false,
                UpdatedAt = now
            },
            new
            {
                Id = Guid.NewGuid(), Kind = NotificationChannelKind.Slack, Name = "Slack", IsEnabled = false,
                UpdatedAt = now
            },
            new
            {
                Id = Guid.NewGuid(), Kind = NotificationChannelKind.Webhook, Name = "自訂 Webhook", IsEnabled = false,
                UpdatedAt = now
            }
        };

        await connection.ExecuteAsync(new CommandDefinition(sql, channels, cancellationToken: cancellationToken));
    }

    private static async Task SeedAiProvidersAsync(
        IDbConnection connection,
        ISqlDialect dialect,
        CancellationToken cancellationToken)
    {
        var sql = dialect.Upsert(
            "AiProviders",
            [
                UpsertColumn.Parameter("Id"),
                UpsertColumn.Parameter("Kind"),
                UpsertColumn.Parameter("Name"),
                UpsertColumn.Parameter("ApiKey"),
                UpsertColumn.Parameter("Model"),
                UpsertColumn.Parameter("BaseUrl"),
                UpsertColumn.Parameter("IsEnabled"),
                UpsertColumn.Parameter("IsDefault"),
                UpsertColumn.Parameter("UpdatedAt")
            ],
            ["Kind"],
            null);

        var now = DateTime.UtcNow;
        var providers = new[] { AiProviderKind.Gemini, AiProviderKind.Claude, AiProviderKind.Codex }
            .Select(kind => new
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                Name = AiProviderKind.DisplayName(kind),
                ApiKey = (string?)null,
                Model = AiProviderKind.DefaultModel(kind),
                BaseUrl = (string?)null,
                IsEnabled = false,
                IsDefault = false,
                UpdatedAt = now
            });

        await connection.ExecuteAsync(new CommandDefinition(sql, providers, cancellationToken: cancellationToken));
    }

    private static async Task SeedJobsAsync(IDbConnection connection, CancellationToken cancellationToken)
    {
        var exists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM Jobs;", cancellationToken: cancellationToken));

        if (exists != 0) return;

        const string sql = """
                           INSERT INTO Jobs (Id, Name, Project, Description, Repository, Branch, RunnerName, WorkingDirectory,
                                             Command, TimeoutMinutes, ArtifactPath, TriggerKind, Cron, NotifyOnFailure, IsDisabled,
                                             CreatedAt, UpdatedAt)
                           VALUES (@Id, @Name, @Project, @Description, NULL, @Branch, @RunnerName, NULL,
                                   @Command, @TimeoutMinutes, NULL, @TriggerKind, @Cron, @NotifyOnFailure, @IsDisabled,
                                   @Now, @Now);
                           """;

        var now = DateTime.UtcNow;
        var jobs = new[]
        {
            new
            {
                Id = Guid.NewGuid(),
                Name = "版本驗證",
                Project = "Walis API",
                Description = (string?)"確認執行環境與 .NET SDK 可用。",
                Branch = "main",
                RunnerName = Runner.LocalName,
                Command = "echo 'Walis verification started' && dotnet --version",
                TimeoutMinutes = 10,
                TriggerKind = TriggerMode.Manual,
                Cron = (string?)null,
                NotifyOnFailure = true,
                IsDisabled = false,
                Now = now
            },
            new
            {
                Id = Guid.NewGuid(),
                Name = "環境檢查",
                Project = "Walis",
                Description = (string?)"每日檢查磁碟與工具鏈狀態。",
                Branch = "main",
                RunnerName = Runner.LocalName,
                Command = "uname -a && df -h .",
                TimeoutMinutes = 5,
                TriggerKind = TriggerMode.Cron,
                Cron = (string?)"30 2 * * *",
                NotifyOnFailure = true,
                IsDisabled = false,
                Now = now
            }
        };

        await connection.ExecuteAsync(new CommandDefinition(sql, jobs, cancellationToken: cancellationToken));
    }
}