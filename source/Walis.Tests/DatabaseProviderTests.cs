using System.Text;
using System.Text.Json;
using Dapper;
using Walis.Core.Backup;
using Walis.Core.Database;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

/// <summary>
///     The external databases are shared, and running concurrently would wipe each other's tables, so these tests run serially.
/// </summary>
[CollectionDefinition("外部資料庫", DisableParallelization = true)]
public sealed class ExternalDatabaseCollection;

/// <summary>
///     The behavior of the same code on four databases. Without the environment variables only SQLite runs;
///     with them the real databases are used, and only a real database can tell whether the dialect is right.
/// </summary>
[Collection("外部資料庫")]
public class DatabaseProviderTests
{
    private static JobDraft Draft(string name = "跨資料庫任務")
    {
        return new JobDraft(name, "Walis", "說明", null, "main", Runner.LocalName, null, "echo hi", 30, null,
            TriggerMode.Manual, null, true, false, ScriptModes.Source, null);
    }

    [Fact]
    public async Task UpgradeRenamesDuplicateJobNamesBeforeMakingThemUnique()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using (var connection = database.Factory.Create())
            await connection.ExecuteAsync("DROP INDEX UX_Jobs_Name;");

        var first = await database.Jobs.CreateJobAsync(DuplicateDraft("deploy"));
        var second = await database.Jobs.CreateJobAsync(DuplicateDraft("Deploy"));

        await new DatabaseInitializer(database.Factory).InitializeAsync();

        Assert.Equal("deploy", (await database.Jobs.GetJobAsync(first.Id))!.Name);
        Assert.Equal("Deploy (2)", (await database.Jobs.GetJobAsync(second.Id))!.Name);

        static JobDraft DuplicateDraft(string name) => new(name, null, null, null, "main", null, null, "echo hi", 5, null,
            TriggerMode.Manual, null, true, false, ScriptModes.Source, null);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task InitializationDisablesLegacyApiKeysButKeepsCurrentOnes(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var apiKeys = new ApiKeyRepository(database.Factory);
        var issued = await apiKeys.CreateAsync(new ApiKeyDraft("current", UserRole.Operator, null), "tester");
        var legacyId = Guid.NewGuid();

        using (var connection = database.Factory.Create())
            await connection.ExecuteAsync(
                $"""
                 INSERT INTO ApiKeys (Id, Name, Prefix, Hash, Role, Scope, IsDisabled, CreatedAt)
                 VALUES (@Id, 'legacy', 'legacy01', 'pbkdf2$210000$c2FsdA==$aGFzaA==', 'admin', 'api',
                         {database.Factory.Dialect.Boolean(false)}, @Now);
                 """,
                new { Id = legacyId, Now = DateTime.UtcNow });

        await new DatabaseInitializer(database.Factory).InitializeAsync();

        var keys = await apiKeys.ListAsync();
        Assert.True(keys.Single(key => key.Id == legacyId).IsDisabled);
        Assert.False(keys.Single(key => key.Id == issued.Key.Id).IsDisabled);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task RepeatedInitializationDoesNotFail(DatabaseProvider provider)
    {
        // The service runs initialization on every start, so the second run must be harmless.
        await using var database = await TemporaryDatabase.CreateAsync(provider);

        await new DatabaseInitializer(database.Factory).InitializeAsync();
        await new DatabaseInitializer(database.Factory).InitializeAsync();

        var jobs = await database.Jobs.ListJobsAsync();
        Assert.NotEmpty(jobs);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task PagedJobQuerySortsByStatisticsOnEveryDatabase(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);

        // The default data already has sample jobs, so a prefix search narrows the scope to these three; the casing deliberately differs to also verify case-insensitivity.
        var never = await database.Jobs.CreateJobAsync(Draft("Paging-沒跑過"));
        var flaky = await database.Jobs.CreateJobAsync(Draft("Paging-常失敗"));
        var solid = await database.Jobs.CreateJobAsync(Draft("Paging-很穩"));

        async Task RunAsync(Guid jobId, string status)
        {
            var build = await database.Jobs.QueueBuildAsync(jobId, "manual", Runner.LocalName);
            await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName);
            await database.Jobs.CompleteBuildAsync(build.Id,
                new BuildCompletion(status, status == BuildStatus.Succeeded ? 0 : 1, "x\n"));
        }

        // Completion times come from the database clock (milliseconds); two finishing in the same millisecond would tie the "last build" sort, so spread them out.
        await RunAsync(flaky.Id, BuildStatus.Failed);
        await Task.Delay(15);
        await RunAsync(flaky.Id, BuildStatus.Succeeded);
        await Task.Delay(15);
        await RunAsync(solid.Id, BuildStatus.Succeeded);

        // Success rate ascending: the frequently failing 50% comes first; never-run counts as 100% and ties with the stable one are broken by name.
        var byRate =
            await database.Jobs.QueryJobsAsync(new JobQuery("pAGING-", Sort: JobSort.SuccessRate, PageSize: 10));
        Assert.Equal(["Paging-常失敗", "Paging-沒跑過", "Paging-很穩"], byRate.Items.Select(job => job.Name).ToArray());

        // Last build descending: those with builds come first (NULL ordering differs per database, so only the relative order of those with builds is checked).
        var byLast =
            await database.Jobs.QueryJobsAsync(new JobQuery("Paging-", Sort: JobSort.LastBuild, Descending: true,
                PageSize: 10));
        var withBuilds = byLast.Items.Where(job => job.LastBuildAt is not null).Select(job => job.Name).ToArray();
        Assert.Equal(["Paging-很穩", "Paging-常失敗"], withBuilds);

        // Paging syntax: skip one, take one (SQL Server uses OFFSET/FETCH).
        var second =
            await database.Jobs.QueryJobsAsync(new JobQuery("Paging-", Sort: JobSort.Name, Page: 2, PageSize: 1));
        Assert.Single(second.Items);
        Assert.Equal(3, second.Total);
        Assert.Equal(never.Name, byRate.Items[1].Name);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task NonConcurrentJobClaimsOnlyOneBuildOnEveryDatabase(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var job = await database.Jobs.CreateJobAsync(Draft("Serial-序列") with { AllowConcurrentBuilds = false });
        var first = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        var second = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        Assert.Equal(first.Id, (await database.Jobs.ClaimNextLocalBuildAsync(Runner.LocalName, "節點一"))!.Id);
        Assert.Null(await database.Jobs.ClaimNextLocalBuildAsync(Runner.LocalName, "節點一"));

        await database.Jobs.CompleteBuildAsync(first.Id, new BuildCompletion(BuildStatus.Succeeded, 0, "ok\n"));
        Assert.Equal(second.Id, (await database.Jobs.ClaimNextLocalBuildAsync(Runner.LocalName, "節點一"))!.Id);

        // The step run conditions and retry counts must be stored and read back.
        await database.Jobs.CreateBuildStepsAsync(second.Id,
        [
            new BuildStepPlan("主要", "echo hi", false),
            new BuildStepPlan("清理", "echo bye", false, RunWhen: StepRunCondition.Always, Retries: 2)
        ]);
        var steps = await database.Jobs.ListBuildStepsAsync(second.Id);
        Assert.Equal(StepRunCondition.Always, steps[1].RunWhen);
        Assert.Equal(2, steps[1].Retries);
        Assert.Equal(StepRunCondition.Success, steps[0].RunWhen);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task FullJobAndBuildLifecycle(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);

        var job = await database.Jobs.CreateJobAsync(Draft());
        var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        Assert.Equal(1, build.Number);
        Assert.Equal(BuildStatus.Queued, build.Status);

        Assert.True(await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName, "節點一"));

        // The log is appended to the existing content: || is logical OR in MySQL, and a mishandled dialect turns this into 0.
        var running = await database.Jobs.GetBuildAsync(build.Id);
        Assert.Contains("等待執行器處理", running!.Log);
        Assert.Contains("開始執行", running.Log);

        // The start time comes from the database clock and must be UTC: SQLite returns a string with Z, and parsing it directly yields local time,
        // so on a UTC+8 machine the duration would be minus eight hours.
        Assert.NotNull(running.StartedAt);
        Assert.InRange((running.StartedAt!.Value - DateTime.UtcNow).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(1));

        await database.Jobs.CompleteBuildAsync(build.Id, new BuildCompletion(BuildStatus.Succeeded, 0, "完成\n"));

        var stored = await database.Jobs.GetBuildAsync(build.Id);
        Assert.NotNull(stored);
        Assert.Equal(BuildStatus.Succeeded, stored!.Status);
        Assert.Equal("節點一", stored.Node);

        var reloaded = await database.Jobs.GetJobAsync(job.Id);
        Assert.Equal(1, reloaded!.TotalBuilds);
        Assert.Equal(1, reloaded.SucceededBuilds);
        Assert.Equal(BuildStatus.Succeeded, reloaded.LastBuildStatus);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task StatisticsAndTrendsComputeSecondsOnEveryDatabase(DatabaseProvider provider)
    {
        // julianday exists only in SQLite; the other three each have their own time-difference function.
        await using var database = await TemporaryDatabase.CreateAsync(provider);

        var job = await database.Jobs.CreateJobAsync(Draft());
        var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName);
        await database.Jobs.CompleteBuildAsync(build.Id, new BuildCompletion(BuildStatus.Succeeded, 0, "ok\n"));

        var counts = await database.Jobs.GetBuildCountsAsync();
        Assert.Equal(1, counts.SucceededLast7Days);
        Assert.True(counts.AverageDurationSeconds >= 0);

        var trend = await database.Jobs.GetTrendAsync(7);
        Assert.Equal(7, trend.Count);
        Assert.Equal(1, trend.Sum(day => day.Total));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task SameBuildIsClaimedOnlyOnce(DatabaseProvider provider)
    {
        // The heart of HA: two nodes claim at once, and only one can flip Queued to Running.
        await using var database = await TemporaryDatabase.CreateAsync(provider);

        var job = await database.Jobs.CreateJobAsync(Draft());
        var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", "builder-01");

        var claims = await Task.WhenAll(
            database.Jobs.ClaimNextBuildAsync("builder-01", "節點一"),
            database.Jobs.ClaimNextBuildAsync("builder-01", "節點二"));

        Assert.Single(claims, claim => claim is not null);
        Assert.Equal(build.Id, claims.First(claim => claim is not null)!.Id);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task InterruptedBuildRecoveryAffectsOnlyOwnNode(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);

        var job = await database.Jobs.CreateJobAsync(Draft());
        var mine = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        var theirs = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        await database.Jobs.MarkBuildRunningAsync(mine.Id, Runner.LocalName, "節點一");
        await database.Jobs.MarkBuildRunningAsync(theirs.Id, Runner.LocalName, "節點二");

        var reset = await database.Jobs.ResetStuckBuildsAsync("節點一");

        Assert.Equal(1, reset);
        Assert.Equal(BuildStatus.Failed, (await database.Jobs.GetBuildAsync(mine.Id))!.Status);
        Assert.Equal(BuildStatus.Running, (await database.Jobs.GetBuildAsync(theirs.Id))!.Status);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task ConcurrentEnqueueNeitherLosesBuildsNorCollidesNumbers(DatabaseProvider provider)
    {
        // When two nodes trigger the same job at once, numbering and inserting interleave.
        // Relying only on UNIQUE(JobId, Number) plus retries eventually fails a whole request,
        // and the user presses the button but gets no build; hence the job row is locked before numbering.
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var job = await database.Jobs.CreateJobAsync(Draft("併發取號"));

        var builds = await Task.WhenAll(Enumerable
            .Range(0, 20)
            .Select(_ => database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName)));

        Assert.Equal(20, builds.Length);
        Assert.Equal(20, builds.Select(build => build.Number).Distinct().Count());
        Assert.Equal(20, (await database.Jobs.GetJobAsync(job.Id))!.TotalBuilds);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task ConcurrentEnqueueWithSameExternalRefInsertsOnce(DatabaseProvider provider)
    {
        // Both requests pass the launcher's pre-check before either has inserted; the check under the job row lock must stop the second.
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var job = await database.Jobs.CreateJobAsync(Draft("重送防護"));
        var reference = new BuildExternalReference("agentgate:run-1", BuildExternalRefs.ComputeTriggerHash(null));

        var results = await Task.WhenAll(Enumerable
            .Range(0, 10)
            .Select(_ => database.Jobs.QueueBuildsAsync(job.Id, "manual", Runner.LocalName,
                [new BuildQueueItem("a"), new BuildQueueItem("b")], reference)));

        Assert.Single(results, result => result.Outcome == BuildQueueOutcome.Queued);
        Assert.All(results, result => Assert.Contains(result.Outcome,
            new[] { BuildQueueOutcome.Queued, BuildQueueOutcome.Deduplicated }));
        Assert.All(results, result => Assert.Equal(2, result.Builds.Count));
        Assert.Equal(2, (await database.Jobs.GetJobAsync(job.Id))!.TotalBuilds);

        // Case differences are different references everywhere, even where the default collation is case-insensitive.
        var upper = await database.Jobs.QueueBuildsAsync(job.Id, "manual", Runner.LocalName, [new BuildQueueItem()],
            reference with { Value = "AGENTGATE:RUN-1" });
        Assert.Equal(BuildQueueOutcome.Queued, upper.Outcome);

        var conflict = await database.Jobs.QueueBuildsAsync(job.Id, "manual", Runner.LocalName, [new BuildQueueItem()],
            reference with { TriggerHash = BuildExternalRefs.ComputeTriggerHash(new Dictionary<string, string> { ["A"] = "1" }) });
        Assert.Equal(BuildQueueOutcome.ExternalRefConflict, conflict.Outcome);

        var stored = await database.Jobs.FindBuildsByExternalRefAsync(job.Id, reference.Value);
        Assert.Equal(["a", "b"], stored.Select(build => build.MatrixLabel));
        Assert.All(stored, build => Assert.Equal(reference.TriggerHash, build.TriggerHash));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task ExternalRefCanBeReusedAfterItsBuildsArePurged(DatabaseProvider provider)
    {
        // Deduplication lasts exactly as long as the build history is kept.
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var job = await database.Jobs.CreateJobAsync(Draft("重送期限"));
        var reference = new BuildExternalReference("agentgate:expire", BuildExternalRefs.ComputeTriggerHash(null));

        var first = await database.Jobs.QueueBuildsAsync(job.Id, "manual", Runner.LocalName, [new BuildQueueItem()],
            reference);
        Assert.Equal(BuildQueueOutcome.Queued, first.Outcome);

        using (var connection = database.Factory.Create())
        {
            await connection.ExecuteAsync(
                "UPDATE Builds SET Status = @Status, StartedAt = @At, FinishedAt = @At WHERE Id = @Id;",
                new { Status = BuildStatus.Succeeded, At = DateTime.UtcNow.AddDays(-10), first.Builds[0].Id });
        }

        Assert.Single(await database.Jobs.PurgeBuildsAsync(5));

        var again = await database.Jobs.QueueBuildsAsync(job.Id, "manual", Runner.LocalName, [new BuildQueueItem()],
            reference);
        Assert.Equal(BuildQueueOutcome.Queued, again.Outcome);
        Assert.NotEqual(first.Builds[0].Id, again.Builds[0].Id);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task NodeRestartDoesNotReclaimRemoteRunnerBuilds(DatabaseProvider provider)
    {
        // A build claimed by a remote runner has no node (no API node is running it);
        // if reclaiming also swept up "no node", restarting another API node would kill
        // builds still running remotely.
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var job = await database.Jobs.CreateJobAsync(Draft("遠端建置"));

        var remote = await database.Jobs.QueueBuildAsync(job.Id, "manual", "builder-01");
        Assert.NotNull(await database.Jobs.ClaimNextBuildAsync("builder-01"));

        var mine = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        await database.Jobs.MarkBuildRunningAsync(mine.Id, Runner.LocalName, "節點一");

        var reset = await database.Jobs.ResetStuckBuildsAsync("節點一");

        Assert.Equal(1, reset);
        Assert.Equal(BuildStatus.Running, (await database.Jobs.GetBuildAsync(remote.Id))!.Status);
        Assert.Equal(BuildStatus.Failed, (await database.Jobs.GetBuildAsync(mine.Id))!.Status);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task PicksOnlyQueuedBuildsItCanRun(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var job = await database.Jobs.CreateJobAsync(Draft("撿工作"));

        var local = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        await database.Jobs.QueueBuildAsync(job.Id, "manual", "builder-01");

        var mine = await database.Jobs.ListQueuedBuildIdsAsync(Runner.LocalName);
        Assert.Equal([local.Id], mine);

        // No name means all of them, for statistics and cleanup.
        Assert.Equal(2, (await database.Jobs.ListQueuedBuildIdsAsync()).Count);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task OtpChallengeIsRecognizedOnAnotherNode(DatabaseProvider provider)
    {
        // Challenges used to live in memory; with several nodes the second sign-in step routed to another node would fail.
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var user = await database.Security.CreateUserAsync(
            new UserDraft("otp", "OTP 使用者", "pw123456", UserRole.Admin));

        var token = await database.Security.CreateOtpChallengeAsync(user.Id, TimeSpan.FromMinutes(5));

        Assert.Equal(user.Id, await database.Security.ConsumeOtpChallengeAsync(token));

        // Once used it cannot be used again.
        Assert.Null(await database.Security.ConsumeOtpChallengeAsync(token));
        Assert.Null(await database.Security.ConsumeOtpChallengeAsync("不存在的權杖"));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task SettingsAndTranslationsCanBeWrittenRepeatedly(DatabaseProvider provider)
    {
        // upsert is written four ways across the four databases: ON CONFLICT, ON DUPLICATE KEY, MERGE.
        await using var database = await TemporaryDatabase.CreateAsync(provider);

        await database.Settings.SaveAsync(new WalisSettings { MaxConcurrentBuilds = 3 });
        await database.Settings.SaveAsync(new WalisSettings { MaxConcurrentBuilds = 5 });

        Assert.Equal(5, (await database.Settings.GetAsync()).MaxConcurrentBuilds);

        await database.Localization.SaveTranslationsAsync("en",
            new Dictionary<string, string> { ["儀表板"] = "Dashboard" });
        await database.Localization.SaveTranslationsAsync("en",
            new Dictionary<string, string> { ["儀表板"] = "Overview" });

        var translations = await database.Localization.ListTranslationsAsync("en");
        Assert.Equal("Overview", translations.Single(entry => entry.Source == "儀表板").Value);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task AccountsAreCaseInsensitive(DatabaseProvider provider)
    {
        // PostgreSQL text comparison is case-sensitive, the other three are not by default; queries always lowercase for consistency.
        await using var database = await TemporaryDatabase.CreateAsync(provider);

        await database.Security.CreateUserAsync(new UserDraft("Leo", "Leo Shiang", "pw123456", UserRole.Admin));

        Assert.NotNull(await database.Security.FindUserAsync("leo"));
        Assert.NotNull(await database.Security.FindUserAsync("LEO"));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task LeaseHasOnlyOneHolderAtATime(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);

        Assert.True(await database.Leases.TryAcquireAsync(LeaseNames.Scheduler, "節點一", TimeSpan.FromMinutes(5)));
        Assert.False(await database.Leases.TryAcquireAsync(LeaseNames.Scheduler, "節點二", TimeSpan.FromMinutes(5)));

        // Renewing your own lease always succeeds.
        Assert.True(await database.Leases.TryAcquireAsync(LeaseNames.Scheduler, "節點一", TimeSpan.FromMinutes(5)));
        Assert.Equal("節點一", await database.Leases.GetOwnerAsync(LeaseNames.Scheduler));

        // After the holder gives it up, someone else can take it.
        await database.Leases.ReleaseAsync(LeaseNames.Scheduler, "節點一");
        Assert.True(await database.Leases.TryAcquireAsync(LeaseNames.Scheduler, "節點二", TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task ExportThenImportPreservesData(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);

        var job = await database.Jobs.CreateJobAsync(Draft("備份來源"));
        var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName, "節點一");
        await database.Jobs.CompleteBuildAsync(build.Id, new BuildCompletion(BuildStatus.Succeeded, 0, "完成\n"));

        var dump = new DatabaseDump(database.Factory);

        using var stream = new MemoryStream();
        var exported = await dump.ExportAsync(stream);
        Assert.True(exported.Rows > 0);

        stream.Position = 0;
        var imported = await dump.ImportAsync(stream);
        Assert.Equal(exported.Rows, imported.Rows);

        var restored = await database.Jobs.GetJobAsync(job.Id);
        Assert.Equal("備份來源", restored!.Name);

        var restoredBuild = await database.Jobs.GetBuildAsync(build.Id);
        Assert.Equal(BuildStatus.Succeeded, restoredBuild!.Status);
        Assert.Equal("節點一", restoredBuild.Node);

        // The time must be UTC, otherwise the frontend treats it as local time and shifts it again.
        Assert.Equal(DateTimeKind.Utc, restoredBuild.QueuedAt.Kind);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ImportAcceptsOnlyExactFormatVersion(int version)
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft("版本錯誤時保留"));
        var header = JsonSerializer.Serialize(new DatabaseDump.DumpHeader(
            version,
            DatabaseProvider.Sqlite.ToString(),
            DateTime.UtcNow,
            []));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(header + "\n"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DatabaseDump(database.Factory).ImportAsync(stream));

        Assert.Contains(version.ToString(), exception.Message);
        Assert.NotNull(await database.Jobs.GetJobAsync(job.Id));
    }

    [Fact]
    public async Task OverlongRowIsRejectedWithinBoundedMemoryAndTransactionRollsBack()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft("過長列時保留"));
        var header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new DatabaseDump.DumpHeader(
            DatabaseDump.FormatVersion,
            DatabaseProvider.Sqlite.ToString(),
            DateTime.UtcNow,
            [])) + "\n");
        using var stream = new MemoryStream(header.Length + DatabaseDump.MaximumLineBytes + 1);
        stream.Write(header);
        var oversizedLine = new byte[DatabaseDump.MaximumLineBytes + 1];
        Array.Fill(oversizedLine, (byte)'x');
        stream.Write(oversizedLine);
        stream.Position = 0;

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DatabaseDump(database.Factory).ImportAsync(stream));

        Assert.Contains("單列", exception.Message);
        Assert.NotNull(await database.Jobs.GetJobAsync(job.Id));
    }

    [Fact]
    public async Task CorruptRowsReportFormatErrorAndTransactionRollsBack()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft("JSON 損壞時保留"));
        var header = JsonSerializer.Serialize(new DatabaseDump.DumpHeader(
            DatabaseDump.FormatVersion,
            DatabaseProvider.Sqlite.ToString(),
            DateTime.UtcNow,
            []));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            header + "\n{\"t\":\"Jobs\",\"r\":{"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DatabaseDump(database.Factory).ImportAsync(stream));

        Assert.Contains("不是有效的資料列", exception.Message);
        Assert.NotNull(await database.Jobs.GetJobAsync(job.Id));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task BackupDoesNotReviveSessionsOrOverwriteOperationalLeases(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var user = await database.Security.CreateUserAsync(
            new UserDraft("backup-user", "Backup User", "backup-password", UserRole.Admin));
        var session = await database.Security.CreateSessionAsync(user.Id, TimeSpan.FromHours(1));
        var challenge = await database.Security.CreateOtpChallengeAsync(user.Id, TimeSpan.FromMinutes(5));
        Assert.True(await database.Leases.TryAcquireAsync(
            LeaseNames.Scheduler,
            "live-node",
            TimeSpan.FromMinutes(5)));

        using var stream = new MemoryStream();
        await new DatabaseDump(database.Factory).ExportAsync(stream);

        stream.Position = 0;
        await new DatabaseDump(database.Factory).ImportAsync(stream);

        Assert.Null(await database.Security.GetSessionAsync(session.Token));
        Assert.Null(await database.Security.ConsumeOtpChallengeAsync(challenge));
        Assert.Equal("live-node", await database.Leases.GetOwnerAsync(LeaseNames.Scheduler));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task LeaseOwnershipChangeBeforeImportPreventsClearingData(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var job = await database.Jobs.CreateJobAsync(Draft("不可被清掉"));
        using var stream = new MemoryStream();
        await new DatabaseDump(database.Factory).ExportAsync(stream);
        Assert.True(await database.Leases.TryAcquireAsync(
            LeaseNames.Maintenance,
            "actual-owner",
            TimeSpan.FromMinutes(5)));

        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DatabaseDump(database.Factory).ImportAsync(
                stream,
                [new ImportLeaseGuard(LeaseNames.Maintenance, "stale-owner", TimeSpan.FromMinutes(3))]));

        Assert.NotNull(await database.Jobs.GetJobAsync(job.Id));
    }

    [Fact]
    public async Task LeaseExpiringDuringImportPreventsCommittingClearedData()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft("租約過期時保留"));
        const string owner = "expiring-import";
        Assert.True(await database.Leases.TryAcquireAsync(
            LeaseNames.Maintenance,
            owner,
            TimeSpan.FromSeconds(2)));

        var header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new DatabaseDump.DumpHeader(
            DatabaseDump.FormatVersion,
            DatabaseProvider.Sqlite.ToString(),
            DateTime.UtcNow,
            [])) + "\n");
        await using var stream = new DelayedEndStream(header, TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DatabaseDump(database.Factory).ImportAsync(
                stream,
                [new ImportLeaseGuard(LeaseNames.Maintenance, owner, TimeSpan.FromMinutes(3))]));

        Assert.NotNull(await database.Jobs.GetJobAsync(job.Id));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task StrictRenewalDoesNotReviveExpiredHolderWithSameName(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        const string owner = "old-owner";
        Assert.True(await database.Leases.TryAcquireAsync(
            LeaseNames.BackupOperation,
            owner,
            TimeSpan.FromMinutes(5)));

        using (var connection = database.Factory.Create())
        {
            await connection.ExecuteAsync(
                "UPDATE Leases SET ExpiresAt = @Expired WHERE Name = @Name;",
                new { Expired = DateTime.UtcNow.AddMinutes(-1), Name = LeaseNames.BackupOperation });
        }

        Assert.False(await database.Leases.RenewAsync(
            LeaseNames.BackupOperation,
            owner,
            TimeSpan.FromMinutes(5)));
        Assert.True(await database.Leases.TryAcquireAsync(
            LeaseNames.BackupOperation,
            "new-owner",
            TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task CanRestoreFromBackupOfAnotherDatabaseKind(DatabaseProvider provider)
    {
        // The export is database-agnostic, so a SQLite backup restores into PostgreSQL and vice versa.
        await using var source = await TemporaryDatabase.CreateAsync();
        var job = await source.Jobs.CreateJobAsync(Draft("從 SQLite 搬過來"));
        await source.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        using var stream = new MemoryStream();
        await new DatabaseDump(source.Factory).ExportAsync(stream);

        await using var target = await TemporaryDatabase.CreateAsync(provider);
        stream.Position = 0;
        await new DatabaseDump(target.Factory).ImportAsync(stream);

        var restored = await target.Jobs.GetJobAsync(job.Id);
        Assert.Equal("從 SQLite 搬過來", restored!.Name);
        Assert.Equal(1, restored.TotalBuilds);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task GlobalAndJobScopedEnvironmentVariablesDoNotConflict(DatabaseProvider provider)
    {
        // Unique indexes are written three ways across the four databases (partial index, functional index); this confirms the semantics match.
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var job = await database.Jobs.CreateJobAsync(Draft());

        await database.Variables.CreateAsync(new EnvironmentVariableDraft("TOKEN", "全域", false, null));
        await database.Variables.CreateAsync(new EnvironmentVariableDraft("TOKEN", "任務", false, job.Id));

        var resolved = await database.Variables.ResolveForJobAsync(job.Id);
        Assert.Equal("任務", resolved["TOKEN"]);
    }

    private sealed class DelayedEndStream(byte[] prefix, TimeSpan delay) : Stream
    {
        private bool _delayed;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => prefix.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            if (!_delayed)
            {
                _delayed = true;
                await Task.Delay(delay, cancellationToken);
            }

            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}