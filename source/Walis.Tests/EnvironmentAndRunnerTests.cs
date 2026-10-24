using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

public class EnvironmentVariableRepositoryTests
{
    [Fact]
    public async Task JobVariableOverridesGlobalVariableWithSameName()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(
            new JobDraft("任務", null, null, null, "main", null, null, "echo hi", 30, null, TriggerMode.Manual, null,
                true, false, ScriptModes.Source, null));

        await database.Variables.CreateAsync(new EnvironmentVariableDraft("NODE_ENV", "development", false, null));
        await database.Variables.CreateAsync(new EnvironmentVariableDraft("SHARED", "global", false, null));
        await database.Variables.CreateAsync(new EnvironmentVariableDraft("NODE_ENV", "production", false, job.Id));

        var resolved = await database.Variables.ResolveForJobAsync(job.Id);

        Assert.Equal("production", resolved["NODE_ENV"]);
        Assert.Equal("global", resolved["SHARED"]);
    }

    [Fact]
    public async Task OtherJobsVariablesAreNotResolved()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var mine = await database.Jobs.CreateJobAsync(
            new JobDraft("我的", null, null, null, "main", null, null, "echo hi", 30, null, TriggerMode.Manual, null,
                true, false, ScriptModes.Source, null));
        var other = await database.Jobs.CreateJobAsync(
            new JobDraft("別人的", null, null, null, "main", null, null, "echo hi", 30, null, TriggerMode.Manual, null,
                true, false, ScriptModes.Source, null));

        await database.Variables.CreateAsync(new EnvironmentVariableDraft("OTHER_ONLY", "值", false, other.Id));

        Assert.DoesNotContain("OTHER_ONLY", (await database.Variables.ResolveForJobAsync(mine.Id)).Keys);
    }

    [Fact]
    public async Task GlobalSecretsAreNotInjectedButJobSecretsAreInjectedAndMasked()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(
            new JobDraft("任務", null, null, null, "main", null, null, "echo hi", 30, null, TriggerMode.Manual, null,
                true, false, ScriptModes.Source, null));

        await database.Variables.CreateAsync(new EnvironmentVariableDraft("GLOBAL_SECRET", "global-secret-value", true,
            null));
        await database.Variables.CreateAsync(new EnvironmentVariableDraft("JOB_SECRET", "job-secret-value", true,
            job.Id));
        await database.Variables.CreateAsync(new EnvironmentVariableDraft("TOO_SHORT", "abc", true, job.Id));

        var resolved = await database.Variables.ResolveForJobAsync(job.Id);
        var masked = await database.Variables.ListSecretValuesForJobAsync(job.Id);

        Assert.DoesNotContain("GLOBAL_SECRET", resolved.Keys);
        Assert.DoesNotContain("TOO_SHORT", resolved.Keys);
        Assert.Equal("job-secret-value", resolved["JOB_SECRET"]);
        Assert.Equal(["job-secret-value"], masked);

        var trustedGlobals = await database.Variables.ResolveGlobalsForTrustedServiceAsync();
        Assert.Equal("global-secret-value", trustedGlobals["GLOBAL_SECRET"]);
    }

    [Fact]
    public async Task OnlySecretVariablesAreListedForMasking()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.Variables.CreateAsync(new EnvironmentVariableDraft("PLAIN", "看得到", false, null));
        await database.Variables.CreateAsync(new EnvironmentVariableDraft("SECRET", "非常機密", true, null));

        var secrets = await database.Variables.ListSecretValuesAsync();

        Assert.Contains("非常機密", secrets);
        Assert.DoesNotContain("看得到", secrets);
    }

    [Fact]
    public async Task DeletingJobAlsoDeletesItsVariables()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var job = await database.Jobs.CreateJobAsync(
            new JobDraft("任務", null, null, null, "main", null, null, "echo hi", 30, null, TriggerMode.Manual, null,
                true, false, ScriptModes.Source, null));
        await database.Variables.CreateAsync(new EnvironmentVariableDraft("SCOPED", "值", false, job.Id));

        await database.Jobs.DeleteJobAsync(job.Id);

        Assert.Empty(await database.Variables.ListAsync());
    }
}

public class RunnerRepositoryTests
{
    private static RunnerDraft Draft(string name = "builder-01", bool paused = false)
    {
        return new RunnerDraft(name, "Ubuntu 24.04", "1.0.0", " Linux , Docker ,linux", 2, paused);
    }

    [Fact]
    public async Task TagsAreLowercasedAndDeduplicated()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var runner = await database.Runners.CreateAsync(Draft());

        Assert.Equal(["linux", "docker"], runner.LabelList);
    }

    [Fact]
    public async Task NewlyRegisteredRunnerIsIdle()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var runner = await database.Runners.CreateAsync(Draft());

        Assert.Equal(RunnerStatus.Idle, runner.Status);
    }

    [Fact]
    public async Task PausedRunnerShowsPausedFirst()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var runner = await database.Runners.CreateAsync(Draft());

        var paused = await database.Runners.SetPausedAsync(runner.Id, true);

        Assert.Equal(RunnerStatus.Paused, paused!.Status);
    }

    [Fact]
    public async Task RunnerIsBusyWhileBuildRuns()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var runner = await database.Runners.CreateAsync(Draft());
        var job = await database.Jobs.CreateJobAsync(
            new JobDraft("任務", null, null, null, "main", runner.Name, null, "echo hi", 30, null, TriggerMode.Manual,
                null, true, false, ScriptModes.Source, null));

        var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", runner.Name);
        await database.Jobs.MarkBuildRunningAsync(build.Id, runner.Name);

        var busy = await database.Runners.GetByNameAsync(runner.Name);

        Assert.Equal(RunnerStatus.Busy, busy!.Status);
        Assert.Equal(1, busy.UsedSlots);
        Assert.Equal(build.Number, busy.CurrentBuildNumber);
        Assert.Equal("任務", busy.CurrentJobName);
    }

    [Fact]
    public async Task BuiltInLocalRunnerCannotBeDeleted()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var local = await database.Runners.GetByNameAsync(Runner.LocalName);

        Assert.False(await database.Runners.DeleteAsync(local!.Id));
        Assert.NotNull(await database.Runners.GetByNameAsync(Runner.LocalName));
    }

    [Fact]
    public async Task DeletingRunnerDisablesEveryKeyBoundToItsName()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var keys = new ApiKeyRepository(database.Factory);
        var runner = await database.Runners.CreateAsync(Draft("Builder-02"));

        // Two keys bound to the runner (one in a different case, the way a key is compared elsewhere), one bound to another runner.
        var first = await keys.CreateAsync(new ApiKeyDraft("k1", UserRole.Operator, null, "Builder-02"), null);
        var second = await keys.CreateAsync(new ApiKeyDraft("k2", UserRole.Operator, null, "builder-02"), null);
        var other = await keys.CreateAsync(new ApiKeyDraft("k3", UserRole.Operator, null, "builder-03"), null);

        var deletion = await database.Runners.DeleteWithKeysAsync(runner.Id);

        Assert.NotNull(deletion);
        Assert.Equal("Builder-02", deletion.Name);
        Assert.Equal(2, deletion.DisabledKeyCount);
        Assert.Null(await database.Runners.GetByNameAsync("Builder-02"));

        var list = await keys.ListAsync();
        Assert.True(list.Single(key => key.Id == first.Key.Id).IsDisabled);
        Assert.True(list.Single(key => key.Id == second.Key.Id).IsDisabled);
        Assert.False(list.Single(key => key.Id == other.Key.Id).IsDisabled);

        // A disabled key no longer authenticates, so it cannot re-register the name either.
        Assert.Null(await keys.AuthenticateAsync(first.Token));
        Assert.NotNull(await keys.AuthenticateAsync(other.Token));

        // Deleting again finds nothing and disables nothing.
        Assert.Null(await database.Runners.DeleteWithKeysAsync(runner.Id));
    }

    [Fact]
    public async Task HeartbeatUpdatesLastSeenTime()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var before = (await database.Runners.GetByNameAsync(Runner.LocalName))!.LastSeenAt;

        await Task.Delay(1_100);
        Assert.True(await database.Runners.HeartbeatAsync(Runner.LocalName));

        var after = (await database.Runners.GetByNameAsync(Runner.LocalName))!.LastSeenAt;
        Assert.True(after > before);
    }
}