using System.Data;
using Dapper;
using Walis.Core.Database;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Security;

namespace Walis.Core.Repositories;

public sealed class JobRepository(IDbConnectionFactory connectionFactory, ISecretProtector protector) : IJobRepository
{
    public const int MaximumActiveBuilds = 1_000;
    public const int MaximumActiveBuildsPerJob = 100;
    public const int MaximumBuildsPerTrigger = 50;

    /// <summary>How many times to retry taking a build number. The row lock already serializes one job; this is the last safety net.</summary>
    private const int QueueAttempts = 10;

    /// <summary>When another node wins the claim, move on to the next row so the round does not come back empty-handed.</summary>
    private const int ClaimAttempts = 5;

    private const string JobColumns = """
                                      j.Id, j.Name, j.Project, j.Description, j.Repository, j.Branch, j.RunnerName, j.WorkingDirectory,
                                      j.Command, j.TimeoutMinutes, j.ArtifactPath, j.TriggerKind, j.Cron, j.NotifyOnFailure, j.IsDisabled,
                                      j.ScriptMode, j.ActionsJson, j.CachePaths, j.CleanWorkspace, j.CleanWorkspaceAfterBuild,
                                      j.MatrixJson, j.ViewRole,
                                      CASE WHEN j.RunRole = 'viewer' THEN 'operator' ELSE j.RunRole END AS RunRole,
                                      j.ParametersJson, j.AllowConcurrentBuilds, j.DownstreamJson,
                                      j.DenyNetwork, j.TrustedUnsandboxed,
                                      j.CreatedAt, j.UpdatedAt
                                      """;

    private const string BuildColumns = """
                                        b.Id, b.JobId, b.Number, b.Status, b.TriggeredBy, b.RunnerName, b.QueuedAt, b.StartedAt,
                                        b.FinishedAt, b.ExitCode, b.Log, b.MatrixLabel, b.MatrixValues, b.ParametersJson, b.SecretParametersJson, b.Node,
                                        b.ExternalRef, b.TriggerHash, b.CancelRequestedAt,
                                        j.Name AS JobName, j.Project AS JobProject
                                        """;

    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    /// <summary>
    ///     Password-type parameter values are ciphertext in the database; every read path hands callers the plaintext (or null
    ///     when the key cannot read it). The property is settable precisely so this can happen without re-materializing the row.
    /// </summary>
    private Build Unprotect(Build build)
    {
        build.SecretParametersJson = protector.Unprotect(build.SecretParametersJson, SecretPurposes.BuildSecretParameters);
        return build;
    }

    private List<Build> Unprotect(IEnumerable<Build> builds)
    {
        return [.. builds.Select(Unprotect)];
    }

    private readonly string _jobStatistics = $"""
                                              (SELECT COUNT(1) FROM Builds b WHERE b.JobId = j.Id) AS TotalBuilds,
                                              (SELECT COUNT(1) FROM Builds b WHERE b.JobId = j.Id AND b.Status = 'Succeeded') AS SucceededBuilds,
                                              (SELECT b.Number FROM Builds b WHERE b.JobId = j.Id
                                                  ORDER BY b.Number DESC {connectionFactory.Dialect.Limit("1")}) AS LastBuildNumber,
                                              (SELECT b.Status FROM Builds b WHERE b.JobId = j.Id
                                                  ORDER BY b.Number DESC {connectionFactory.Dialect.Limit("1")}) AS LastBuildStatus,
                                              (SELECT COALESCE(b.FinishedAt, b.StartedAt, b.QueuedAt) FROM Builds b WHERE b.JobId = j.Id
                                                  ORDER BY b.Number DESC {connectionFactory.Dialect.Limit("1")}) AS LastBuildAt,
                                              (SELECT {connectionFactory.Dialect.SecondsBetween("b.FinishedAt", "b.StartedAt")} FROM Builds b
                                                  WHERE b.JobId = j.Id AND b.FinishedAt IS NOT NULL AND b.StartedAt IS NOT NULL
                                                  ORDER BY b.Number DESC {connectionFactory.Dialect.Limit("1")}) AS LastBuildSeconds
                                              """;

    /// <summary>Skips builds whose job disallows concurrency and already has one running when claiming; MarkBuildRunningAsync checks again.</summary>
    private string NotBlockedByConcurrencySql => $"""
                                                  NOT EXISTS (
                                                      SELECT 1 FROM Jobs cj
                                                      WHERE cj.Id = Builds.JobId AND cj.AllowConcurrentBuilds = {_dialect.Boolean(false)}
                                                        AND EXISTS (SELECT 1 FROM Builds cr WHERE cr.JobId = Builds.JobId AND cr.Status = 'Running'))
                                                  """;

    public async Task<IReadOnlyList<Job>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   SELECT {JobColumns}, {_jobStatistics}
                   FROM Jobs j
                   ORDER BY j.IsDisabled, j.Name;
                   """;

        using var connection = connectionFactory.Create();
        var jobs = await connection.QueryAsync<Job>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return jobs.Select(WithSuccessRate).ToList();
    }

    public async Task<JobPage> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, JobQuery.MaximumPageSize);

        // Permissions: the ViewRoles the caller can see. An empty set sees nothing, so return an empty page without asking the database.
        if (query.ViewRoles is { Count: 0 }) return new JobPage([], 0, page, pageSize, 0, 0, []);

        var scope = new List<string>();
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (query.ViewRoles is not null)
        {
            scope.Add("j.ViewRole IN @ViewRoles");
            parameters.Add("ViewRoles", query.ViewRoles.ToArray());
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // LOWER exists in all four; SQLite's LIKE is case-insensitive already, the others rely on LOWER.
            conditions.Add(
                "(LOWER(j.Name) LIKE @Search OR LOWER(COALESCE(j.Project, '')) LIKE @Search OR LOWER(j.Branch) LIKE @Search)");
            parameters.Add("Search", $"%{query.Search.Trim().ToLowerInvariant()}%");
        }

        if (query.Status is JobStatusFilter.Enabled or JobStatusFilter.Paused)
        {
            conditions.Add("j.IsDisabled = @IsDisabled");
            parameters.Add("IsDisabled", query.Status == JobStatusFilter.Paused);
        }

        if (!string.IsNullOrWhiteSpace(query.TriggerKind))
        {
            conditions.Add("j.TriggerKind = @TriggerKind");
            parameters.Add("TriggerKind", query.TriggerKind.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.Project))
        {
            conditions.Add("j.Project = @Project");
            parameters.Add("Project", query.Project.Trim());
        }

        var scopeWhere = scope.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", scope)}";
        var all = scope.Concat(conditions).ToList();
        var where = all.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", all)}";

        // Sorting is on the outer subquery so statistics columns can be sort keys (Postgres and SQL Server reject expressions on aliases in ORDER BY).
        // Every sort ends with Name, Id so paging stays stable.
        var direction = query.Descending ? "DESC" : "ASC";
        var orderBy = JobSort.Normalize(query.Sort) switch
        {
            JobSort.Project => $"Project {direction}, Name ASC, Id ASC",
            JobSort.LastBuild => $"LastBuildAt {direction}, Name ASC, Id ASC",
            JobSort.SuccessRate =>
                $"(CASE WHEN TotalBuilds = 0 THEN 1.0 ELSE SucceededBuilds * 1.0 / TotalBuilds END) {direction}, TotalBuilds {direction}, Name ASC, Id ASC",
            JobSort.Updated => $"UpdatedAt {direction}, Name ASC, Id ASC",
            _ => $"Name {direction}, Id ASC"
        };

        parameters.Add("Take", pageSize);
        parameters.Add("Skip", (page - 1) * pageSize);

        var itemsSql = $"""
                        SELECT * FROM (
                            SELECT {JobColumns}, {_jobStatistics}
                            FROM Jobs j
                            {where}
                        ) q
                        ORDER BY {orderBy}
                        {_dialect.Page("@Take", "@Skip")};
                        """;
        var countSql = $"SELECT COUNT(1) FROM Jobs j {where};";
        var summarySql = $"""
                          SELECT COUNT(1) AS TotalAll,
                                 COALESCE(SUM(CASE WHEN j.IsDisabled = {_dialect.Boolean(false)} THEN 1 ELSE 0 END), 0) AS EnabledAll
                          FROM Jobs j {scopeWhere};
                          """;
        var projectsSql = $"""
                           SELECT DISTINCT j.Project FROM Jobs j
                           {(scope.Count == 0 ? "WHERE" : scopeWhere + " AND")} j.Project IS NOT NULL AND j.Project <> ''
                           ORDER BY j.Project;
                           """;

        using var connection = connectionFactory.Create();
        var items = (await connection.QueryAsync<Job>(new CommandDefinition(itemsSql, parameters,
                cancellationToken: cancellationToken)))
            .Select(WithSuccessRate)
            .ToList();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countSql, parameters,
            cancellationToken: cancellationToken));
        var summary = await connection.QuerySingleAsync<(int TotalAll, int EnabledAll)>(
            new CommandDefinition(summarySql, parameters, cancellationToken: cancellationToken));
        var projects =
            (await connection.QueryAsync<string>(new CommandDefinition(projectsSql, parameters,
                cancellationToken: cancellationToken))).ToList();

        return new JobPage(items, total, page, pageSize, summary.TotalAll, summary.EnabledAll, projects);
    }

    public async Task<Job?> GetJobByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   SELECT {JobColumns}, {_jobStatistics}
                   FROM Jobs j
                   WHERE j.Name = @Name
                   ORDER BY j.CreatedAt
                   {_dialect.Limit("1")};
                   """;

        using var connection = connectionFactory.Create();
        var job = await connection.QueryFirstOrDefaultAsync<Job>(
            new CommandDefinition(sql, new { Name = name.Trim() }, cancellationToken: cancellationToken));
        return job is null ? null : WithSuccessRate(job);
    }

    public async Task<Job?> GetJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   SELECT {JobColumns}, {_jobStatistics}
                   FROM Jobs j
                   WHERE j.Id = @Id;
                   """;

        using var connection = connectionFactory.Create();
        var job = await connection.QuerySingleOrDefaultAsync<Job>(
            new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));
        return job is null ? null : WithSuccessRate(job);
    }

    public async Task<Job> CreateJobAsync(JobDraft draft, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var parameters = ToParameters(draft);
        parameters.Add("Id", Guid.NewGuid());
        parameters.Add("CreatedAt", now);
        parameters.Add("UpdatedAt", now);

        const string sql = """
                           INSERT INTO Jobs (Id, Name, Project, Description, Repository, Branch, RunnerName, WorkingDirectory,
                                             Command, TimeoutMinutes, ArtifactPath, TriggerKind, Cron, NotifyOnFailure, IsDisabled,
                                             ScriptMode, ActionsJson, CachePaths, CleanWorkspace, CleanWorkspaceAfterBuild,
                                             MatrixJson, ViewRole, RunRole,
                                             ParametersJson, AllowConcurrentBuilds, DownstreamJson,
                                             DenyNetwork, TrustedUnsandboxed,
                                             CreatedAt, UpdatedAt)
                           VALUES (@Id, @Name, @Project, @Description, @Repository, @Branch, @RunnerName, @WorkingDirectory,
                                   @Command, @TimeoutMinutes, @ArtifactPath, @TriggerKind, @Cron, @NotifyOnFailure, @IsDisabled,
                                   @ScriptMode, @ActionsJson, @CachePaths, @CleanWorkspace, @CleanWorkspaceAfterBuild,
                                   @MatrixJson, @ViewRole, @RunRole,
                                   @ParametersJson, @AllowConcurrentBuilds, @DownstreamJson,
                                   @DenyNetwork, @TrustedUnsandboxed,
                                   @CreatedAt, @UpdatedAt);
                           """;

        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return (await GetJobAsync(parameters.Get<Guid>("Id"), cancellationToken))!;
    }

    public async Task<Job?> UpdateJobAsync(Guid id, JobDraft draft, CancellationToken cancellationToken = default)
    {
        var parameters = ToParameters(draft);
        parameters.Add("Id", id);
        parameters.Add("UpdatedAt", DateTime.UtcNow);

        const string sql = """
                           UPDATE Jobs
                           SET Name = @Name,
                               Project = @Project,
                               Description = @Description,
                               Repository = @Repository,
                               Branch = @Branch,
                               RunnerName = @RunnerName,
                               WorkingDirectory = @WorkingDirectory,
                               Command = @Command,
                               TimeoutMinutes = @TimeoutMinutes,
                               ArtifactPath = @ArtifactPath,
                               TriggerKind = @TriggerKind,
                               Cron = @Cron,
                               NotifyOnFailure = @NotifyOnFailure,
                               IsDisabled = @IsDisabled,
                               ScriptMode = @ScriptMode,
                               ActionsJson = @ActionsJson,
                               CachePaths = @CachePaths,
                               CleanWorkspace = @CleanWorkspace,
                               CleanWorkspaceAfterBuild = @CleanWorkspaceAfterBuild,
                               MatrixJson = @MatrixJson,
                               ViewRole = @ViewRole,
                               RunRole = @RunRole,
                               ParametersJson = @ParametersJson,
                               AllowConcurrentBuilds = @AllowConcurrentBuilds,
                               DownstreamJson = @DownstreamJson,
                               DenyNetwork = @DenyNetwork,
                               TrustedUnsandboxed = @TrustedUnsandboxed,
                               UpdatedAt = @UpdatedAt
                           WHERE Id = @Id;
                           """;

        using var connection = connectionFactory.Create();
        var affected =
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return affected == 0 ? null : await GetJobAsync(id, cancellationToken);
    }

    public async Task<Job?> SetJobEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Jobs SET IsDisabled = @IsDisabled, UpdatedAt = @UpdatedAt WHERE Id = @Id;",
            new { Id = id, IsDisabled = !enabled, UpdatedAt = DateTime.UtcNow },
            cancellationToken: cancellationToken));

        return affected == 0 ? null : await GetJobAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Jobs WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken));
        return affected != 0;
    }

    public Task<Build> QueueBuildAsync(
        Guid jobId,
        string triggeredBy,
        string? runnerName,
        CancellationToken cancellationToken = default)
    {
        return QueueBuildAsync(jobId, triggeredBy, runnerName, null, null, cancellationToken);
    }

    public async Task<Build> QueueBuildAsync(
        Guid jobId,
        string triggeredBy,
        string? runnerName,
        string? matrixLabel,
        string? matrixValues,
        CancellationToken cancellationToken = default)
    {
        var builds = await TryQueueBuildsAsync(
            jobId,
            triggeredBy,
            runnerName,
            [new BuildQueueItem(matrixLabel, matrixValues)],
            cancellationToken);

        return builds is { Count: 1 }
            ? builds[0]
            : throw new InvalidOperationException("待處理建置已達容量上限。");
    }

    public async Task<IReadOnlyList<Build>?> TryQueueBuildsAsync(
        Guid jobId,
        string triggeredBy,
        string? runnerName,
        IReadOnlyList<BuildQueueItem> items,
        CancellationToken cancellationToken = default)
    {
        var result = await QueueBuildsAsync(jobId, triggeredBy, runnerName, items, null, cancellationToken);

        return result.Outcome switch
        {
            BuildQueueOutcome.CapacityExceeded => null,
            BuildQueueOutcome.JobNotFound => [],
            _ => result.Builds
        };
    }

    public async Task<BuildQueueResult> QueueBuildsAsync(
        Guid jobId,
        string triggeredBy,
        string? runnerName,
        IReadOnlyList<BuildQueueItem> items,
        BuildExternalReference? externalReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count is < 1 or > MaximumBuildsPerTrigger)
            throw new ArgumentOutOfRangeException(
                nameof(items),
                $"一次必須排入 1 到 {MaximumBuildsPerTrigger} 筆建置。");

        using var connection = connectionFactory.Create();

        if (connection.State != ConnectionState.Open) connection.Open();

        // The build number is "the nth run of this job". When several nodes trigger at once, numbering and inserting interleave,
        // and relying only on UNIQUE(JobId, Number) plus retries eventually fails whole requests.
        // Lock the job row first so numbering for one job is serialized; the retry is only the last safety net.
        for (var attempt = 1;; attempt++)
            try
            {
                using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                await LockQueueCapacityAsync(connection, transaction, cancellationToken);

                // The capacity lock serializes triggers of different jobs too; the job lock protects both numbering and the delete race.
                var lockedJob = await connection.QuerySingleOrDefaultAsync<QueueJob>(new CommandDefinition(
                    $"SELECT Id, Name, Project FROM Jobs{_dialect.ForUpdateHint} WHERE Id = @JobId{_dialect.ForUpdateSuffix};",
                    new { JobId = jobId },
                    transaction,
                    cancellationToken: cancellationToken));
                if (lockedJob is null)
                {
                    transaction.Rollback();
                    return BuildQueueResult.Of(BuildQueueOutcome.JobNotFound);
                }

                // Checked while the job row is locked: two concurrent requests with the same reference are serialized here,
                // so the second one sees the first one's rows instead of inserting its own. Done before the capacity check
                // because a retry must keep returning the original builds even when the queue has since filled up.
                if (externalReference is not null)
                {
                    var existing = await FindBuildsByExternalRefAsync(connection, transaction, jobId,
                        externalReference.Value, cancellationToken);
                    if (existing.Count != 0)
                    {
                        transaction.Rollback();
                        return new BuildQueueResult(
                            existing.All(build => build.TriggerHash == externalReference.TriggerHash)
                                ? BuildQueueOutcome.Deduplicated
                                : BuildQueueOutcome.ExternalRefConflict,
                            existing);
                    }
                }

                var activeGlobal = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COUNT(1) FROM Builds WHERE Status IN ('Queued', 'Running');",
                    transaction: transaction,
                    cancellationToken: cancellationToken));
                var activeForJob = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COUNT(1) FROM Builds WHERE JobId = @JobId AND Status IN ('Queued', 'Running');",
                    new { JobId = jobId },
                    transaction,
                    cancellationToken: cancellationToken));

                if (activeGlobal + items.Count > MaximumActiveBuilds ||
                    activeForJob + items.Count > MaximumActiveBuildsPerJob)
                {
                    transaction.Rollback();
                    return BuildQueueResult.Of(BuildQueueOutcome.CapacityExceeded);
                }

                var queued = new List<Build>(items.Count);
                foreach (var item in items)
                    queued.Add(await InsertBuildAsync(
                        connection,
                        transaction,
                        jobId,
                        lockedJob.Name,
                        lockedJob.Project,
                        triggeredBy,
                        runnerName,
                        item,
                        externalReference,
                        cancellationToken));

                transaction.Commit();
                return new BuildQueueResult(BuildQueueOutcome.Queued, queued);
            }
            catch (Exception exception) when (attempt < QueueAttempts && IsRetryable(exception))
            {
                // Yield a little so both sides do not retry in lockstep and collide again.
                await Task.Delay(Random.Shared.Next(10, 60), cancellationToken);
            }
    }

    public async Task<IReadOnlyList<Build>> FindBuildsByExternalRefAsync(
        Guid jobId,
        string externalRef,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        return await FindBuildsByExternalRefAsync(connection, null, jobId, externalRef, cancellationToken);
    }

    public async Task<BuildContext?> GetBuildContextAsync(Guid buildId, CancellationToken cancellationToken = default)
    {
        var build = await GetBuildAsync(buildId, cancellationToken);
        if (build is null) return null;

        var job = await GetJobAsync(build.JobId, cancellationToken);
        return job is null ? null : new BuildContext(job, build);
    }

    public async Task UpdateBuildLogAsync(Guid buildId, string log, CancellationToken cancellationToken = default)
    {
        // Only update builds in progress: a progress report arriving after completion must not overwrite the final result.
        const string sql = """
                           UPDATE Builds SET Log = @Log WHERE Id = @Id AND Status = @Running;
                           """;

        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { Id = buildId, Log = log, BuildStatus.Running },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> MarkBuildRunningAsync(
        Guid buildId,
        string runnerName,
        string? node = null,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   UPDATE Builds
                   SET Status = @Status, StartedAt = @StartedAt, RunnerName = @RunnerName, Node = @Node,
                       Log = {_dialect.Concat("Log", "@Message")}
                   WHERE Id = @Id AND Status = @Queued;
                   """;

        using var connection = connectionFactory.Create();
        if (connection.State != ConnectionState.Open) connection.Open();

        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var now = await connection.ReadUtcNowAsync(_dialect, transaction, cancellationToken);

        // Restore first acquires this same row as a lease. Locking it in the same transaction as
        // Queued -> Running closes the check/start race across API nodes and remote runners.
        var ensureLease = _dialect.Upsert(
            "Leases",
            [
                UpsertColumn.Parameter("Name"),
                UpsertColumn.Parameter("Owner"),
                UpsertColumn.Parameter("AcquiredAt"),
                UpsertColumn.Parameter("ExpiresAt")
            ],
            ["Name"],
            null);
        await connection.ExecuteAsync(new CommandDefinition(
            ensureLease,
            new { Name = LeaseNames.Maintenance, Owner = "none", AcquiredAt = now, ExpiresAt = now },
            transaction,
            cancellationToken: cancellationToken));

        var maintenanceUntil = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            $"SELECT ExpiresAt FROM Leases{_dialect.ForUpdateHint} WHERE Name = @Name{_dialect.ForUpdateSuffix};",
            new { Name = LeaseNames.Maintenance },
            transaction,
            cancellationToken: cancellationToken));
        if (maintenanceUntil > now)
        {
            transaction.Rollback();
            return false;
        }

        // Jobs with concurrency disabled: do not claim while another build of the same job is running; it stays queued until that one finishes.
        var blocked = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"""
             SELECT COUNT(1) FROM Builds me
             INNER JOIN Jobs j ON j.Id = me.JobId
             INNER JOIN Builds r ON r.JobId = me.JobId AND r.Status = @Running AND r.Id <> me.Id
             WHERE me.Id = @Id AND j.AllowConcurrentBuilds = {_dialect.Boolean(false)};
             """,
            new { Id = buildId, BuildStatus.Running },
            transaction,
            cancellationToken: cancellationToken));
        if (blocked != 0)
        {
            transaction.Rollback();
            return false;
        }

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = buildId,
                Status = BuildStatus.Running,
                BuildStatus.Queued,
                StartedAt = now,
                RunnerName = runnerName,
                Node = node,
                Message = $"由執行器 {runnerName} 開始執行…\n"
            },
            transaction,
            cancellationToken: cancellationToken));

        transaction.Commit();
        return affected != 0;
    }

    public async Task<int> CompleteBuildAsync(Guid buildId, BuildCompletion completion,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
                           UPDATE Builds
                           SET Status = @Status,
                               ExitCode = @ExitCode,
                               FinishedAt = @FinishedAt,
                               Log = @Log
                           WHERE Id = @Id AND Status = @Running;
                           """;

        using var connection = connectionFactory.Create();
        var finishedAt = await connection.ReadUtcNowAsync(_dialect, cancellationToken: cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = buildId,
                completion.Status,
                completion.ExitCode,
                BuildStatus.Running,
                FinishedAt = finishedAt,
                completion.Log
            },
            cancellationToken: cancellationToken));
    }

    public async Task<BuildCancellation> CancelBuildAsync(Guid buildId, CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   UPDATE Builds
                   SET Status = @Canceled, FinishedAt = @FinishedAt, Log = {_dialect.Concat("Log", "@Message")}
                   WHERE Id = @Id AND Status = @Queued;
                   """;

        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = buildId,
                BuildStatus.Canceled,
                BuildStatus.Queued,
                FinishedAt = DateTime.UtcNow,
                Message = "建置已取消。\n"
            },
            cancellationToken: cancellationToken));

        if (affected != 0) return BuildCancellation.Canceled;

        // A running build cannot be flipped from here: its shell is still going. Leave a request for whoever runs it
        // (the local worker polls, a remote runner is told on its next report) and let that side record the final state.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Builds SET CancelRequestedAt = @Now
            WHERE Id = @Id AND Status = @Running AND CancelRequestedAt IS NULL;
            """,
            new { Id = buildId, Now = DateTime.UtcNow, BuildStatus.Running },
            cancellationToken: cancellationToken));

        var running = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM Builds WHERE Id = @Id AND Status = @Running AND CancelRequestedAt IS NOT NULL;",
            new { Id = buildId, BuildStatus.Running },
            cancellationToken: cancellationToken));

        return running != 0 ? BuildCancellation.Requested : BuildCancellation.NotCancelable;
    }

    public async Task<bool> IsCancelRequestedAsync(Guid buildId, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM Builds WHERE Id = @Id AND CancelRequestedAt IS NOT NULL;",
            new { Id = buildId },
            cancellationToken: cancellationToken));
        return count != 0;
    }

    public async Task<IReadOnlyList<Build>> ListBuildsAsync(BuildQuery query,
        CancellationToken cancellationToken = default)
    {
        var filter = ComposeBuildFilter(query);
        filter.Parameters.Add("Limit", Math.Clamp(query.Limit, 1, 200));

        var sql = $"""
                   SELECT {BuildColumns}
                   FROM Builds b
                   INNER JOIN Jobs j ON j.Id = b.JobId
                   {filter.Where}
                   ORDER BY b.QueuedAt DESC, b.Id ASC
                   {_dialect.Limit("@Limit")};
                   """;

        using var connection = connectionFactory.Create();
        var builds =
            await connection.QueryAsync<Build>(new CommandDefinition(sql, filter.Parameters,
                cancellationToken: cancellationToken));
        return Unprotect(builds);
    }

    public async Task<BuildPage> QueryBuildsAsync(BuildQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, BuildQuery.MaximumPageSize);

        // Permissions: an empty set of visible ViewRoles sees nothing, so answer without asking the database.
        if (query.ViewRoles is { Count: 0 }) return new BuildPage([], 0, page, pageSize, EmptyStatusCounts());

        var filter = ComposeBuildFilter(query);
        filter.Parameters.Add("Take", pageSize);
        filter.Parameters.Add("Skip", (page - 1) * pageSize);

        // QueuedAt is not unique, so Id joins the ordering; otherwise two builds queued in the same instant could repeat or vanish between pages.
        var itemsSql = $"""
                        SELECT {BuildColumns}
                        FROM Builds b
                        INNER JOIN Jobs j ON j.Id = b.JobId
                        {filter.Where}
                        ORDER BY b.QueuedAt DESC, b.Id ASC
                        {_dialect.Page("@Take", "@Skip")};
                        """;
        var countSql = $"""
                        SELECT {_dialect.CountAll}
                        FROM Builds b
                        INNER JOIN Jobs j ON j.Id = b.JobId
                        {filter.Where};
                        """;
        var tallySql = $"""
                        SELECT b.Status AS Status, {_dialect.CountAll} AS Count
                        FROM Builds b
                        INNER JOIN Jobs j ON j.Id = b.JobId
                        {filter.TallyWhere}
                        GROUP BY b.Status;
                        """;

        using var connection = connectionFactory.Create();
        var items = Unprotect(await connection.QueryAsync<Build>(new CommandDefinition(itemsSql, filter.Parameters,
            cancellationToken: cancellationToken)));
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, filter.Parameters,
            cancellationToken: cancellationToken));
        var tally = await connection.QueryAsync<StatusTally>(new CommandDefinition(tallySql, filter.Parameters,
            cancellationToken: cancellationToken));

        var counts = EmptyStatusCounts();
        foreach (var row in tally) counts[row.Status] = (int)row.Count;

        return new BuildPage(items, (int)total, page, pageSize, counts);
    }

    /// <summary>
    ///     The conditions shared by the build queries. The status condition is left out of <see cref="BuildFilterSql.TallyWhere" />,
    ///     so listing a single status does not narrow the per-status tally shown above the table.
    /// </summary>
    private BuildFilterSql ComposeBuildFilter(BuildQuery query)
    {
        var scope = new List<string>();
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (query.ViewRoles is not null)
        {
            scope.Add("j.ViewRole IN @ViewRoles");
            parameters.Add("ViewRoles", query.ViewRoles.ToArray());
        }

        if (!string.IsNullOrWhiteSpace(query.RunnerName))
        {
            conditions.Add("b.RunnerName = @RunnerName");
            parameters.Add("RunnerName", query.RunnerName);
        }

        if (query.JobId is { } jobId)
        {
            conditions.Add("b.JobId = @JobId");
            parameters.Add("JobId", jobId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            conditions.Add($"(j.Name LIKE @Search OR {_dialect.CastToText("b.Number")} LIKE @Search)");
            parameters.Add("Search", $"%{query.Search.Trim()}%");
        }

        var tally = scope.Concat(conditions).ToList();
        var all = tally.ToList();

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            all.Add("b.Status = @Status");
            parameters.Add("Status", query.Status);
        }

        return new BuildFilterSql(Where(all), Where(tally), parameters);

        static string Where(IReadOnlyList<string> parts)
        {
            return parts.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", parts)}";
        }
    }

    private static Dictionary<string, int> EmptyStatusCounts()
    {
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [BuildStatus.Queued] = 0,
            [BuildStatus.Running] = 0,
            [BuildStatus.Succeeded] = 0,
            [BuildStatus.Failed] = 0,
            [BuildStatus.Canceled] = 0
        };
    }

    /// <summary>The WHERE clauses and parameters shared by the build queries.</summary>
    private sealed record BuildFilterSql(string Where, string TallyWhere, DynamicParameters Parameters);

    /// <summary>
    ///     One row of the per-status tally. Dapper needs a type it can construct without arguments: with zero rows
    ///     Microsoft.Data.Sqlite cannot tell the column types apart, reports them all as BLOB, and then matches no positional record constructor.
    /// </summary>
    private sealed class StatusTally
    {
        public string Status { get; set; } = string.Empty;

        public long Count { get; set; }
    }

    public async Task<Build?> GetBuildAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   SELECT {BuildColumns}
                   FROM Builds b
                   INNER JOIN Jobs j ON j.Id = b.JobId
                   WHERE b.Id = @Id;
                   """;

        using var connection = connectionFactory.Create();
        var build = await connection.QuerySingleOrDefaultAsync<Build>(
            new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));

        if (build is null) return null;

        build.Steps = await ListBuildStepsAsync(id, cancellationToken);

        return Unprotect(build);
    }

    public async Task CreateBuildStepsAsync(
        Guid buildId,
        IReadOnlyList<BuildStepPlan> plans,
        CancellationToken cancellationToken = default)
    {
        if (plans.Count == 0) return;

        const string sql = """
                           INSERT INTO BuildSteps (Id, BuildId, Number, Name, Command, ContinueOnError, Parallel, RunWhen, Retries, StepKey, DependsOnJson, Condition, Status, Log)
                           VALUES (@Id, @BuildId, @Number, @Name, @Command, @ContinueOnError, @Parallel, @RunWhen, @Retries, @StepKey, @DependsOnJson, @Condition, @Status, '');
                           """;

        using var connection = connectionFactory.Create();

        var rows = plans.Select((plan, index) => new
        {
            Id = Guid.NewGuid(),
            BuildId = buildId,
            Number = index + 1,
            plan.Name,
            plan.Command,
            plan.ContinueOnError,
            plan.Parallel,
            RunWhen = StepRunCondition.Normalize(plan.RunWhen),
            Retries = Math.Clamp(plan.Retries, 0, BuildStepPlan.MaximumRetries),
            StepKey = plan.Key,
            DependsOnJson = plan.DependsOn is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(plan.DependsOn) : null,
            Condition = string.IsNullOrWhiteSpace(plan.Condition) ? null : plan.Condition.Trim(),
            Status = BuildStepStatus.Pending
        });

        await connection.ExecuteAsync(new CommandDefinition(sql, rows, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<BuildStep>> ListBuildStepsAsync(
        Guid buildId,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var steps = await connection.QueryAsync<BuildStep>(new CommandDefinition(
            """
            SELECT Id, BuildId, Number, Name, Command, ContinueOnError, Parallel, RunWhen, Retries, StepKey, DependsOnJson, Condition, OutputsJson, Status,
                   StartedAt, FinishedAt, ExitCode, Log
            FROM BuildSteps WHERE BuildId = @BuildId ORDER BY Number;
            """,
            new { BuildId = buildId },
            cancellationToken: cancellationToken));

        return [.. steps];
    }

    public async Task MarkBuildStepRunningAsync(Guid stepId, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE BuildSteps
            SET Status = @Running, StartedAt = @StartedAt
            WHERE Id = @Id AND Status = @Pending
              AND EXISTS (
                  SELECT 1 FROM Builds b
                  WHERE b.Id = BuildSteps.BuildId AND b.Status = @BuildRunning
              );
            """,
            new
            {
                Id = stepId,
                BuildStepStatus.Running,
                BuildStepStatus.Pending,
                BuildRunning = BuildStatus.Running,
                StartedAt = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));
    }

    public async Task UpdateBuildStepLogAsync(
        Guid stepId,
        string log,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE BuildSteps SET Log = @Log
            WHERE Id = @Id AND Status = @Running
              AND EXISTS (
                  SELECT 1 FROM Builds b
                  WHERE b.Id = BuildSteps.BuildId AND b.Status = @BuildRunning
              );
            """,
            new
            {
                Id = stepId,
                Log = log,
                BuildStepStatus.Running,
                BuildRunning = BuildStatus.Running
            },
            cancellationToken: cancellationToken));
    }

    public async Task CompleteBuildStepAsync(
        Guid stepId,
        string status,
        int? exitCode,
        string log,
        string? outputsJson = null,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE BuildSteps
            SET Status = @Status, ExitCode = @ExitCode, Log = @Log, OutputsJson = @OutputsJson, FinishedAt = @FinishedAt
            WHERE Id = @Id AND Status IN (@Pending, @Running)
              AND EXISTS (
                  SELECT 1 FROM Builds b
                  WHERE b.Id = BuildSteps.BuildId AND b.Status = @BuildRunning
              );
            """,
            new
            {
                Id = stepId,
                Status = status,
                ExitCode = exitCode,
                Log = log,
                OutputsJson = outputsJson,
                BuildStepStatus.Pending,
                BuildStepStatus.Running,
                BuildRunning = BuildStatus.Running,
                FinishedAt = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));
    }

    public async Task SkipPendingBuildStepsAsync(Guid buildId, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE BuildSteps SET Status = @Skipped, FinishedAt = @FinishedAt
            WHERE BuildId = @BuildId AND Status = @Pending
              AND EXISTS (
                  SELECT 1 FROM Builds b
                  WHERE b.Id = BuildSteps.BuildId AND b.Status = @BuildRunning
              );
            """,
            new
            {
                BuildId = buildId,
                BuildStepStatus.Skipped,
                BuildStepStatus.Pending,
                BuildRunning = BuildStatus.Running,
                FinishedAt = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    ///     Queued builds. With a runner name, returns only what this runner can run (no runner assigned, or assigned to it);
    ///     builds assigned to a remote runner are left for that runner to claim, and picking them up would be a wasted trip.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ListQueuedBuildIdsAsync(
        string? runnerName = null,
        CancellationToken cancellationToken = default)
    {
        var filter = runnerName is null
            ? string.Empty
            : " AND (RunnerName IS NULL OR RunnerName = @RunnerName)";

        using var connection = connectionFactory.Create();
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            $"SELECT Id FROM Builds WHERE Status = @Status{filter} ORDER BY QueuedAt;",
            new { Status = BuildStatus.Queued, RunnerName = runnerName },
            cancellationToken: cancellationToken));
        return ids.AsList();
    }

    public async Task<int> CountRunningBuildsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM Builds WHERE Status = @Status;",
            new { Status = BuildStatus.Running },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Build>> ListRunningBuildsAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   SELECT {BuildColumns}
                   FROM Builds b
                   INNER JOIN Jobs j ON j.Id = b.JobId
                   WHERE b.Status = @Status
                   ORDER BY b.StartedAt;
                   """;

        using var connection = connectionFactory.Create();
        var builds = await connection.QueryAsync<Build>(new CommandDefinition(
            sql, new { Status = BuildStatus.Running }, cancellationToken: cancellationToken));
        return Unprotect(builds);
    }

    public async Task<string?> GetPreviousBuildStatusAsync(Guid jobId, int beforeNumber,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"""
             SELECT Status FROM Builds
             WHERE JobId = @JobId AND Number < @Number AND Status IN ('Succeeded', 'Failed')
             ORDER BY Number DESC {_dialect.Limit("1")};
             """,
            new { JobId = jobId, Number = beforeNumber },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> HasBuildQueuedSinceAsync(Guid jobId, DateTime since,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM Builds WHERE JobId = @JobId AND QueuedAt >= @Since;",
            new { JobId = jobId, Since = since },
            cancellationToken: cancellationToken));
        return count != 0;
    }

    /// <summary>
    ///     Lets a runner claim work: takes one queued build assigned to it and marks it running.
    ///     Several API nodes sharing one database compete at the same time, so the selection locks the row and skips rows others are handling,
    ///     but the real gate is still the conditional UPDATE in <see cref="MarkBuildRunningAsync" />:
    ///     only the side that flips Queued to Running gets the work; the loser moves on to the next row.
    /// </summary>
    public async Task<Build?> ClaimNextBuildAsync(
        string runnerName,
        string? node = null,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   SELECT Id FROM Builds{_dialect.SkipLockedHint}
                   WHERE Status = @Queued AND RunnerName = @RunnerName
                     AND {NotBlockedByConcurrencySql}
                   ORDER BY QueuedAt
                   {_dialect.Limit("1")}{_dialect.SkipLockedSuffix}
                   """;

        for (var attempt = 0; attempt < ClaimAttempts; attempt++)
        {
            using var connection = connectionFactory.Create();
            var id = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                sql,
                new { BuildStatus.Queued, RunnerName = runnerName },
                cancellationToken: cancellationToken));

            if (id is null) return null;

            if (await MarkBuildRunningAsync(id.Value, runnerName, node, cancellationToken))
                return await GetBuildAsync(id.Value, cancellationToken);
        }

        return null;
    }

    /// <summary>
    ///     Lets an API node take its own work: queued builds with no runner assigned or assigned to the built-in runner.
    ///     With several nodes each one is picking, and the same conditional UPDATE decides who gets it.
    /// </summary>
    public async Task<Build?> ClaimNextLocalBuildAsync(
        string runnerName,
        string node,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   SELECT Id FROM Builds{_dialect.SkipLockedHint}
                   WHERE Status = @Queued AND (RunnerName IS NULL OR RunnerName = @RunnerName)
                     AND {NotBlockedByConcurrencySql}
                   ORDER BY QueuedAt
                   {_dialect.Limit("1")}{_dialect.SkipLockedSuffix}
                   """;

        for (var attempt = 0; attempt < ClaimAttempts; attempt++)
        {
            using var connection = connectionFactory.Create();
            var id = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                sql,
                new { BuildStatus.Queued, RunnerName = runnerName },
                cancellationToken: cancellationToken));

            if (id is null) return null;

            if (await MarkBuildRunningAsync(id.Value, runnerName, node, cancellationToken))
                return await GetBuildAsync(id.Value, cancellationToken);
        }

        return null;
    }

    /// <summary>
    ///     The fallback for the average must be written as <c>0.0</c>: <c>AVG</c> returns NULL when there are no builds,
    ///     and an integer 0 fallback makes SQLite type the column INTEGER, so Dapper cannot match
    ///     the double parameter of <see cref="BuildCounts" /> and the whole dashboard returns 500.
    /// </summary>
    public async Task<IReadOnlyList<Build>> ListBuildsBetweenAsync(DateTime from, DateTime to, int limit = 5_000, CancellationToken cancellationToken = default)
    {
        // Logs may be large and the timeline only needs times and status; the Log column gets an empty string.
        var sql = $"""
            SELECT b.Id, b.JobId, b.Number, b.Status, b.TriggeredBy, b.RunnerName, b.QueuedAt, b.StartedAt,
                   b.FinishedAt, b.ExitCode, '' AS Log, b.MatrixLabel, b.Node,
                   j.Name AS JobName, j.Project AS JobProject
            FROM Builds b
            INNER JOIN Jobs j ON j.Id = b.JobId
            WHERE b.QueuedAt >= @From AND b.QueuedAt < @To
            ORDER BY b.QueuedAt
            {_dialect.Limit("@Limit")};
            """;

        using var connection = connectionFactory.Create();
        var builds = await connection.QueryAsync<Build>(new CommandDefinition(
            sql,
            new { From = from, To = to, Limit = Math.Clamp(limit, 1, 20_000) },
            cancellationToken: cancellationToken));
        return builds.AsList();
    }

    public async Task<IReadOnlyDictionary<Guid, double>> GetAverageDurationsAsync(DateTime since, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT JobId, AVG({_dialect.SecondsBetween("FinishedAt", "StartedAt")}) AS Seconds
            FROM Builds
            WHERE Status = @Succeeded AND FinishedAt IS NOT NULL AND StartedAt IS NOT NULL AND FinishedAt >= @Since
            GROUP BY JobId;
            """;

        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<(Guid JobId, double Seconds)>(new CommandDefinition(
            sql,
            new { Succeeded = BuildStatus.Succeeded, Since = since },
            cancellationToken: cancellationToken));
        return rows.ToDictionary(row => row.JobId, row => row.Seconds);
    }

    public async Task<BuildCounts> GetBuildCountsAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   SELECT
                       (SELECT {_dialect.CountAll} FROM Builds WHERE Status = 'Queued') AS QueuedBuilds,
                       (SELECT {_dialect.CountAll} FROM Builds WHERE Status = 'Running') AS RunningBuilds,
                       (SELECT {_dialect.CountAll} FROM Builds WHERE Status = 'Succeeded') AS SuccessfulBuilds,
                       (SELECT {_dialect.CountAll} FROM Builds WHERE Status = 'Failed') AS FailedBuilds,
                       (SELECT {_dialect.CountAll} FROM Builds WHERE QueuedAt >= @Since) AS BuildsLast7Days,
                       (SELECT {_dialect.CountAll} FROM Builds WHERE QueuedAt >= @Since AND Status = 'Failed')
                           AS FailuresLast7Days,
                       (SELECT {_dialect.CountAll} FROM Builds WHERE QueuedAt >= @Since AND Status = 'Succeeded')
                           AS SucceededLast7Days,
                       (SELECT COALESCE(AVG({_dialect.SecondsBetween("FinishedAt", "StartedAt")}), 0.0)
                           FROM Builds WHERE FinishedAt IS NOT NULL AND StartedAt IS NOT NULL AND QueuedAt >= @Since)
                           AS AverageDurationSeconds,
                       (SELECT COALESCE(AVG({_dialect.SecondsBetween("StartedAt", "QueuedAt")}), 0.0)
                           FROM Builds WHERE StartedAt IS NOT NULL AND QueuedAt >= @Since)
                           AS AverageQueueSeconds;
                   """;

        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<BuildCounts>(new CommandDefinition(
            sql,
            new { Since = DateTime.UtcNow.AddDays(-7) },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<DailyBuildStat>> GetTrendAsync(int days,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 90);
        var since = DateTime.UtcNow.Date.AddDays(-(days - 1));

        var sql = $"""
                   SELECT {_dialect.DateText("QueuedAt")} AS Date,
                          COUNT(1) AS Total,
                          SUM(CASE WHEN Status = 'Succeeded' THEN 1 ELSE 0 END) AS Succeeded,
                          SUM(CASE WHEN Status = 'Failed' THEN 1 ELSE 0 END) AS Failed
                   FROM Builds
                   WHERE QueuedAt >= @Since
                   GROUP BY {_dialect.DateText("QueuedAt")};
                   """;

        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<TrendRow>(new CommandDefinition(
            sql, new { Since = since }, cancellationToken: cancellationToken));
        var byDate = rows.ToDictionary(
            row => row.Date,
            row => new DailyBuildStat(row.Date, (int)row.Total, (int)row.Succeeded, (int)row.Failed),
            StringComparer.Ordinal);

        return Enumerable.Range(0, days)
            .Select(offset =>
            {
                var date = since.AddDays(offset).ToString("yyyy-MM-dd");
                return byDate.TryGetValue(date, out var stat) ? stat : new DailyBuildStat(date, 0, 0, 0);
            })
            .ToList();
    }

    public async Task AddArtifactsAsync(
        Guid buildId,
        IReadOnlyList<CollectedArtifact> artifacts,
        CancellationToken cancellationToken = default)
    {
        if (artifacts.Count == 0) return;

        var now = DateTime.UtcNow;
        var rows = artifacts.Select(artifact => new
        {
            Id = Guid.NewGuid(),
            BuildId = buildId,
            artifact.RelativePath,
            artifact.SizeBytes,
            CreatedAt = now
        });

        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO BuildArtifacts (Id, BuildId, RelativePath, SizeBytes, CreatedAt)
            SELECT @Id, @BuildId, @RelativePath, @SizeBytes, @CreatedAt
            FROM Builds
            WHERE Id = @BuildId AND Status = @Running;
            """,
            rows.Select(row => new
            {
                row.Id,
                row.BuildId,
                row.RelativePath,
                row.SizeBytes,
                row.CreatedAt,
                BuildStatus.Running
            }),
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<BuildArtifact>> ListArtifactsAsync(Guid buildId,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var artifacts = await connection.QueryAsync<BuildArtifact>(new CommandDefinition(
            "SELECT Id, BuildId, RelativePath, SizeBytes, CreatedAt FROM BuildArtifacts WHERE BuildId = @BuildId ORDER BY RelativePath;",
            new { BuildId = buildId },
            cancellationToken: cancellationToken));
        return artifacts.AsList();
    }

    public async Task<BuildArtifact?> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<BuildArtifact>(new CommandDefinition(
            "SELECT Id, BuildId, RelativePath, SizeBytes, CreatedAt FROM BuildArtifacts WHERE Id = @Id;",
            new { Id = artifactId },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> PurgeBuildsAsync(int retentionDays,
        CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0) return [];

        var threshold = DateTime.UtcNow.AddDays(-retentionDays);

        using var connection = connectionFactory.Create();
        var ids = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT Id FROM Builds WHERE FinishedAt IS NOT NULL AND FinishedAt < @Threshold;",
            new { Threshold = threshold },
            cancellationToken: cancellationToken))).AsList();

        if (ids.Count == 0) return [];

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Builds WHERE FinishedAt IS NOT NULL AND FinishedAt < @Threshold;",
            new { Threshold = threshold },
            cancellationToken: cancellationToken));

        return ids;
    }

    /// <summary>
    ///     Marks interrupted builds as failed at service startup.
    ///     Single node (<paramref name="node" /> null) keeps the old behavior and reclaims everything;
    ///     with several nodes only its own are reclaimed, otherwise a newly started node would kill builds running on another one,
    ///     and would also kill builds running on remote runners (whose Node is empty).
    /// </summary>
    public async Task<int> ResetStuckBuildsAsync(string? node = null, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE Builds
             SET Status = @Failed, FinishedAt = @Now, Log = {_dialect.Concat("Log", "@Message")}
             WHERE Status = @Running AND (@Node IS NULL OR Node = @Node);
             """,
            new
            {
                BuildStatus.Failed,
                BuildStatus.Running,
                Now = DateTime.UtcNow,
                Node = node,
                Message = "服務重新啟動，這次建置被中斷。\n"
            },
            cancellationToken: cancellationToken));
    }

    private async Task LockQueueCapacityAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string name = "build-queue-capacity";
        var ensureLock = _dialect.Upsert(
            "SecurityClaims",
            [
                UpsertColumn.Parameter("Name"),
                UpsertColumn.Parameter("Owner"),
                UpsertColumn.Parameter("ClaimedAt")
            ],
            ["Name"],
            null);

        await connection.ExecuteAsync(new CommandDefinition(
            ensureLock,
            new { Name = name, Owner = "capacity", ClaimedAt = DateTime.UtcNow },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT Owner FROM SecurityClaims{_dialect.ForUpdateHint} WHERE Name = @Name{_dialect.ForUpdateSuffix};",
            new { Name = name },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    ///     MySQL and SQL Server compare strings case-insensitively (and MySQL may ignore trailing spaces) under their default collations,
    ///     so the SQL match is only a pre-filter on the index; the reference itself is compared ordinally here to behave the same on all four.
    /// </summary>
    private async Task<IReadOnlyList<Build>> FindBuildsByExternalRefAsync(
        IDbConnection connection,
        IDbTransaction? transaction,
        Guid jobId,
        string externalRef,
        CancellationToken cancellationToken)
    {
        var sql = $"""
                   SELECT {BuildColumns}
                   FROM Builds b
                   INNER JOIN Jobs j ON j.Id = b.JobId
                   WHERE b.JobId = @JobId AND b.ExternalRef = @ExternalRef
                   ORDER BY b.Number;
                   """;

        var rows = await connection.QueryAsync<Build>(new CommandDefinition(
            sql,
            new { JobId = jobId, ExternalRef = externalRef },
            transaction,
            cancellationToken: cancellationToken));

        return Unprotect(rows.Where(build => string.Equals(build.ExternalRef, externalRef, StringComparison.Ordinal)));
    }

    private async Task<Build> InsertBuildAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        Guid jobId,
        string jobName,
        string? jobProject,
        string triggeredBy,
        string? runnerName,
        BuildQueueItem item,
        BuildExternalReference? externalReference,
        CancellationToken cancellationToken)
    {
        const string nextNumberSql = "SELECT COALESCE(MAX(Number), 0) + 1 FROM Builds WHERE JobId = @JobId;";
        const string insertSql = """
                                 INSERT INTO Builds (Id, JobId, Number, Status, TriggeredBy, RunnerName, QueuedAt, Log,
                                                     MatrixLabel, MatrixValues, ParametersJson, SecretParametersJson,
                                                     ExternalRef, TriggerHash)
                                 VALUES (@Id, @JobId, @Number, @Status, @TriggeredBy, @RunnerName, @QueuedAt, @Log,
                                         @MatrixLabel, @MatrixValues, @ParametersJson, @SecretParametersJson,
                                         @ExternalRef, @TriggerHash);
                                 """;

        var nextNumber = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            nextNumberSql, new { JobId = jobId }, transaction, cancellationToken: cancellationToken));

        var build = new Build
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            JobName = jobName,
            JobProject = jobProject,
            Number = nextNumber,
            Status = BuildStatus.Queued,
            TriggeredBy = string.IsNullOrWhiteSpace(triggeredBy) ? "manual" : triggeredBy.Trim(),
            RunnerName = string.IsNullOrWhiteSpace(runnerName) ? null : runnerName.Trim(),
            QueuedAt = DateTime.UtcNow,
            Log = "等待執行器處理…\n",
            MatrixLabel = item.MatrixLabel,
            MatrixValues = item.MatrixValues,
            ParametersJson = item.ParametersJson,
            SecretParametersJson = item.SecretParametersJson,
            ExternalRef = externalReference?.Value,
            TriggerHash = externalReference?.TriggerHash
        };

        await connection.ExecuteAsync(new CommandDefinition(
            insertSql,
            new
            {
                build.Id,
                build.JobId,
                build.Number,
                build.Status,
                build.TriggeredBy,
                build.RunnerName,
                build.QueuedAt,
                build.Log,
                build.MatrixLabel,
                build.MatrixValues,
                build.ParametersJson,
                // Password-type values are encrypted at rest; the returned object keeps the plaintext for the caller.
                SecretParametersJson = protector.Protect(build.SecretParametersJson, SecretPurposes.BuildSecretParameters),
                build.ExternalRef,
                build.TriggerHash
            },
            transaction,
            cancellationToken: cancellationToken));

        return build;
    }

    /// <summary>
    ///     Errors worth retrying: a unique key collision, or the database being busy/deadlocked. The four drivers throw different exception types,
    ///     so rather than depending on each one, look for keywords in the error message.
    /// </summary>
    private static bool IsRetryable(Exception exception)
    {
        var message = exception.Message;
        return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
               || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase)
               || message.Contains("23505", StringComparison.Ordinal)
               || message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
               || message.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Lock wait timeout", StringComparison.OrdinalIgnoreCase)
               // PostgreSQL throws 40001 when it detects a read/write dependency under Serializable;
               // that means "please retry", not an error.
               || message.Contains("could not serialize access", StringComparison.OrdinalIgnoreCase)
               || message.Contains("40001", StringComparison.Ordinal);
    }

    private static Job WithSuccessRate(Job job)
    {
        job.SuccessRate = job.TotalBuilds == 0
            ? 100
            : (int)Math.Round(job.SucceededBuilds * 100.0 / job.TotalBuilds, MidpointRounding.AwayFromZero);
        return job;
    }

    private static DynamicParameters ToParameters(JobDraft draft)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Name", draft.Name.Trim());
        parameters.Add("Project", EmptyToNull(draft.Project));
        parameters.Add("Description", EmptyToNull(draft.Description));
        parameters.Add("Repository", EmptyToNull(draft.Repository));
        parameters.Add("Branch", EmptyToNull(draft.Branch) ?? "main");
        parameters.Add("RunnerName", EmptyToNull(draft.RunnerName));
        parameters.Add("WorkingDirectory", EmptyToNull(draft.WorkingDirectory));
        parameters.Add("Command", draft.Command.Trim());
        parameters.Add("TimeoutMinutes", Math.Clamp(draft.TimeoutMinutes, 1, 1440));
        parameters.Add("ArtifactPath", EmptyToNull(draft.ArtifactPath));
        parameters.Add("TriggerKind", TriggerMode.IsValid(draft.TriggerKind) ? draft.TriggerKind : TriggerMode.Manual);
        parameters.Add("Cron", draft.TriggerKind == TriggerMode.Cron ? EmptyToNull(draft.Cron) : null);
        parameters.Add("NotifyOnFailure", draft.NotifyOnFailure);
        parameters.Add("IsDisabled", draft.IsDisabled);
        parameters.Add("ScriptMode", ScriptModes.IsValid(draft.ScriptMode) ? draft.ScriptMode : ScriptModes.Source);
        parameters.Add("ActionsJson", EmptyToNull(draft.ActionsJson));
        parameters.Add("CachePaths", EmptyToNull(draft.CachePaths));
        parameters.Add("CleanWorkspace", draft.CleanWorkspace);
        parameters.Add("CleanWorkspaceAfterBuild", draft.CleanWorkspaceAfterBuild);
        parameters.Add("MatrixJson", EmptyToNull(draft.MatrixJson));
        parameters.Add("ViewRole", JobAccess.NormalizeViewRole(draft.ViewRole));
        parameters.Add("RunRole", JobAccess.NormalizeRunRole(draft.RunRole));
        parameters.Add("ParametersJson", EmptyToNull(draft.ParametersJson));
        parameters.Add("AllowConcurrentBuilds", draft.AllowConcurrentBuilds);
        parameters.Add("DownstreamJson", EmptyToNull(draft.DownstreamJson));
        parameters.Add("DenyNetwork", draft.DenyNetwork);
        parameters.Add("TrustedUnsandboxed", draft.TrustedUnsandboxed);
        return parameters;
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class QueueJob
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Project { get; init; }
    }

    /// <summary>SQLite's COUNT/SUM return Int64, and when Dapper maps records through a constructor the types must match exactly.</summary>
    // Dapper's target type must have a parameterless constructor. With a positional record, when a query returns zero rows
    // Microsoft.Data.Sqlite cannot determine the column types and reports them all as BLOB, so Dapper finds no
    // matching constructor and throws; a brand-new database is exactly the zero-row case.
    private sealed class TrendRow
    {
        public string Date { get; set; } = string.Empty;
        public long Total { get; set; }
        public long Succeeded { get; set; }
        public long Failed { get; set; }
    }
}