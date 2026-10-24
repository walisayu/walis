using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

public class JobRepositoryTests
{
    private static JobDraft Draft(
        string name = "測試任務",
        string command = "echo hi",
        string trigger = TriggerMode.Manual,
        string? cron = null,
        bool disabled = false)
    {
        return new JobDraft(name, "Walis", "說明", null, "main", Runner.LocalName, null, command, 30, null, trigger, cron,
            true,
            disabled, ScriptModes.Source, null);
    }

    [Fact]
    public async Task StatisticsAreAvailableWithoutAnyBuilds()
    {
        // A brand-new database has no builds, so AVG returns NULL; if the fallback were an integer 0,
        // SQLite would type the column INTEGER, Dapper could not match the double parameter of BuildCounts,
        // and the whole dashboard page would return 500.
        await using var database = await TemporaryDatabase.CreateAsync();

        var counts = await database.Jobs.GetBuildCountsAsync();

        Assert.Equal(0, counts.BuildsLast7Days);
        Assert.Equal(0d, counts.AverageDurationSeconds);
        Assert.Equal(0d, counts.AverageQueueSeconds);
    }

    [Fact]
    public async Task TrendFillsEveryDayWithoutAnyBuilds()
    {
        // When a query returns zero rows Microsoft.Data.Sqlite cannot determine the column types and reports them all as BLOB;
        // with a positional record as target type Dapper finds no constructor and throws.
        await using var database = await TemporaryDatabase.CreateAsync();

        var trend = await database.Jobs.GetTrendAsync(7);

        Assert.Equal(7, trend.Count);
        Assert.All(trend, day => Assert.Equal(0, day.Total));
    }

    [Fact]
    public async Task ResolvesEmptySetWithoutEnvironmentVariables()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());

        var resolved = await database.Variables.ResolveForJobAsync(job.Id);

        Assert.Empty(resolved);
    }

    [Fact]
    public async Task InitializationCreatesDefaultJobsAndLocalRunner()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var jobs = await database.Jobs.ListJobsAsync();
        var runners = await database.Runners.ListAsync();

        Assert.NotEmpty(jobs);
        Assert.Contains(runners, runner => runner.IsLocal && runner.Name == Runner.LocalName);
    }

    [Fact]
    public async Task CreatedJobReadsBackIdentically()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var created = await database.Jobs.CreateJobAsync(Draft("  前端建置  ", " npm run build "));

        Assert.Equal("前端建置", created.Name);
        Assert.Equal("npm run build", created.Command);
        Assert.Equal("main", created.Branch);
        Assert.Equal(100, created.SuccessRate);

        var fetched = await database.Jobs.GetJobAsync(created.Id);
        Assert.Equal(created.Name, fetched!.Name);
    }

    [Fact]
    public async Task NonScheduledJobDoesNotKeepCron()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var created = await database.Jobs.CreateJobAsync(Draft(trigger: TriggerMode.Manual, cron: "30 2 * * *"));

        Assert.Null(created.Cron);
    }

    [Fact]
    public async Task UpdatingAndDisablingJobIsReflectedInQueries()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var created = await database.Jobs.CreateJobAsync(Draft());

        var updated = await database.Jobs.UpdateJobAsync(created.Id, Draft("改名後", "ls -al"));
        Assert.Equal("改名後", updated!.Name);
        Assert.Equal("ls -al", updated.Command);

        var disabled = await database.Jobs.SetJobEnabledAsync(created.Id, false);
        Assert.True(disabled!.IsDisabled);

        Assert.True(await database.Jobs.DeleteJobAsync(created.Id));
        Assert.Null(await database.Jobs.GetJobAsync(created.Id));
    }

    [Fact]
    public async Task BuildNumbersIncrementPerJob()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var first = await database.Jobs.CreateJobAsync(Draft("任務一"));
        var second = await database.Jobs.CreateJobAsync(Draft("任務二"));

        var firstBuild = await database.Jobs.QueueBuildAsync(first.Id, "manual", Runner.LocalName);
        var secondBuild = await database.Jobs.QueueBuildAsync(first.Id, "manual", Runner.LocalName);
        var otherJobBuild = await database.Jobs.QueueBuildAsync(second.Id, "manual", Runner.LocalName);

        Assert.Equal(1, firstBuild.Number);
        Assert.Equal(2, secondBuild.Number);
        Assert.Equal(1, otherJobBuild.Number);
    }

    [Fact]
    public async Task PendingBuildCapacityIsCheckedTransactionallyAndNeverPartiallyEnqueued()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft($"容量-{Guid.NewGuid():N}"));
        var fifty = Enumerable.Repeat(new BuildQueueItem(), JobRepository.MaximumBuildsPerTrigger).ToArray();

        Assert.Equal(50, (await database.Jobs.TryQueueBuildsAsync(
            job.Id, "test", "remote-never", fifty))!.Count);
        Assert.Equal(49, (await database.Jobs.TryQueueBuildsAsync(
            job.Id, "test", "remote-never", fifty[..49]))!.Count);

        var rejectedBatch = await database.Jobs.TryQueueBuildsAsync(
            job.Id,
            "test",
            "remote-never",
            [new BuildQueueItem("a"), new BuildQueueItem("b")]);
        Assert.Null(rejectedBatch);
        Assert.Equal(99, (await database.Jobs.ListBuildsAsync(
            new BuildQuery(null, null, job.Id, null, 200))).Count);

        // Two racing for the last slot: only one may succeed; the capacity lock prevents the check-then-insert race.
        var contenders = await Task.WhenAll(
            database.Jobs.TryQueueBuildsAsync(job.Id, "test", "remote-never", [new BuildQueueItem("c")]),
            database.Jobs.TryQueueBuildsAsync(job.Id, "test", "remote-never", [new BuildQueueItem("d")]));
        Assert.Single(contenders, result => result is { Count: 1 });
        Assert.Single(contenders, result => result is null);
        Assert.Equal(JobRepository.MaximumActiveBuildsPerJob, (await database.Jobs.ListBuildsAsync(
            new BuildQuery(null, null, job.Id, null, 200))).Count);
    }

    [Fact]
    public async Task BuildLifecycleUpdatesStatusAndSuccessRate()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());

        var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        Assert.Equal(BuildStatus.Queued, build.Status);

        Assert.True(await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName));
        Assert.False(await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName));

        await database.Jobs.CompleteBuildAsync(build.Id, new BuildCompletion(BuildStatus.Succeeded, 0, "完成"));

        var finished = await database.Jobs.GetBuildAsync(build.Id);
        Assert.Equal(BuildStatus.Succeeded, finished!.Status);
        Assert.Equal(0, finished.ExitCode);
        Assert.NotNull(finished.DurationSeconds);
        Assert.Equal("測試任務", finished.JobName);

        var refreshed = await database.Jobs.GetJobAsync(job.Id);
        Assert.Equal(1, refreshed!.TotalBuilds);
        Assert.Equal(100, refreshed.SuccessRate);
        Assert.Equal(BuildStatus.Succeeded, refreshed.LastBuildStatus);
    }

    [Fact]
    public async Task RepositoryCompletesOnlyRunningBuildsAndFinalResultIsImmutable()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());
        var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        await database.Jobs.CompleteBuildAsync(
            build.Id,
            new BuildCompletion(BuildStatus.Succeeded, 0, "不應寫入"));
        var queued = await database.Jobs.GetBuildAsync(build.Id);
        Assert.Equal(BuildStatus.Queued, queued!.Status);
        Assert.DoesNotContain("不應寫入", queued.Log);

        Assert.True(await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName));
        await database.Jobs.CompleteBuildAsync(
            build.Id,
            new BuildCompletion(BuildStatus.Succeeded, 0, "final"));
        await database.Jobs.CompleteBuildAsync(
            build.Id,
            new BuildCompletion(BuildStatus.Failed, 1, "late overwrite"));

        var finished = await database.Jobs.GetBuildAsync(build.Id);
        Assert.Equal(BuildStatus.Succeeded, finished!.Status);
        Assert.Equal("final", finished.Log);
    }

    [Fact]
    public async Task StepStatusAdvancesOnlyWhileBuildRunsAndIsImmutableAfterCompletion()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());
        var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        await database.Jobs.CreateBuildStepsAsync(
            build.Id,
            [new BuildStepPlan("test", "echo hi", false)]);
        var step = (await database.Jobs.ListBuildStepsAsync(build.Id)).Single();

        await database.Jobs.MarkBuildStepRunningAsync(step.Id);
        Assert.Equal(BuildStepStatus.Pending, (await database.Jobs.ListBuildStepsAsync(build.Id)).Single().Status);

        Assert.True(await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName));
        await database.Jobs.MarkBuildStepRunningAsync(step.Id);
        await database.Jobs.UpdateBuildStepLogAsync(step.Id, "running");
        await database.Jobs.CompleteBuildStepAsync(
            step.Id,
            BuildStepStatus.Succeeded,
            0,
            "final step");
        await database.Jobs.CompleteBuildStepAsync(
            step.Id,
            BuildStepStatus.Failed,
            1,
            "late step overwrite");

        var completedStep = (await database.Jobs.ListBuildStepsAsync(build.Id)).Single();
        Assert.Equal(BuildStepStatus.Succeeded, completedStep.Status);
        Assert.Equal("final step", completedStep.Log);

        await database.Jobs.CompleteBuildAsync(
            build.Id,
            new BuildCompletion(BuildStatus.Succeeded, 0, "done"));
        await database.Jobs.UpdateBuildStepLogAsync(step.Id, "after build");

        completedStep = (await database.Jobs.ListBuildStepsAsync(build.Id)).Single();
        Assert.Equal("final step", completedStep.Log);
    }

    [Fact]
    public async Task ArtifactIndexAcceptsWritesOnlyForRunningBuilds()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());
        var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        await database.Jobs.AddArtifactsAsync(
            build.Id,
            [new CollectedArtifact("queued.txt", 1)]);
        Assert.Empty(await database.Jobs.ListArtifactsAsync(build.Id));

        Assert.True(await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName));
        await database.Jobs.AddArtifactsAsync(
            build.Id,
            [new CollectedArtifact("running.txt", 2)]);
        Assert.Single(await database.Jobs.ListArtifactsAsync(build.Id));

        await database.Jobs.CompleteBuildAsync(
            build.Id,
            new BuildCompletion(BuildStatus.Succeeded, 0, "done"));
        await database.Jobs.AddArtifactsAsync(
            build.Id,
            [new CollectedArtifact("late.txt", 3)]);

        var artifacts = await database.Jobs.ListArtifactsAsync(build.Id);
        Assert.Single(artifacts);
        Assert.Equal("running.txt", artifacts[0].RelativePath);
    }

    [Fact]
    public async Task SuccessRateCountsCompletedBuilds()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());

        foreach (var status in new[]
                     { BuildStatus.Succeeded, BuildStatus.Succeeded, BuildStatus.Failed, BuildStatus.Succeeded })
        {
            var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
            await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName);
            await database.Jobs.CompleteBuildAsync(build.Id,
                new BuildCompletion(status, status == BuildStatus.Succeeded ? 0 : 1, ""));
        }

        var refreshed = await database.Jobs.GetJobAsync(job.Id);
        Assert.Equal(4, refreshed!.TotalBuilds);
        Assert.Equal(75, refreshed.SuccessRate);
    }

    [Fact]
    public async Task CleanWorkspaceAfterBuildIsStoredAsATriState()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var unset = await database.Jobs.CreateJobAsync(Draft());
        Assert.Null(unset.CleanWorkspaceAfterBuild);

        var on = await database.Jobs.CreateJobAsync(Draft() with { Name = "開", CleanWorkspaceAfterBuild = true });
        Assert.True(on.CleanWorkspaceAfterBuild);

        var updated = await database.Jobs.UpdateJobAsync(on.Id, Draft() with { Name = "關", CleanWorkspaceAfterBuild = false });
        Assert.False(updated!.CleanWorkspaceAfterBuild);

        var cleared = await database.Jobs.UpdateJobAsync(on.Id, Draft() with { Name = "跟隨" });
        Assert.Null(cleared!.CleanWorkspaceAfterBuild);
    }

    [Fact]
    public async Task CompletingABuildReportsWhetherThisCallChangedIt()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());
        var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        // Still queued: nothing to finish.
        Assert.Equal(0, await database.Jobs.CompleteBuildAsync(build.Id, new BuildCompletion(BuildStatus.Succeeded, 0, "太早")));

        await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName);
        Assert.Equal(1, await database.Jobs.CompleteBuildAsync(build.Id, new BuildCompletion(BuildStatus.Succeeded, 0, "第一次")));
        Assert.Equal(0, await database.Jobs.CompleteBuildAsync(build.Id, new BuildCompletion(BuildStatus.Failed, 1, "第二次")));

        var stored = (await database.Jobs.GetBuildAsync(build.Id))!;
        Assert.Equal(BuildStatus.Succeeded, stored.Status);
        Assert.Equal("第一次", stored.Log);
    }

    [Fact]
    public async Task QueuedBuildsAreCanceledAndRunningBuildsGetACancelRequest()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());

        var queued = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        Assert.Equal(BuildCancellation.Canceled, await database.Jobs.CancelBuildAsync(queued.Id));
        Assert.Equal(BuildStatus.Canceled, (await database.Jobs.GetBuildAsync(queued.Id))!.Status);

        // Running: the row keeps its status, the executor is asked to stop and decides the final state itself.
        var running = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        await database.Jobs.MarkBuildRunningAsync(running.Id, Runner.LocalName);
        Assert.False(await database.Jobs.IsCancelRequestedAsync(running.Id));
        Assert.Equal(BuildCancellation.Requested, await database.Jobs.CancelBuildAsync(running.Id));

        var flagged = (await database.Jobs.GetBuildAsync(running.Id))!;
        Assert.Equal(BuildStatus.Running, flagged.Status);
        Assert.NotNull(flagged.CancelRequestedAt);
        Assert.True(await database.Jobs.IsCancelRequestedAsync(running.Id));

        // Asking again is idempotent and keeps the first timestamp.
        Assert.Equal(BuildCancellation.Requested, await database.Jobs.CancelBuildAsync(running.Id));
        Assert.Equal(flagged.CancelRequestedAt, (await database.Jobs.GetBuildAsync(running.Id))!.CancelRequestedAt);

        // Finished builds cannot be cancelled.
        await database.Jobs.CompleteBuildAsync(running.Id, new BuildCompletion(BuildStatus.Canceled, null, "stopped"));
        Assert.Equal(BuildCancellation.NotCancelable, await database.Jobs.CancelBuildAsync(running.Id));
        Assert.Equal(BuildCancellation.NotCancelable, await database.Jobs.CancelBuildAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task PreviousBuildStatusDecidesRecovery()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());

        var failed = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        await database.Jobs.MarkBuildRunningAsync(failed.Id, Runner.LocalName);
        await database.Jobs.CompleteBuildAsync(failed.Id, new BuildCompletion(BuildStatus.Failed, 1, ""));

        var next = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        Assert.Equal(BuildStatus.Failed, await database.Jobs.GetPreviousBuildStatusAsync(job.Id, next.Number));
    }

    [Fact]
    public async Task FiltersApplyToBuildQueries()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft("查詢測試"));

        var succeeded = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        await database.Jobs.MarkBuildRunningAsync(succeeded.Id, Runner.LocalName);
        await database.Jobs.CompleteBuildAsync(succeeded.Id, new BuildCompletion(BuildStatus.Succeeded, 0, ""));
        await database.Jobs.QueueBuildAsync(job.Id, "schedule", "remote-1");

        Assert.Single(await database.Jobs.ListBuildsAsync(new BuildQuery(BuildStatus.Succeeded, null, null, null, 20)));
        Assert.Single(await database.Jobs.ListBuildsAsync(new BuildQuery(null, "remote-1", null, null, 20)));
        Assert.Equal(2, (await database.Jobs.ListBuildsAsync(new BuildQuery(null, null, job.Id, null, 20))).Count);
        Assert.Equal(2, (await database.Jobs.ListBuildsAsync(new BuildQuery(null, null, null, "查詢", 20))).Count);
        Assert.Empty(await database.Jobs.ListBuildsAsync(new BuildQuery(null, null, null, "不存在", 20)));
    }

    [Fact]
    public async Task BuildPageReturnsOneSliceAndTheMatchingTotal()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());

        for (var index = 0; index < 5; index++) await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        var first = await database.Jobs.QueryBuildsAsync(new BuildQuery(null, null, null, null, 30, Page: 1, PageSize: 2));
        var second = await database.Jobs.QueryBuildsAsync(new BuildQuery(null, null, null, null, 30, Page: 2, PageSize: 2));
        var third = await database.Jobs.QueryBuildsAsync(new BuildQuery(null, null, null, null, 30, Page: 3, PageSize: 2));

        Assert.Equal(5, first.Total);
        Assert.Equal(2, first.PageSize);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Single(third.Items);

        // The newest build heads the first page, and the pages together cover every build exactly once.
        Assert.Equal(5, first.Items[0].Number);
        var numbers = first.Items.Concat(second.Items).Concat(third.Items).Select(build => build.Number).Order();
        Assert.Equal([1, 2, 3, 4, 5], numbers.ToArray());
    }

    [Fact]
    public async Task BuildPageBeyondTheLastPageIsEmptyButStillReportsTheTotal()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());
        await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        // Like the job query, the repository answers the page that was asked for; falling back to the last page is the view's job.
        var beyond =
            await database.Jobs.QueryBuildsAsync(new BuildQuery(null, null, null, null, 30, Page: 9, PageSize: 10));

        Assert.Equal(9, beyond.Page);
        Assert.Equal(1, beyond.Total);
        Assert.Empty(beyond.Items);

        var empty =
            await database.Jobs.QueryBuildsAsync(new BuildQuery(null, null, null, "不存在", 30, Page: 4, PageSize: 10));

        Assert.Equal(0, empty.Total);
        Assert.Empty(empty.Items);
    }

    [Fact]
    public async Task BuildPageCountsPerStatusIgnoreTheStatusFilter()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());

        foreach (var status in new[] { BuildStatus.Succeeded, BuildStatus.Succeeded, BuildStatus.Failed })
        {
            var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
            await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName);
            await database.Jobs.CompleteBuildAsync(build.Id, new BuildCompletion(status, 0, ""));
        }

        await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        var page = await database.Jobs.QueryBuildsAsync(new BuildQuery(BuildStatus.Failed, null, null, null, 30));

        Assert.Equal(1, page.Total);
        Assert.Single(page.Items);
        Assert.Equal(2, page.Counts[BuildStatus.Succeeded]);
        Assert.Equal(1, page.Counts[BuildStatus.Failed]);
        Assert.Equal(1, page.Counts[BuildStatus.Queued]);
        Assert.Equal(0, page.Counts[BuildStatus.Canceled]);
    }

    [Fact]
    public async Task BuildPageHidesBuildsOfJobsTheRoleCannotSee()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var open = await database.Jobs.CreateJobAsync(Draft("公開任務"));
        var restricted = await database.Jobs.CreateJobAsync(Draft("限管理員") with { ViewRole = UserRole.Admin });

        await database.Jobs.QueueBuildAsync(open.Id, "manual", Runner.LocalName);
        await database.Jobs.QueueBuildAsync(restricted.Id, "manual", Runner.LocalName);

        var viewer = await database.Jobs.QueryBuildsAsync(new BuildQuery(null, null, null, null, 30, [UserRole.Viewer]));

        Assert.Equal(1, viewer.Total);
        Assert.Equal(open.Id, viewer.Items[0].JobId);
        Assert.Equal(1, viewer.Counts[BuildStatus.Queued]);

        var administrator = await database.Jobs.QueryBuildsAsync(
            new BuildQuery(null, null, null, null, 30, [UserRole.Viewer, UserRole.Operator, UserRole.Admin]));

        Assert.Equal(2, administrator.Total);

        var nobody = await database.Jobs.QueryBuildsAsync(new BuildQuery(null, null, null, null, 30, []));

        Assert.Equal(0, nobody.Total);
        Assert.Empty(nobody.Items);
    }

    [Fact]
    public async Task RestartMarksRunningBuildsFailed()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());
        var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName);

        Assert.Equal(1, await database.Jobs.ResetStuckBuildsAsync());
        Assert.Equal(BuildStatus.Failed, (await database.Jobs.GetBuildAsync(build.Id))!.Status);
    }

    [Fact]
    public async Task TrendQueryFillsDaysWithoutBuilds()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());
        await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        var trend = await database.Jobs.GetTrendAsync(7);

        Assert.Equal(7, trend.Count);
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), trend[^1].Date);
        Assert.Equal(1, trend[^1].Total);
    }

    [Fact]
    public async Task RemoteRunnerClaimsOnlyBuildsAssignedToIt()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());
        await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        var remote = await database.Jobs.QueueBuildAsync(job.Id, "manual", "remote-1");

        Assert.Null(await database.Jobs.ClaimNextBuildAsync("remote-2"));

        var claimed = await database.Jobs.ClaimNextBuildAsync("remote-1");
        Assert.Equal(remote.Id, claimed!.Id);
        Assert.Equal(BuildStatus.Running, claimed.Status);
        Assert.Null(await database.Jobs.ClaimNextBuildAsync("remote-1"));
    }

    [Fact]
    public async Task PagedQueryFiltersSortsAndPagesInDatabase()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        // The default data already has sample jobs; statistics are compared against that baseline, and list assertions use search to narrow to the jobs created here.
        var baseline = await database.Jobs.QueryJobsAsync(new JobQuery());

        // 30 jobs: three projects in rotation, every 4th paused, every 5th scheduled, every 7th visible to admins only.
        for (var i = 1; i <= 30; i++)
            await database.Jobs.CreateJobAsync(new JobDraft(
                $"任務 {i:D2}", $"專案{(char)('A' + i % 3)}", null, null, "main", Runner.LocalName, null, "echo hi", 30,
                null,
                i % 5 == 0 ? TriggerMode.Cron : TriggerMode.Manual, i % 5 == 0 ? "0 * * * *" : null, true, i % 4 == 0,
                ScriptModes.Source, null, ViewRole: i % 7 == 0 ? UserRole.Admin : UserRole.Viewer));

        // Page two, 10 per page: 10 rows, total 30, statistics unaffected by paging.
        var page = await database.Jobs.QueryJobsAsync(new JobQuery("任務 ", Page: 2, PageSize: 10));
        Assert.Equal(10, page.Items.Count);
        Assert.Equal(30, page.Total);
        Assert.Equal(baseline.TotalAll + 30, page.TotalAll);
        Assert.Equal(baseline.EnabledAll + 23, page.EnabledAll);
        Assert.Equal(["專案A", "專案B", "專案C"], page.Projects.Where(project => project.StartsWith("專案")).ToArray());
        Assert.Equal("任務 11", page.Items[0].Name);

        // Past the last page: empty, but the total stays.
        var beyond = await database.Jobs.QueryJobsAsync(new JobQuery("任務 ", Page: 9, PageSize: 10));
        Assert.Empty(beyond.Items);
        Assert.Equal(30, beyond.Total);

        // Search is case-insensitive and matches name, project and branch.
        var search = await database.Jobs.QueryJobsAsync(new JobQuery("專案b"));
        Assert.Equal(10, search.Total);
        Assert.All(search.Items, job => Assert.Equal("專案B", job.Project));

        // Status, trigger kind and project filters.
        Assert.Equal(7, (await database.Jobs.QueryJobsAsync(new JobQuery("任務 ", JobStatusFilter.Paused))).Total);
        Assert.Equal(23, (await database.Jobs.QueryJobsAsync(new JobQuery("任務 ", JobStatusFilter.Enabled))).Total);
        Assert.Equal(6, (await database.Jobs.QueryJobsAsync(new JobQuery("任務 ", TriggerKind: TriggerMode.Cron))).Total);
        Assert.Equal(10, (await database.Jobs.QueryJobsAsync(new JobQuery(Project: "專案C"))).Total);

        // Permissions: a viewer sees only viewer-level jobs, and the statistics and project list likewise count only visible ones.
        var asViewer =
            await database.Jobs.QueryJobsAsync(new JobQuery("任務 ", ViewRoles: [UserRole.Viewer], PageSize: 100));
        Assert.Equal(26, asViewer.Total);
        Assert.Equal(baseline.TotalAll + 26, asViewer.TotalAll);
        Assert.DoesNotContain(asViewer.Items, job => job.ViewRole == UserRole.Admin);
        var nothing = await database.Jobs.QueryJobsAsync(new JobQuery(ViewRoles: []));
        Assert.Empty(nothing.Items);
        Assert.Equal(0, nothing.TotalAll);

        // Sort: name descending, the first row is the last one.
        var desc = await database.Jobs.QueryJobsAsync(new JobQuery("任務 ", Sort: JobSort.Name, Descending: true,
            PageSize: 1));
        Assert.Equal("任務 30", desc.Items[0].Name);
    }

    [Fact]
    public async Task IsolationFlagsRoundTripAndDefaultToOff()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var plain = await database.Jobs.CreateJobAsync(Draft("一般任務"));
        Assert.False(plain.DenyNetwork);
        Assert.False(plain.TrustedUnsandboxed);

        var draft = Draft("信任任務") with { DenyNetwork = true, TrustedUnsandboxed = true };
        var trusted = await database.Jobs.CreateJobAsync(draft);
        Assert.True(trusted.DenyNetwork);
        Assert.True(trusted.TrustedUnsandboxed);

        var updated = await database.Jobs.UpdateJobAsync(trusted.Id, draft with { TrustedUnsandboxed = false });
        Assert.True(updated!.DenyNetwork);
        Assert.False(updated.TrustedUnsandboxed);
        Assert.False((await database.Jobs.GetJobAsync(trusted.Id))!.TrustedUnsandboxed);
    }

    [Fact]
    public async Task NonConcurrentJobRunsOneBuildAtATime()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var draft = Draft("序列化任務") with { AllowConcurrentBuilds = false };
        var job = await database.Jobs.CreateJobAsync(draft);
        Assert.False(job.AllowConcurrentBuilds);

        var first = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
        var second = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        Assert.True(await database.Jobs.MarkBuildRunningAsync(first.Id, Runner.LocalName));
        Assert.False(await database.Jobs.MarkBuildRunningAsync(second.Id, Runner.LocalName));
        Assert.Equal(BuildStatus.Queued, (await database.Jobs.GetBuildAsync(second.Id))!.Status);

        // The claim must skip it too, otherwise every round picks the same one.
        Assert.Null(await database.Jobs.ClaimNextLocalBuildAsync(Runner.LocalName, "節點一"));

        await database.Jobs.CompleteBuildAsync(first.Id, new BuildCompletion(BuildStatus.Succeeded, 0, "ok\n"));
        Assert.True(await database.Jobs.MarkBuildRunningAsync(second.Id, Runner.LocalName));
    }

    [Fact]
    public async Task BuildParametersAndDownstreamAreStoredWithJob()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var draft = Draft("有參數") with
        {
            ParametersJson = """[{"name":"ENV","type":"choice","options":["stg","prod"]}]""",
            DownstreamJson = """[{"jobName":"部署","when":"success"}]"""
        };
        var job = await database.Jobs.CreateJobAsync(draft);
        Assert.Contains("ENV", job.ParametersJson);
        Assert.Contains("部署", job.DownstreamJson);

        var queued = await database.Jobs.TryQueueBuildsAsync(
            job.Id, "manual", Runner.LocalName,
            [new BuildQueueItem(null, null, """{"ENV":"prod"}""", """{"TOKEN":"s3cret"}""")]);
        var build = (await database.Jobs.GetBuildAsync(queued![0].Id))!;
        Assert.Equal("""{"ENV":"prod"}""", build.ParametersJson);
        Assert.Equal("""{"TOKEN":"s3cret"}""", build.SecretParametersJson);

        Assert.Equal(job.Id, (await database.Jobs.GetJobByNameAsync("有參數"))!.Id);
        Assert.Null(await database.Jobs.GetJobByNameAsync("不存在"));
    }

    [Fact]
    public async Task CleanWorkspaceKeepsAllThreeStatesThroughCreateAndUpdate()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        // Null is what every existing job has, and it must stay null so those jobs keep following the system setting.
        var job = await database.Jobs.CreateJobAsync(Draft("跟隨系統設定"));
        Assert.Null(job.CleanWorkspace);

        var always = await database.Jobs.CreateJobAsync(Draft("每次清空") with { CleanWorkspace = true });
        Assert.True(always.CleanWorkspace);

        var never = await database.Jobs.UpdateJobAsync(always.Id, Draft("每次清空") with { CleanWorkspace = false });
        Assert.False(never!.CleanWorkspace);

        var backToDefault = await database.Jobs.UpdateJobAsync(always.Id, Draft("每次清空") with { CleanWorkspace = null });
        Assert.Null(backToDefault!.CleanWorkspace);

        var listed = (await database.Jobs.ListJobsAsync()).First(item => item.Id == job.Id);
        Assert.Null(listed.CleanWorkspace);
    }

    [Fact]
    public async Task BuildCannotEnterRunningWhileMaintenanceLeaseActive()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(Draft());
        var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);

        Assert.True(await database.Leases.TryAcquireAsync(
            LeaseNames.Maintenance,
            "restore-test",
            TimeSpan.FromMinutes(5)));
        Assert.False(await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName));
        Assert.Equal(BuildStatus.Queued, (await database.Jobs.GetBuildAsync(build.Id))!.Status);

        await database.Leases.ReleaseAsync(LeaseNames.Maintenance, "restore-test");
        Assert.True(await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName));
    }
}