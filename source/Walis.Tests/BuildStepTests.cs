using Walis.Api.Services;
using Walis.Core.Actions;
using Walis.Core.Execution;
using Walis.Core.Models;

namespace Walis.Tests;

public class ActionPlanTests
{
    private static readonly Dictionary<string, ActionDefinition> Definitions = new()
    {
        ["test.echo"] = new ActionDefinition
        {
            Id = "test.echo",
            Name = "輸出訊息",
            Parameters = [new ActionParameter { Name = "message", Label = "訊息", Type = ActionParameterType.Text }],
            Template = "echo {{message}}"
        },
        ["test.required"] = new ActionDefinition
        {
            Id = "test.required",
            Name = "有必填",
            Parameters = [new ActionParameter { Name = "target", Label = "目標", Required = true }],
            Template = "deploy {{target}}"
        }
    };

    [Fact]
    public void EachActionBecomesOneStep()
    {
        var result = ActionScriptCompiler.Plan(
            [
                new ActionStep { ActionId = "test.echo", Parameters = { ["message"] = "一" } },
                new ActionStep { ActionId = "test.echo", Name = "自訂名稱", Parameters = { ["message"] = "二" } }
            ],
            Definitions);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal("輸出訊息", result.Steps[0].Name);
        Assert.Equal("自訂名稱", result.Steps[1].Name);
        Assert.Contains("echo '一'", result.Steps[0].Command);
    }

    [Fact]
    public void DisabledActionsDoNotBecomeSteps()
    {
        var result = ActionScriptCompiler.Plan(
            [
                new ActionStep { ActionId = "test.echo", Enabled = false, Parameters = { ["message"] = "略過" } },
                new ActionStep { ActionId = "test.echo", Parameters = { ["message"] = "執行" } }
            ],
            Definitions);

        Assert.Single(result.Steps);
        Assert.Contains("執行", result.Steps[0].Command);
    }

    [Fact]
    public void MissingRequiredFieldReportsError()
    {
        var result = ActionScriptCompiler.Plan(
            [new ActionStep { ActionId = "test.required" }],
            Definitions);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("目標"));
    }

    [Fact]
    public void PlanningAndCompilationShareParameterResolution()
    {
        ActionStep[] steps = [new() { ActionId = "test.echo", Parameters = { ["message"] = "同一份" } }];

        var compiled = ActionScriptCompiler.Compile(steps, Definitions);
        var planned = ActionScriptCompiler.Plan(steps, Definitions);

        // The script content of both must match; the only difference is granularity.
        Assert.Contains(planned.Steps[0].Command, compiled.Script);
    }
}

public class BuildStepRunnerTests
{
    [Fact]
    public async Task RunsAllStepsInOrderRecordingEachOutput()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var (job, build) = await SetupAsync(database, [
            new BuildStepPlan("第一步", "echo 一號輸出", false),
            new BuildStepPlan("第二步", "echo 二號輸出", false)
        ]);

        var result = await CreateRunner(database).RunAsync(job, build, Options(), CancellationToken.None);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);

        var steps = await database.Jobs.ListBuildStepsAsync(build.Id);
        Assert.All(steps, step => Assert.Equal(BuildStepStatus.Succeeded, step.Status));
        Assert.Contains("一號輸出", steps[0].Log);
        Assert.Contains("二號輸出", steps[1].Log);

        // The aggregated log is still complete, so the old UI and notifications are unaffected.
        Assert.Contains("一號輸出", result.Completion.Log);
        Assert.Contains("二號輸出", result.Completion.Log);
    }

    [Fact]
    public async Task FailedStepMarksFollowingStepsSkipped()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var (job, build) = await SetupAsync(database, [
            new BuildStepPlan("會失敗", "echo 失敗前; exit 7", false),
            new BuildStepPlan("不該執行", "echo 不應該看到這行", false)
        ]);

        var result = await CreateRunner(database).RunAsync(job, build, Options(), CancellationToken.None);

        Assert.Equal(BuildStatus.Failed, result.Completion.Status);
        Assert.Equal(7, result.Completion.ExitCode);

        var steps = await database.Jobs.ListBuildStepsAsync(build.Id);
        Assert.Equal(BuildStepStatus.Failed, steps[0].Status);
        Assert.Equal(BuildStepStatus.Skipped, steps[1].Status);
        Assert.DoesNotContain("不應該看到這行", result.Completion.Log);
    }

    [Fact]
    public async Task ContinueOnErrorDoesNotStopFollowingSteps()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var (job, build) = await SetupAsync(database, [
            new BuildStepPlan("允許失敗", "exit 3", true),
            new BuildStepPlan("仍要執行", "echo 有跑到", false)
        ]);

        var result = await CreateRunner(database).RunAsync(job, build, Options(), CancellationToken.None);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);

        var steps = await database.Jobs.ListBuildStepsAsync(build.Id);
        Assert.Equal(BuildStepStatus.Failed, steps[0].Status);
        Assert.Equal(BuildStepStatus.Succeeded, steps[1].Status);
        Assert.Contains("有跑到", result.Completion.Log);
    }

    [Fact]
    public async Task StepFilesLiveInAPrivateScratchDirectoryOfTheWorkspaceThatSurvivesGitClean()
    {
        var workspace = Directory.CreateTempSubdirectory("walis-scratch-");

        try
        {
            await using var database = await TemporaryDatabase.CreateAsync();
            var (job, build) = await SetupAsync(database, [
                new BuildStepPlan("位置", "echo \"FILE=$WALIS_ENV_FILE\"; rm -rf .walis", false),
                new BuildStepPlan("寫入", "echo \"AFTER_CLEAN=ok\" >> \"$WALIS_ENV_FILE\"", false),
                new BuildStepPlan("讀取", "echo \"值是 $AFTER_CLEAN\"", false)
            ]);
            var options = new BuildExecutionOptions(workspace.FullName, null, new Dictionary<string, string>(), [], false);

            var result = await CreateRunner(database).RunAsync(job, build, options, CancellationToken.None);

            Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
            var steps = await database.Jobs.ListBuildStepsAsync(build.Id);
            // Not the shared temp directory, where a concurrent build of another job could read the values.
            Assert.Contains($"{Path.DirectorySeparatorChar}.walis{Path.DirectorySeparatorChar}build-", steps[0].Log);
            Assert.Contains("值是 ok", steps[2].Log);
            // Removed with the build.
            Assert.False(Directory.Exists(Path.Combine(workspace.FullName, ".walis")));
        }
        finally { workspace.Delete(true); }
    }

    [Fact]
    public async Task WalisEnvFilePassesValuesToLaterSteps()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var (job, build) = await SetupAsync(database, [
            new BuildStepPlan("設定變數", "echo \"BUILT_VERSION=1.2.3\" >> \"$WALIS_ENV_FILE\"", false),
            new BuildStepPlan("讀取變數", "echo 版本是 $BUILT_VERSION", false)
        ]);

        var result = await CreateRunner(database).RunAsync(job, build, Options(), CancellationToken.None);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);

        var steps = await database.Jobs.ListBuildStepsAsync(build.Id);
        Assert.Contains("版本是 1.2.3", steps[1].Log);
    }

    [Fact]
    public async Task CleanAfterBuildEmptiesAnAllowlistedWorkspaceAndRefusesOthers()
    {
        var directory = Directory.CreateTempSubdirectory("walis-clean-after");

        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(directory.FullName, "ws")).FullName;
            await File.WriteAllTextAsync(Path.Combine(workspace, "token.txt"), "secret");
            Directory.CreateDirectory(Path.Combine(workspace, "src"));

            // Not allowlisted: the host process must not delete anything.
            var refused = BuildStepSequence.CleanAfterBuild(workspace, Options() with { CleanWorkspaceAfterBuild = true });
            Assert.StartsWith("略過建置後清空工作目錄", refused);
            Assert.True(File.Exists(Path.Combine(workspace, "token.txt")));

            var allowed = Options() with
            {
                CleanWorkspaceAfterBuild = true,
                AllowedWorkingDirectories = [workspace]
            };
            var cleaned = BuildStepSequence.CleanAfterBuild(workspace, allowed);

            Assert.Contains("移除 2 個項目", cleaned);
            Assert.True(Directory.Exists(workspace));
            Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task BackgroundProcessesLeftByAStepAreTerminatedWhenItEnds()
    {
        if (OperatingSystem.IsWindows() || !ProcessGroups.IsSupported) return;

        var directory = Directory.CreateTempSubdirectory("walis-stragglers");

        try
        {
            var marker = Path.Combine(directory.FullName, "still-alive");
            await using var database = await TemporaryDatabase.CreateAsync();

            // A detached subshell that would write the marker after the step has returned; the tree kill cannot see it.
            var (job, build) = await SetupAsync(database, [
                new BuildStepPlan(
                    "留下背景程序",
                    $"(sleep 2; touch '{marker}') > /dev/null 2>&1 & echo started",
                    false)
            ]);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await CreateRunner(database).RunAsync(job, build, Options(), CancellationToken.None);
            watch.Stop();

            Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
            Assert.Contains("已一併終止", result.Completion.Log);

            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(File.Exists(marker), "步驟結束後背景程序仍在執行。");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task EnvFileCannotOverrideSystemVariablesOrHome()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var database = await TemporaryDatabase.CreateAsync();
        var (job, build) = await SetupAsync(database, [
            new BuildStepPlan(
                "改寫系統變數",
                "printf 'WALIS_STEP_build_version=hacked\nWALIS_BUILD_NUMBER=999\nHOME=/nonexistent\nZDOTDIR=/nonexistent\nPLAIN=ok\n' >> \"$WALIS_ENV_FILE\"",
                false),
            new BuildStepPlan(
                "讀回",
                "printf '%s|%s|%s home=%s\n' \"$WALIS_STEP_build_version\" \"$WALIS_BUILD_NUMBER\" \"$PLAIN\" \"$HOME\"",
                false)
        ]);

        var options = Options() with
        {
            Variables = new Dictionary<string, string>
            {
                ["WALIS_STEP_build_version"] = "1.2.3",
                ["WALIS_BUILD_NUMBER"] = "7"
            }
        };

        var result = await CreateRunner(database).RunAsync(job, build, options, CancellationToken.None);
        var steps = await database.Jobs.ListBuildStepsAsync(build.Id);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.Contains("1.2.3|7|ok home=", steps[1].Log);
        Assert.DoesNotContain("/nonexistent", steps[1].Log);
    }

    [Fact]
    public async Task EnvFileReplacedBySymlinkIsNeitherReadNorTruncated()
    {
        if (OperatingSystem.IsWindows()) return;

        var directory = Directory.CreateTempSubdirectory("walis-env-link");

        try
        {
            var target = Path.Combine(directory.FullName, "target.env");
            await File.WriteAllTextAsync(target, "INJECTED=不該匯入\n");
            await using var database = await TemporaryDatabase.CreateAsync();
            var (job, build) = await SetupAsync(database, [
                new BuildStepPlan(
                    "置換連結",
                    $"printf 'SAFE_VALUE=ok\\n' >> \"$WALIS_ENV_FILE\"; rm \"$WALIS_ENV_FILE\"; ln -s '{target}' \"$WALIS_ENV_FILE\"",
                    false),
                new BuildStepPlan("驗證", "printf '%s|%s\\n' \"${SAFE_VALUE:-missing}\" \"${INJECTED:-missing}\"", false)
            ]);

            var result = await CreateRunner(database).RunAsync(job, build, Options(), CancellationToken.None);
            var steps = await database.Jobs.ListBuildStepsAsync(build.Id);

            Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
            Assert.Contains("ok|missing", steps[1].Log);
            Assert.Equal("INJECTED=不該匯入\n", await File.ReadAllTextAsync(target));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CleanOrCacheWithoutAllowlistFailsBeforeTouchingWorkspace()
    {
        var directory = Directory.CreateTempSubdirectory("walis-preflight");

        try
        {
            var marker = Path.Combine(directory.FullName, "must-stay.txt");
            await File.WriteAllTextAsync(marker, "保留");
            await using var database = await TemporaryDatabase.CreateAsync();
            var job = await database.Jobs.CreateJobAsync(new JobDraft(
                $"清理安全檢查 {Guid.NewGuid():N}", null, null, null, "main", null, directory.FullName,
                "echo 不該執行", 30, null, TriggerMode.Manual, null, true, false, ScriptModes.Source, null));
            var build = await database.Jobs.QueueBuildAsync(job.Id, "test", null);
            await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName);
            await database.Jobs.CreateBuildStepsAsync(build.Id, [new BuildStepPlan("腳本", job.Command, false)]);
            var options = Options() with { CleanWorkspace = true };

            var result = await CreateRunner(database).RunAsync(job, build, options, CancellationToken.None);

            Assert.Equal(BuildStatus.Failed, result.Completion.Status);
            Assert.Contains("必須設定允許的工作目錄", result.Completion.Log);
            Assert.True(File.Exists(marker));
            Assert.All(await database.Jobs.ListBuildStepsAsync(build.Id), step =>
                Assert.Equal(BuildStepStatus.Skipped, step.Status));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task MultiStepAggregateLogIsAlsoCapped()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var database = await TemporaryDatabase.CreateAsync();
        var plans = Enumerable.Range(1, 4)
            .Select(number => new BuildStepPlan($"大量輸出 {number}", "yes x | head -c 70000", false))
            .ToArray();
        var (job, build) = await SetupAsync(database, plans);

        var result = await CreateRunner(database).RunAsync(job, build, Options(), CancellationToken.None);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.True(result.Completion.Log.Length <= 200_000, $"實際保留 {result.Completion.Log.Length} 字元");
        Assert.Contains("彙總日誌已截斷", result.Completion.Log);
    }

    [Fact]
    public async Task StepNameAndNumberArePassedAsEnvironmentVariables()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var (job, build) = await SetupAsync(database, [
            new BuildStepPlan("命名的步驟", "echo \"$WALIS_STEP_NUMBER-$WALIS_STEP_NAME\"", false)
        ]);

        await CreateRunner(database).RunAsync(job, build, Options(), CancellationToken.None);

        var steps = await database.Jobs.ListBuildStepsAsync(build.Id);
        Assert.Contains("1-命名的步驟", steps[0].Log);
    }

    private static BuildStepRunner CreateRunner(TemporaryDatabase database)
    {
        return new BuildStepRunner(database.Jobs, new ShellCommandExecutor());
    }

    private static BuildExecutionOptions Options()
    {
        return new BuildExecutionOptions(Path.GetTempPath(), null, new Dictionary<string, string>(), [], false);
    }

    private static async Task<(Job Job, Build Build)> SetupAsync(
        TemporaryDatabase database,
        IReadOnlyList<BuildStepPlan> plans)
    {
        var job = await database.Jobs.CreateJobAsync(new JobDraft(
            $"步驟測試 {Guid.NewGuid():N}", null, null, null, "main", null, Path.GetTempPath(),
            "echo fallback", 30, null, TriggerMode.Manual, null, true, false, ScriptModes.Source, null));

        var build = await database.Jobs.QueueBuildAsync(job.Id, "test", null);
        await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName);
        await database.Jobs.CreateBuildStepsAsync(build.Id, plans);

        return (job, build);
    }
}

public class ParallelStepTests
{
    [Fact]
    public async Task StepsMarkedParallelRunConcurrently()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        // Three steps each sleep one second; in parallel the total is close to one second rather than three.
        var (job, build) = await SetupAsync(database, [
            new BuildStepPlan("第一", "sleep 1; echo 一", false),
            new BuildStepPlan("第二", "sleep 1; echo 二", false, true),
            new BuildStepPlan("第三", "sleep 1; echo 三", false, true)
        ]);

        var started = DateTime.UtcNow;
        var result = await CreateRunner(database).RunAsync(job, build, Options(), CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.True(elapsed < TimeSpan.FromSeconds(2.5), $"耗時 {elapsed.TotalSeconds:0.#} 秒，看起來沒有平行執行");

        var steps = await database.Jobs.ListBuildStepsAsync(build.Id);
        Assert.All(steps, step => Assert.Equal(BuildStepStatus.Succeeded, step.Status));
        Assert.Contains("一", steps[0].Log);
        Assert.Contains("三", steps[2].Log);
    }

    [Fact]
    public async Task FailureInParallelGroupStopsFollowingGroups()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var (job, build) = await SetupAsync(database, [
            new BuildStepPlan("會失敗", "exit 4", false),
            new BuildStepPlan("同時跑", "echo 有跑到", false, true),
            new BuildStepPlan("後面的", "echo 不該執行", false)
        ]);

        var result = await CreateRunner(database).RunAsync(job, build, Options(), CancellationToken.None);

        Assert.Equal(BuildStatus.Failed, result.Completion.Status);

        var steps = await database.Jobs.ListBuildStepsAsync(build.Id);
        Assert.Equal(BuildStepStatus.Failed, steps[0].Status);

        // The other step of the same group still ran to completion; what got skipped is the following group.
        Assert.Equal(BuildStepStatus.Succeeded, steps[1].Status);
        Assert.Equal(BuildStepStatus.Skipped, steps[2].Status);
    }

    [Fact]
    public async Task ParallelStepsHaveSeparateEnvFiles()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var (job, build) = await SetupAsync(database, [
            new BuildStepPlan("設 A", "echo \"FROM_A=甲\" >> \"$WALIS_ENV_FILE\"", false),
            new BuildStepPlan("設 B", "echo \"FROM_B=乙\" >> \"$WALIS_ENV_FILE\"", false, true),
            new BuildStepPlan("讀取", "echo \"$FROM_A-$FROM_B\"", false)
        ]);

        var result = await CreateRunner(database).RunAsync(job, build, Options(), CancellationToken.None);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);

        // Values written by both parallel steps must reach later steps without overwriting each other.
        var steps = await database.Jobs.ListBuildStepsAsync(build.Id);
        Assert.Contains("甲-乙", steps[2].Log);
    }

    private static BuildStepRunner CreateRunner(TemporaryDatabase database)
    {
        return new BuildStepRunner(database.Jobs, new ShellCommandExecutor());
    }

    private static BuildExecutionOptions Options()
    {
        return new BuildExecutionOptions(Path.GetTempPath(), null, new Dictionary<string, string>(), [], false);
    }

    private static async Task<(Job Job, Build Build)> SetupAsync(
        TemporaryDatabase database,
        IReadOnlyList<BuildStepPlan> plans)
    {
        var job = await database.Jobs.CreateJobAsync(new JobDraft(
            $"平行測試 {Guid.NewGuid():N}", null, null, null, "main", null, Path.GetTempPath(),
            "echo fallback", 30, null, TriggerMode.Manual, null, true, false, ScriptModes.Source, null));

        var build = await database.Jobs.QueueBuildAsync(job.Id, "test", null);
        await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName);
        await database.Jobs.CreateBuildStepsAsync(build.Id, plans);

        return (job, build);
    }
}