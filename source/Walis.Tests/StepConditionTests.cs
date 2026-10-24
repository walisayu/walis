using Walis.Core.Actions;
using Walis.Core.Execution;
using Walis.Core.Models;

namespace Walis.Tests;

public class StepConditionTests
{
    private static bool Eval(string text, params (string Key, string Value)[] variables)
    {
        var parsed = StepCondition.Parse(text);
        Assert.True(parsed.IsValid, parsed.Error);
        return StepCondition.Evaluate(parsed.Root!, variables.ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    [Fact]
    public void ComparisonLogicAndFunctions()
    {
        var vars = new[] { ("WALIS_STEP_check_changed", "true"), ("BRANCH", "release/1.2"), ("WALIS_STEP_api_status", "404") };

        Assert.True(Eval("steps.check.changed == 'true'", vars));
        Assert.False(Eval("steps.check.changed != \"true\"", vars));
        Assert.True(Eval("steps.check.changed && env.BRANCH != 'main'", vars));
        Assert.True(Eval("!(steps.api.status == '200') || empty(env.NOPE)", vars));
        Assert.True(Eval("startsWith(env.BRANCH, 'release/') && contains(env.BRANCH, '1.2') && endsWith(env.BRANCH, '.2')", vars));
        Assert.True(Eval("matches(steps.api.status, '^4[0-9][0-9]$')", vars));
        Assert.False(Eval("steps.missing.value", vars));
        Assert.True(Eval("empty(steps.missing.value)", vars));
        Assert.True(Eval("'it\\'s' == 'it\\'s'"));
    }

    [Theory]
    [InlineData("steps.check == 'x'", "steps.<識別字>.<名稱>")]
    [InlineData("foo == 'x'", "看不懂")]
    [InlineData("'open", "引號")]
    [InlineData("(steps.a.b == 'x'", "右括號")]
    [InlineData("contains(env.A)", "需要 2 個參數")]
    [InlineData("env.A == 'x' extra", "多餘")]
    [InlineData("", "空的")]
    public void SyntaxErrorsHaveClearMessages(string text, string expected)
    {
        var parsed = StepCondition.Parse(text);
        Assert.False(parsed.IsValid);
        Assert.Contains(expected, parsed.Error);
    }

    [Fact]
    public void StepReferencesInConditionsMustPointToPredecessors()
    {
        var definitions = BuiltInActions.All.ToDictionary(definition => definition.Id);
        ActionStep Step(string id, string? key, string? condition, IReadOnlyList<string>? dependsOn) => new()
        {
            Id = id, ActionId = "shell.run", Key = key, Condition = condition, DependsOn = dependsOn,
            Parameters = new Dictionary<string, string?> { ["command"] = "true" }
        };

        var ok = ActionScriptCompiler.Plan([Step("a", "check", null, []), Step("b", null, "steps.check.changed == 'true'", ["a"])], definitions);
        Assert.True(ok.IsValid, string.Join(";", ok.Errors));
        Assert.Equal("steps.check.changed == 'true'", ok.Steps[1].Condition);

        var sibling = ActionScriptCompiler.Plan([Step("a", "check", null, []), Step("b", null, "steps.check.changed", [])], definitions);
        Assert.Contains(sibling.Errors, error => error.Contains("條件引用了 steps.check.changed"));

        var broken = ActionScriptCompiler.Plan([Step("a", null, "env.X ==", [])], definitions);
        Assert.Contains(broken.Errors, error => error.Contains("條件有錯"));
    }

    [Fact]
    public async Task StepWithFalseRuntimeConditionIsSkippedNotFailedAndDownstreamContinues()
    {
        if (OperatingSystem.IsWindows()) return;

        var directory = Directory.CreateTempSubdirectory("walis-cond");
        var job = new Job { Id = Guid.NewGuid(), Name = "條件", Command = "true", TimeoutMinutes = 1, WorkingDirectory = directory.FullName };
        var options = new BuildExecutionOptions(directory.FullName, null, new Dictionary<string, string> { ["BRANCH"] = "main" }, [], false);
        BuildStep[] steps =
        [
            new() { Id = Guid.NewGuid(), Number = 1, Name = "check", StepKey = "check", Command = "echo changed=false >> \"$WALIS_OUTPUT_FILE\"" },
            new() { Id = Guid.NewGuid(), Number = 2, Name = "deploy", Command = "echo deploying", DependsOn = [1], Condition = "steps.check.changed == 'true'" },
            new() { Id = Guid.NewGuid(), Number = 3, Name = "main-only", Command = "echo main", DependsOn = [1], Condition = "env.BRANCH == 'main'" },
            new() { Id = Guid.NewGuid(), Number = 4, Name = "after", Command = "echo after", DependsOn = [2, 3] },
            new() { Id = Guid.NewGuid(), Number = 5, Name = "broken", Command = "echo never", DependsOn = [3], Condition = "env.X ==" }
        ];
        var sink = new RecordingSink();

        var result = await new BuildStepSequence(new ShellCommandExecutor()).RunAsync(job, Guid.NewGuid(), steps, options, sink, CancellationToken.None);

        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[1]);
        Assert.Equal(BuildStepStatus.Skipped, sink.Statuses[2]);
        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[3]);
        Assert.Equal(BuildStepStatus.Skipped, sink.Statuses[4]); // A dependency was skipped by its condition, the success condition fails -> skipped but not a failure
        Assert.Equal(BuildStepStatus.Failed, sink.Statuses[5]); // Invalid condition syntax -> this step fails
        Assert.Equal(BuildStatus.Failed, result.Completion.Status);
        Assert.Contains("條件不成立", result.Completion.Log);
    }

    private sealed class RecordingSink : IBuildStepSink
    {
        public Dictionary<int, string> Statuses { get; } = [];
        public Task MarkRunningAsync(BuildStep step, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportStepLogAsync(BuildStep step, string log, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CompleteStepAsync(BuildStep step, string status, int? exitCode, string log, IReadOnlyDictionary<string, string> outputs, CancellationToken cancellationToken)
        {
            Statuses[step.Number] = status;
            return Task.CompletedTask;
        }
        public Task SkipRemainingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportBuildLogAsync(string log, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public class DataActionTests
{
    [Fact]
    public async Task HttpAndJsonActionsChainToExtractFieldForCondition()
    {
        if (OperatingSystem.IsWindows()) return;

        var definitions = BuiltInActions.All.ToDictionary(definition => definition.Id);
        Assert.Contains(definitions["http.request"].Outputs, output => output.Name == "body");
        Assert.Contains(definitions["data.json-get"].Outputs, output => output.Name == "value");

        // No real network: shell.outputs fakes the JSON an API would return, data.json-get extracts a field, and the condition branches on it.
        var plan = ActionScriptCompiler.Plan(
        [
            new ActionStep { Id = "api", ActionId = "shell.outputs", Key = "api", DependsOn = [], Parameters = new() { ["outputs"] = "status=200\nbody={\"data\":{\"items\":[{\"name\":\"walis\"}]}}" } },
            new ActionStep { Id = "pick", ActionId = "data.json-get", Key = "pick", DependsOn = ["api"], Parameters = new() { ["json"] = "${{ steps.api.body }}", ["path"] = "data.items.0.name" } },
            new ActionStep { Id = "yes", ActionId = "shell.run", DependsOn = ["pick"], Condition = "steps.pick.value == 'walis'", Parameters = new() { ["command"] = "echo matched" } },
            new ActionStep { Id = "no", ActionId = "shell.run", DependsOn = ["pick"], Condition = "steps.pick.value != 'walis'", Parameters = new() { ["command"] = "echo other" } }
        ], definitions);
        Assert.True(plan.IsValid, string.Join(";", plan.Errors));

        var directory = Directory.CreateTempSubdirectory("walis-data");
        var job = new Job { Id = Guid.NewGuid(), Name = "資料", Command = "true", TimeoutMinutes = 1, WorkingDirectory = directory.FullName };
        var options = new BuildExecutionOptions(directory.FullName, null, new Dictionary<string, string>(), [], false);
        var steps = plan.Steps.Select((step, index) => new BuildStep
        {
            Id = Guid.NewGuid(), Number = index + 1, Name = step.Name, Command = step.Command, StepKey = step.Key,
            DependsOn = step.DependsOn ?? [], Condition = step.Condition, RunWhen = step.RunWhen
        }).ToList();
        var sink = new RecordingSink();

        var result = await new BuildStepSequence(new ShellCommandExecutor()).RunAsync(job, Guid.NewGuid(), steps, options, sink, CancellationToken.None);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.Equal("walis", sink.Outputs[2]["value"]);
        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[3]);
        Assert.Equal(BuildStepStatus.Skipped, sink.Statuses[4]);
    }

    private sealed class RecordingSink : IBuildStepSink
    {
        public Dictionary<int, string> Statuses { get; } = [];
        public Dictionary<int, IReadOnlyDictionary<string, string>> Outputs { get; } = [];
        public Task MarkRunningAsync(BuildStep step, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportStepLogAsync(BuildStep step, string log, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CompleteStepAsync(BuildStep step, string status, int? exitCode, string log, IReadOnlyDictionary<string, string> outputs, CancellationToken cancellationToken)
        {
            Statuses[step.Number] = status;
            Outputs[step.Number] = outputs;
            return Task.CompletedTask;
        }
        public Task SkipRemainingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportBuildLogAsync(string log, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
