using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Walis.Core.Actions;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Plugins;
using Walis.Core.Repositories;

namespace Walis.Tests;

public class BuildLogStreamingTests
{
    [Fact]
    public async Task ReportsOutputWhileRunningInsteadOfWaitingForCompletion()
    {
        if (OperatingSystem.IsWindows()) return;

        // A flag file controls when the command ends, so the test does not gamble on sleep timing.
        // The marker string lives in a file rather than in the command: the log header prints the command verbatim,
        // so echoing the marker directly would put it in the header first and make real output indistinguishable.
        var directory = Directory.CreateTempSubdirectory("walis-stream");
        var flag = Path.Combine(directory.FullName, "flag");
        var first = Path.Combine(directory.FullName, "first");
        var second = Path.Combine(directory.FullName, "second");

        await File.WriteAllTextAsync(first, "前半段的輸出");
        await File.WriteAllTextAsync(second, "後半段的輸出");

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Name = "串流測試",
            Command = $"cat '{first}'; echo; while [ ! -f '{flag}' ]; do sleep 0.1; done; cat '{second}'; echo",
            TimeoutMinutes = 1,
            WorkingDirectory = directory.FullName
        };

        var reports = new List<string>();
        var progress = new SynchronousProgress(log =>
        {
            lock (reports)
            {
                reports.Add(log);
            }
        });

        var execution = new ShellCommandExecutor().ExecuteAsync(job, Options(), progress, CancellationToken.None);

        try
        {
            var midRun = await WaitForReportAsync(reports, log => log.Contains("前半段的輸出"));

            // The command has not finished, so this snapshot must not contain the second half.
            Assert.DoesNotContain("後半段的輸出", midRun);
        }
        finally
        {
            await File.WriteAllTextAsync(flag, string.Empty);
        }

        var result = await execution;
        directory.Delete(true);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.Contains("前半段的輸出", result.Completion.Log);
        Assert.Contains("後半段的輸出", result.Completion.Log);
    }

    private static async Task<string> WaitForReportAsync(List<string> reports, Func<string, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            lock (reports)
            {
                var match = reports.FirstOrDefault(predicate);

                if (match is not null) return match;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("等不到包含預期內容的進度回報。");
    }

    [Fact]
    public async Task WorksAsBeforeWithoutProgressReporter()
    {
        if (OperatingSystem.IsWindows()) return;

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Name = "無回報",
            Command = "echo 只有結果",
            TimeoutMinutes = 1,
            WorkingDirectory = Path.GetTempPath()
        };

        var result = await new ShellCommandExecutor().ExecuteAsync(job, Options(), CancellationToken.None);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.Contains("只有結果", result.Completion.Log);
    }

    [Fact]
    public async Task StandardErrorOutputIsVisible()
    {
        if (OperatingSystem.IsWindows()) return;

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Name = "錯誤輸出",
            Command = "echo 這行到 stderr >&2; exit 3",
            TimeoutMinutes = 1,
            WorkingDirectory = Path.GetTempPath()
        };

        var result = await new ShellCommandExecutor().ExecuteAsync(job, Options(), CancellationToken.None);

        Assert.Equal(BuildStatus.Failed, result.Completion.Status);
        Assert.Equal(3, result.Completion.ExitCode);
        Assert.Contains("這行到 stderr", result.Completion.Log);
    }

    [Fact]
    public async Task LargeOutputWithoutNewlinesIsCappedWhileCollecting()
    {
        if (OperatingSystem.IsWindows()) return;

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Name = "大量輸出",
            Command = "yes x | head -c 1000000",
            TimeoutMinutes = 1,
            WorkingDirectory = Path.GetTempPath()
        };

        var result = await new ShellCommandExecutor().ExecuteAsync(job, Options(), CancellationToken.None);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.True(result.Completion.Log.Length <= 64_000, $"實際保留 {result.Completion.Log.Length} 字元");
        Assert.Contains("日誌已截斷", result.Completion.Log);
    }

    [Fact]
    public async Task PerJobSecretsReceivedByRunnerAreMaskedInProgressAndFinalOutput()
    {
        if (OperatingSystem.IsWindows()) return;

        const string secret = "runner-secret-12345";
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Name = "遠端遮蔽",
            Command = "printf '%s' \"$JOB_TOKEN\"",
            TimeoutMinutes = 1,
            WorkingDirectory = Path.GetTempPath()
        };
        var reports = new List<string>();
        var progress = new SynchronousProgress(log => reports.Add(log));
        var options = new BuildExecutionOptions(
            Path.GetTempPath(),
            null,
            new Dictionary<string, string> { ["JOB_TOKEN"] = secret },
            [secret],
            true);

        var result = await new ShellCommandExecutor().ExecuteAsync(
            job, options, progress, CancellationToken.None);

        Assert.DoesNotContain(secret, result.Completion.Log);
        Assert.Contains("***", result.Completion.Log);
        Assert.All(reports, report => Assert.DoesNotContain(secret, report));
    }

    [Fact]
    public async Task RepeatedSecretAcrossLogCapLeavesNoTruncatedFragment()
    {
        if (OperatingSystem.IsWindows()) return;

        const string secret = "abcdefgh";
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Name = "遮蔽邊界",
            Command = "i=0; while [ $i -lt 9000 ]; do printf '%s' \"$TOKEN\"; i=$((i+1)); done",
            TimeoutMinutes = 1,
            WorkingDirectory = Path.GetTempPath()
        };
        var options = new BuildExecutionOptions(
            Path.GetTempPath(),
            null,
            new Dictionary<string, string> { ["TOKEN"] = secret },
            [secret],
            true);

        var result = await new ShellCommandExecutor().ExecuteAsync(job, options, CancellationToken.None);

        Assert.DoesNotContain(secret, result.Completion.Log);
        Assert.DoesNotContain("abcdefg", result.Completion.Log);
        Assert.Contains("***", result.Completion.Log);
    }

    [Fact]
    public async Task ProgressDoesNotPublishTailThatMayStillBeASecret()
    {
        if (OperatingSystem.IsWindows()) return;

        const string secret = "abcdefgh";
        var reports = new List<string>();
        var progress = new SynchronousProgress(reports.Add);
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Name = "跨進度邊界",
            Command = "printf '%s' \"$TOKEN\" | head -c 3; sleep 2; printf '%s' \"$TOKEN\" | tail -c 5",
            TimeoutMinutes = 1,
            WorkingDirectory = Path.GetTempPath()
        };
        var options = new BuildExecutionOptions(
            Path.GetTempPath(),
            null,
            new Dictionary<string, string> { ["TOKEN"] = secret },
            [secret],
            true);

        var result = await new ShellCommandExecutor().ExecuteAsync(
            job,
            options,
            progress,
            CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.DoesNotContain("abc", report));
        Assert.DoesNotContain(secret, result.Completion.Log);
        Assert.Contains("***", result.Completion.Log);
    }

    [Fact]
    public void ServerReMasksAndLimitsContentReportedByRemoteRunner()
    {
        const string secret = "server-side-secret";
        var input = secret + new string('x', ShellCommandExecutor.MaximumLogLength * 2);

        var sanitized = ShellCommandExecutor.SanitizeLog(input, [secret], true);

        Assert.DoesNotContain(secret, sanitized);
        Assert.Contains("***", sanitized);
        Assert.Contains("日誌已截斷", sanitized);
        Assert.True(sanitized.Length <= ShellCommandExecutor.MaximumLogLength);
    }

    [Fact]
    public void OverlappingSecretsMaskLongerValueFirst()
    {
        var sanitized = ShellCommandExecutor.SanitizeLog(
            "token=abcdef",
            ["abcd", "abcdef"],
            true);

        Assert.Equal("token=***", sanitized);
        Assert.DoesNotContain("ef", sanitized);
    }

    [Fact]
    public async Task RunningOutputIsWrittenToDatabaseAndNotOverwrittenByLateProgress()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var job = await database.Jobs.CreateJobAsync(new JobDraft(
            "串流", null, null, null, "main", null, null, "echo hi", 30, null,
            TriggerMode.Manual, null, true, false, ScriptModes.Source, null));

        var build = await database.Jobs.QueueBuildAsync(job.Id, "test", null);
        await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName);

        await database.Jobs.UpdateBuildLogAsync(build.Id, "進行中的輸出");
        Assert.Contains("進行中的輸出", (await database.Jobs.GetBuildAsync(build.Id))!.Log);

        await database.Jobs.CompleteBuildAsync(build.Id, new BuildCompletion(BuildStatus.Succeeded, 0, "最終輸出"));

        // A progress report arriving after completion must be ignored, otherwise it wipes the result.
        await database.Jobs.UpdateBuildLogAsync(build.Id, "遲到的回報");

        var finished = await database.Jobs.GetBuildAsync(build.Id);
        Assert.Equal("最終輸出", finished!.Log);
    }

    private static BuildExecutionOptions Options()
    {
        return new BuildExecutionOptions(Path.GetTempPath(), null, new Dictionary<string, string>(), [], false);
    }

    /// <summary>Progress&lt;T&gt; posts callbacks back to the synchronization context; calling synchronously in tests is easier to reason about.</summary>
    private sealed class SynchronousProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value)
        {
            handler(value);
        }
    }
}

public class ClaimWorkspaceOptionTests
{
    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public async Task ClaimResolvesCleanWorkspaceFromJobThenSystemSetting(bool? job, bool system, bool expected)
    {
        // A runner cannot read the system settings, so the server has to resolve the job option before handing work over.
        await using var factory = new WalisApiFactory();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var runners = factory.Services.GetRequiredService<IRunnerRepository>();
        var settings = factory.Services.GetRequiredService<ISettingsRepository>();

        var name = $"agent-{Guid.NewGuid():N}"[..16];
        await runners.CreateAsync(new RunnerDraft(name, "macOS", "1.0", null, 1, false));

        var configuration = await settings.GetAsync();
        configuration.CleanWorkspace = system;
        configuration.ExternallyIsolatedRunners = name;
        await settings.SaveAsync(configuration);

        var created = await jobs.CreateJobAsync(new JobDraft(
            $"清空選項 {Guid.NewGuid():N}", null, null, null, "main", name, null,
            "echo hi", 30, null, TriggerMode.Manual, null, true, false, ScriptModes.Source, null)
        {
            CleanWorkspace = job
        });

        await jobs.QueueBuildAsync(created.Id, "test", name);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/runners/{name}/claim");
        request.Headers.Add("X-Walis-Runner-Protocol", "2");
        request.Headers.Add("X-Walis-Runner-Isolation", "external");

        var claimed = await factory.CreateClient().SendAsync(request);
        claimed.EnsureSuccessStatusCode();

        var payload = await claimed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected, payload.GetProperty("cleanWorkspace").GetBoolean());
    }
}

public class BuildLogEndpointTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    [Fact]
    public async Task RemoteRunnerCanReportRunningOutput()
    {
        var jobs = factory.Services.GetRequiredService<IJobRepository>();

        var job = await jobs.CreateJobAsync(new JobDraft(
            $"遠端串流 {Guid.NewGuid():N}", null, null, null, "main", "builder-01", null,
            "echo hi", 30, null, TriggerMode.Manual, null, true, false, ScriptModes.Source, null));

        var build = await jobs.QueueBuildAsync(job.Id, "test", "builder-01");
        var client = factory.CreateClient();

        // A report while still queued must not change the content.
        var ignored = await client.PostAsJsonAsync($"/api/builds/{build.Id}/log", new { log = "還沒開始" });
        Assert.Equal(HttpStatusCode.NoContent, ignored.StatusCode);
        Assert.DoesNotContain("還沒開始", (await jobs.GetBuildAsync(build.Id))!.Log);

        await jobs.MarkBuildRunningAsync(build.Id, "builder-01");

        var response = await client.PostAsJsonAsync($"/api/builds/{build.Id}/log", new { log = "執行到一半" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var running = await client.GetFromJsonAsync<JsonElement>($"/api/builds/{build.Id}");
        Assert.Contains("執行到一半", running.GetProperty("log").GetString());
    }

    [Theory]
    [InlineData(BuildStepStatus.Succeeded)]
    [InlineData(BuildStepStatus.Skipped)]
    public async Task ClaimingWorkReturnsPlannedSteps(string reportedStatus)
    {
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var runners = factory.Services.GetRequiredService<IRunnerRepository>();
        var variables = factory.Services.GetRequiredService<IEnvironmentVariableRepository>();
        var settings = factory.Services.GetRequiredService<ISettingsRepository>();

        var name = $"agent-{Guid.NewGuid():N}"[..16];
        await runners.CreateAsync(new RunnerDraft(name, "macOS", "1.0", null, 1, false));
        var configuration = await settings.GetAsync();
        configuration.AllowUnisolatedBuilds = false;
        configuration.ExternallyIsolatedRunners = name;
        await settings.SaveAsync(configuration);

        var job = await jobs.CreateJobAsync(new JobDraft(
            $"遠端步驟 {Guid.NewGuid():N}", null, null, null, "main", name, null,
            "echo 遠端腳本", 30, null, TriggerMode.Manual, null, true, false, ScriptModes.Source, null));
        var suffix = Guid.NewGuid().ToString("N");
        var globalSecretName = $"GLOBAL_SECRET_{suffix}";
        var scopedSecretName = $"SCOPED_SECRET_{suffix}";
        var globalSecretValue = $"global-value-{suffix}";
        var scopedSecretValue = $"scoped-value-{suffix}";
        await variables.CreateAsync(new EnvironmentVariableDraft(globalSecretName, globalSecretValue, true, null));
        await variables.CreateAsync(new EnvironmentVariableDraft(scopedSecretName, scopedSecretValue, true, job.Id));

        var build = await jobs.QueueBuildAsync(job.Id, "test", name);
        var client = factory.CreateClient();

        using var claimRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/runners/{name}/claim");
        claimRequest.Headers.Add("X-Walis-Runner-Protocol", "2");
        claimRequest.Headers.Add("X-Walis-Runner-Isolation", "external");
        var claimed = await client.SendAsync(claimRequest);
        claimed.EnsureSuccessStatusCode();

        var payload = await claimed.Content.ReadFromJsonAsync<JsonElement>();
        var steps = payload.GetProperty("steps").EnumerateArray().ToList();

        Assert.Single(steps);
        Assert.Equal("腳本", steps[0].GetProperty("name").GetString());
        Assert.Contains("遠端腳本", steps[0].GetProperty("command").GetString());
        Assert.False(payload.GetProperty("variables").TryGetProperty(globalSecretName, out _));
        Assert.Equal(scopedSecretValue, payload.GetProperty("variables").GetProperty(scopedSecretName).GetString());
        Assert.DoesNotContain(globalSecretValue,
            payload.GetProperty("secrets").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(scopedSecretValue,
            payload.GetProperty("secrets").EnumerateArray().Select(item => item.GetString()));
        Assert.True(payload.GetProperty("maskSecrets").GetBoolean());
        Assert.True(payload.GetProperty("requireSandbox").GetBoolean());
        Assert.True(payload.GetProperty("externalIsolationSatisfied").GetBoolean());

        // The remote runner reports the step result.
        var stepId = steps[0].GetProperty("id").GetString();
        var reported = await client.PostAsJsonAsync(
            $"/api/builds/{build.Id}/steps/{stepId}/result",
            new { status = reportedStatus, exitCode = 0, log = "遠端輸出" });

        Assert.Equal(HttpStatusCode.NoContent, reported.StatusCode);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/builds/{build.Id}");
        var detailStep = detail.GetProperty("steps").EnumerateArray().Single();

        Assert.Equal(reportedStatus, detailStep.GetProperty("status").GetString());
        Assert.Contains("遠端輸出", detailStep.GetProperty("log").GetString());
    }

    [Fact]
    public async Task UnisolatedRunnerClaimsWorkOnAFreshInstall()
    {
        // Its own host, so the default settings are really the defaults rather than what a neighbouring test saved.
        await using var isolated = new WalisApiFactory();
        var jobs = isolated.Services.GetRequiredService<IJobRepository>();
        var runners = isolated.Services.GetRequiredService<IRunnerRepository>();
        var name = $"fresh-{Guid.NewGuid():N}"[..16];
        await runners.CreateAsync(new RunnerDraft(name, "Linux", "1.0", null, 1, false));
        var job = await jobs.CreateJobAsync(new JobDraft(
            $"首次 {Guid.NewGuid():N}", null, null, null, "main", name, null,
            "echo first", 30, null, TriggerMode.Manual, null, true, false, ScriptModes.Source, null));
        var build = await jobs.QueueBuildAsync(job.Id, "test", name);
        var client = isolated.CreateClient();

        // Declaring external without being listed is harmless while no boundary is required.
        foreach (var declared in new[] { "none", "external" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/runners/{name}/claim");
            request.Headers.Add("X-Walis-Runner-Protocol", "2");
            request.Headers.Add("X-Walis-Runner-Isolation", declared);
            var response = await client.SendAsync(request);

            if (declared == "none")
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.False(payload.GetProperty("requireSandbox").GetBoolean());
                Assert.False(payload.GetProperty("externalIsolationSatisfied").GetBoolean());
                Assert.Equal(BuildStatus.Running, (await jobs.GetBuildAsync(build.Id))!.Status);
            }
            else
            {
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task OldProtocolOrUnisolatedRunnerIsRefusedBeforeClaiming()
    {
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var runners = factory.Services.GetRequiredService<IRunnerRepository>();
        var strict = factory.Services.GetRequiredService<ISettingsRepository>();
        var configuration = await strict.GetAsync();
        configuration.AllowUnisolatedBuilds = false;
        await strict.SaveAsync(configuration);
        var name = $"safe-{Guid.NewGuid():N}"[..16];
        await runners.CreateAsync(new RunnerDraft(name, "Linux", "old", null, 1, false));
        var job = await jobs.CreateJobAsync(new JobDraft(
            $"握手 {Guid.NewGuid():N}", null, null, null, "main", name, null,
            "echo no", 30, null, TriggerMode.Manual, null, true, false, ScriptModes.Source, null));
        var build = await jobs.QueueBuildAsync(job.Id, "test", name);
        var client = factory.CreateClient();

        var oldRunner = await client.PostAsync($"/api/runners/{name}/claim", null);
        Assert.Equal(HttpStatusCode.UpgradeRequired, oldRunner.StatusCode);
        Assert.Equal(BuildStatus.Queued, (await jobs.GetBuildAsync(build.Id))!.Status);

        using var selfDeclaredExternal = new HttpRequestMessage(HttpMethod.Post, $"/api/runners/{name}/claim");
        selfDeclaredExternal.Headers.Add("X-Walis-Runner-Protocol", "2");
        selfDeclaredExternal.Headers.Add("X-Walis-Runner-Isolation", "external");
        var unapproved = await client.SendAsync(selfDeclaredExternal);
        Assert.Equal(HttpStatusCode.Forbidden, unapproved.StatusCode);
        Assert.Equal(BuildStatus.Queued, (await jobs.GetBuildAsync(build.Id))!.Status);

        using var unisolated = new HttpRequestMessage(HttpMethod.Post, $"/api/runners/{name}/claim");
        unisolated.Headers.Add("X-Walis-Runner-Protocol", "2");
        unisolated.Headers.Add("X-Walis-Runner-Isolation", "none");
        var rejected = await client.SendAsync(unisolated);

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(BuildStatus.Queued, (await jobs.GetBuildAsync(build.Id))!.Status);
    }

    [Fact]
    public async Task CorruptFlowOrDisabledPluginDoesNotFallBackToStaleScript()
    {
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var runners = factory.Services.GetRequiredService<IRunnerRepository>();
        var plugins = factory.Services.GetRequiredService<IPluginRepository>();
        var settings = factory.Services.GetRequiredService<ISettingsRepository>();
        var client = factory.CreateClient();
        var pluginId = $"security.plugin{Guid.NewGuid():N}";
        await plugins.InstallAsync(new PluginDraft(
            new PluginManifest { Id = pluginId, Name = "稍後停用", Kind = PluginKinds.Action },
            "function render() { return 'echo plugin'; }",
            true));
        await plugins.SetDisabledAsync(pluginId, true);

        async Task AssertRejectedAsync(string actionsJson)
        {
            var name = $"fail-{Guid.NewGuid():N}"[..16];
            await runners.CreateAsync(new RunnerDraft(name, "Linux", "1", null, 1, false));
            var configuration = await settings.GetAsync();
            configuration.ExternallyIsolatedRunners = name;
            await settings.SaveAsync(configuration);
            var job = await jobs.CreateJobAsync(new JobDraft(
                $"失效流程 {Guid.NewGuid():N}", null, null, null, "main", name, null,
                "echo STALE_COMMAND_MUST_NOT_RUN", 30, null, TriggerMode.Manual, null, true, false,
                ScriptModes.Actions, actionsJson));
            var build = await jobs.QueueBuildAsync(job.Id, "test", name);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/runners/{name}/claim");
            request.Headers.Add("X-Walis-Runner-Protocol", "2");
            request.Headers.Add("X-Walis-Runner-Isolation", "external");
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var failed = await jobs.GetBuildAsync(build.Id);
            Assert.Equal(BuildStatus.Failed, failed!.Status);
            Assert.Contains("規劃失敗", failed.Log);
            Assert.DoesNotContain("STALE_COMMAND_MUST_NOT_RUN", failed.Log);
            Assert.Empty(await jobs.ListBuildStepsAsync(build.Id));
        }

        await AssertRejectedAsync("{invalid-json");
        await AssertRejectedAsync(JsonSerializer.Serialize(new[]
        {
            new ActionStep { ActionId = pluginId }
        }));
    }

    [Fact]
    public async Task UnreachedStepsAreMarkedSkippedWhenBuildEnds()
    {
        var jobs = factory.Services.GetRequiredService<IJobRepository>();

        var job = await jobs.CreateJobAsync(new JobDraft(
            $"略過 {Guid.NewGuid():N}", null, null, null, "main", "somewhere", null,
            "echo hi", 30, null, TriggerMode.Manual, null, true, false, ScriptModes.Source, null));

        var build = await jobs.QueueBuildAsync(job.Id, "test", "somewhere");
        await jobs.CreateBuildStepsAsync(build.Id, [
            new BuildStepPlan("第一步", "echo 1", false),
            new BuildStepPlan("第二步", "echo 2", false)
        ]);
        await jobs.MarkBuildRunningAsync(build.Id, "somewhere");

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/builds/{build.Id}/result",
            new { status = "Failed", exitCode = 1, log = "第一步就掛了" });

        response.EnsureSuccessStatusCode();

        var steps = await jobs.ListBuildStepsAsync(build.Id);
        Assert.All(steps, step => Assert.Equal(BuildStepStatus.Skipped, step.Status));
    }
}
