using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Walis.Core.Actions;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

public class StepExpressionTests
{
    [Fact]
    public void ValueWithReferencesCompilesToSingleQuotedLiteralJoinedWithDoubleQuotedVariable()
    {
        var rendered = ActionTemplate.Render("echo {{tag}}", new Dictionary<string, string?>
        {
            ["tag"] = "registry/app:${{ steps.build.version }}-it's"
        });

        Assert.Equal("echo 'registry/app:'\"${WALIS_STEP_build_version}\"'-it'\\''s'", rendered);
    }

    [Fact]
    public void VerbatimInsertionReplacesReferencesWithDoubleQuotedVariablesAndLeavesRestUntouched()
    {
        var rendered = ActionTemplate.Render("{{{command}}}", new Dictionary<string, string?>
        {
            ["command"] = "deploy --tag ${{steps.build.version}} $HOME"
        });

        Assert.Equal("deploy --tag \"${WALIS_STEP_build_version}\" $HOME", rendered);
    }

    [Fact]
    public void ValueWithoutReferencesStaysSingleQuotedLiteral()
    {
        Assert.Equal("'${{ not.an.expression }}'", ActionTemplate.Quote("${{ not.an.expression }}"));
        Assert.Equal("'a$b'", ActionTemplate.Quote("a$b"));
    }

    [Fact]
    public void OutputFileMayBeOnePerLineOrJsonObject()
    {
        var problems = new List<string>();
        var lines = StepExpressions.ParseOutputs("version=1.2.3\n# 註解\nbad line\nimage=a=b\n_ok=x\n9no=y\n", problems);

        Assert.Equal("1.2.3", lines["version"]);
        Assert.Equal("a=b", lines["image"]);
        Assert.Equal("x", lines["_ok"]);
        Assert.False(lines.ContainsKey("9no"));
        Assert.Equal(2, problems.Count);

        problems.Clear();
        var json = StepExpressions.ParseOutputs("""{"version":"2.0","count":3,"ok":true,"nested":{"a":1},"none":null}""", problems);

        Assert.Equal("2.0", json["version"]);
        Assert.Equal("3", json["count"]);
        Assert.Equal("true", json["ok"]);
        Assert.Equal("""{"a":1}""", json["nested"]);
        Assert.Equal("", json["none"]);
        Assert.Empty(problems);
    }

    [Fact]
    public void KeyRules()
    {
        Assert.True(StepExpressions.IsValidKey("build_1"));
        Assert.False(StepExpressions.IsValidKey("Build"));
        Assert.False(StepExpressions.IsValidKey("1build"));
        Assert.False(StepExpressions.IsValidKey(new string('a', 41)));
    }
}

public class StepReferenceCompileTests
{
    private static readonly Dictionary<string, ActionDefinition> Definitions =
        BuiltInActions.All.ToDictionary(definition => definition.Id);

    private static ActionStep Step(string? key, string command, bool parallel = false, bool enabled = true)
    {
        return new ActionStep
        {
            Id = Guid.NewGuid().ToString(),
            ActionId = "shell.run",
            Key = key,
            Parallel = parallel,
            Enabled = enabled,
            Parameters = new Dictionary<string, string?> { ["command"] = command }
        };
    }

    [Fact]
    public void ReferencesAreValidOnlyToEarlierKeyedSteps()
    {
        var ok = ActionScriptCompiler.Plan([Step("build", "make"), Step(null, "echo ${{ steps.build.version }}")], Definitions);
        Assert.True(ok.IsValid);
        Assert.Equal("build", ok.Steps[0].Key);
        Assert.Contains("\"${WALIS_STEP_build_version}\"", ok.Steps[1].Command);

        var forward = ActionScriptCompiler.Plan([Step(null, "echo ${{ steps.build.version }}"), Step("build", "make")], Definitions);
        Assert.Contains(forward.Errors, error => error.Contains("steps.build.version"));

        var sameGroup = ActionScriptCompiler.Plan([Step("build", "make"), Step(null, "echo ${{ steps.build.version }}", parallel: true)], Definitions);
        Assert.Contains(sameGroup.Errors, error => error.Contains("平行"));

        var disabled = ActionScriptCompiler.Plan([Step("build", "make", enabled: false), Step(null, "echo ${{ steps.build.version }}")], Definitions);
        Assert.Contains(disabled.Errors, error => error.Contains("steps.build.version"));
    }

    [Fact]
    public void KeysMustBeValidAndUnique()
    {
        var bad = ActionScriptCompiler.Compile([Step("Build", "make")], Definitions);
        Assert.Contains(bad.Errors, error => error.Contains("不合法"));

        var duplicate = ActionScriptCompiler.Compile([Step("build", "make"), Step("build", "make")], Definitions);
        Assert.Contains(duplicate.Errors, error => error.Contains("重複"));
    }
}

public class StepOutputSequenceTests
{
    [Fact]
    public async Task StepOutputsAreInjectedAsEnvironmentVariablesAndReportedToSinkWithSecretsMasked()
    {
        if (OperatingSystem.IsWindows()) return;

        var directory = Directory.CreateTempSubdirectory("walis-out");
        var job = new Job
        {
            Id = Guid.NewGuid(), Name = "輸出測試", Command = "true", TimeoutMinutes = 1,
            WorkingDirectory = directory.FullName
        };
        var options = new BuildExecutionOptions(directory.FullName, null, new Dictionary<string, string>(), ["s3cret"], true);
        var sink = new RecordingSink();
        BuildStep[] steps =
        [
            new()
            {
                Id = Guid.NewGuid(), Number = 1, Name = "build", StepKey = "build",
                Command = "printf 'version=1.2.3\\ntoken=s3cret\\n' >> \"$WALIS_OUTPUT_FILE\""
            },
            new()
            {
                Id = Guid.NewGuid(), Number = 2, Name = "nokey",
                Command = "echo '{\"note\":\"x\"}' > \"$WALIS_OUTPUT_FILE\""
            },
            new()
            {
                Id = Guid.NewGuid(), Number = 3, Name = "use",
                Command = "echo \"v=${WALIS_STEP_build_version} t=${WALIS_STEP_build_token} n=${WALIS_STEP_nokey_note:-unset}\""
            }
        ];

        var result = await new BuildStepSequence(new ShellCommandExecutor()).RunAsync(job, Guid.NewGuid(), steps,
            options, sink, CancellationToken.None);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.Equal("1.2.3", sink.Outputs[1]["version"]);
        Assert.Equal("***", sink.Outputs[1]["token"]);
        Assert.Equal("x", sink.Outputs[2]["note"]);
        Assert.Empty(sink.Outputs[3]);
        Assert.Contains("v=1.2.3 t=*** n=unset", sink.Logs[3]);
        Assert.Contains("輸出 2 個：version, token", result.Completion.Log);
    }

    private sealed class RecordingSink : IBuildStepSink
    {
        public Dictionary<int, string> Logs { get; } = [];
        public Dictionary<int, IReadOnlyDictionary<string, string>> Outputs { get; } = [];

        public Task MarkRunningAsync(BuildStep step, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportStepLogAsync(BuildStep step, string log, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteStepAsync(BuildStep step, string status, int? exitCode, string log,
            IReadOnlyDictionary<string, string> outputs, CancellationToken cancellationToken)
        {
            Logs[step.Number] = log;
            Outputs[step.Number] = outputs;
            return Task.CompletedTask;
        }

        public Task SkipRemainingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportBuildLogAsync(string log, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public class StepOutputApiTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    [Fact]
    public async Task RemotelyReportedOutputsAreFilteredAndStoredOnStep()
    {
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var runners = factory.Services.GetRequiredService<IRunnerRepository>();
        var name = $"out-{Guid.NewGuid():N}"[..16];
        await runners.CreateAsync(new RunnerDraft(name, "Linux", "test", null, 1, false));
        var settings = factory.Services.GetRequiredService<ISettingsRepository>();
        var configuration = await settings.GetAsync();
        configuration.ExternallyIsolatedRunners = name;
        await settings.SaveAsync(configuration);
        var job = await jobs.CreateJobAsync(new JobDraft(
            $"輸出 {Guid.NewGuid():N}", null, null, null, "main", name, null,
            "echo out", 30, null, TriggerMode.Manual, null, true, false, ScriptModes.Source, null));
        var build = await jobs.QueueBuildAsync(job.Id, "test", name);
        var client = factory.CreateClient();

        using var claimRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/runners/{name}/claim");
        claimRequest.Headers.Add("X-Walis-Runner-Protocol", "2");
        claimRequest.Headers.Add("X-Walis-Runner-Isolation", "external");
        var claimed = await client.SendAsync(claimRequest);
        claimed.EnsureSuccessStatusCode();
        var stepId = (await claimed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("steps")[0].GetProperty("id").GetString();

        var reported = await client.PostAsJsonAsync(
            $"/api/builds/{build.Id}/steps/{stepId}/result",
            new { status = "Succeeded", exitCode = 0, log = "ok", outputs = new Dictionary<string, string?> { ["version"] = "9", ["bad name"] = "x", ["empty"] = null } });
        Assert.Equal(HttpStatusCode.NoContent, reported.StatusCode);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/builds/{build.Id}");
        var outputs = detail.GetProperty("steps")[0].GetProperty("outputs");
        Assert.Equal("9", outputs.GetProperty("version").GetString());
        Assert.Equal("", outputs.GetProperty("empty").GetString());
        Assert.False(outputs.TryGetProperty("bad name", out _));
        Assert.False(detail.GetProperty("steps")[0].TryGetProperty("outputsJson", out _));
    }
}
