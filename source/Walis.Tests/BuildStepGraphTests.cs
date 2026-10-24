using Walis.Core.Actions;
using Walis.Core.Execution;
using Walis.Core.Models;

namespace Walis.Tests;

public class BuildStepGraphTests
{
    [Fact]
    public void DependenciesDerivedFromOrderAndParallelMatchOldGroupSemantics()
    {
        // 1 | 2,3 parallel | 4 | 5,6 parallel
        var derived = BuildStepGraph.DeriveLinear([false, false, true, false, false, true]);

        Assert.Equal([], derived[0]);
        Assert.Equal([1], derived[1]);
        Assert.Equal([1], derived[2]);
        Assert.Equal([2, 3], derived[3]);
        Assert.Equal([4], derived[4]);
        Assert.Equal([4], derived[5]);
    }

    [Fact]
    public void CyclesAndMissingDependenciesAreRejected()
    {
        BuildStep[] cyclic =
        [
            new() { Id = Guid.NewGuid(), Number = 1, Name = "a", Command = "true", DependsOn = [2] },
            new() { Id = Guid.NewGuid(), Number = 2, Name = "b", Command = "true", DependsOn = [1] }
        ];
        BuildStepGraph.Resolve(cyclic, out var cycle);
        Assert.Contains("循環", cycle);

        BuildStep[] dangling = [new() { Id = Guid.NewGuid(), Number = 1, Name = "a", Command = "true", DependsOn = [7] }];
        BuildStepGraph.Resolve(dangling, out var missing);
        Assert.Contains("不存在", missing);
    }

    [Fact]
    public async Task DiamondOneSideFailsOtherRunsJoinSkippedFailureAndAlwaysRun()
    {
        if (OperatingSystem.IsWindows()) return;

        var directory = Directory.CreateTempSubdirectory("walis-dag");
        var job = new Job { Id = Guid.NewGuid(), Name = "圖", Command = "true", TimeoutMinutes = 1, WorkingDirectory = directory.FullName };
        var options = new BuildExecutionOptions(directory.FullName, null, new Dictionary<string, string>(), [], false);
        BuildStep[] steps =
        [
            new() { Id = Guid.NewGuid(), Number = 1, Name = "prepare", StepKey = "prepare", Command = "echo v=7 >> \"$WALIS_OUTPUT_FILE\"; echo prepare" },
            new() { Id = Guid.NewGuid(), Number = 2, Name = "left", Command = "sleep 0.3; echo left-fails; exit 5", DependsOn = [1] },
            new() { Id = Guid.NewGuid(), Number = 3, Name = "right", Command = "echo right v=$WALIS_STEP_prepare_v", DependsOn = [1] },
            new() { Id = Guid.NewGuid(), Number = 4, Name = "join", Command = "echo join", DependsOn = [2, 3] },
            new() { Id = Guid.NewGuid(), Number = 5, Name = "after-right", Command = "echo after-right", DependsOn = [3] },
            new() { Id = Guid.NewGuid(), Number = 6, Name = "on-failure", Command = "echo cleanup", RunWhen = StepRunCondition.Failure, DependsOn = [4] },
            new() { Id = Guid.NewGuid(), Number = 7, Name = "always", Command = "echo notify", RunWhen = StepRunCondition.Always, DependsOn = [4] }
        ];
        var sink = new RecordingSink();

        var result = await new BuildStepSequence(new ShellCommandExecutor()).RunAsync(job, Guid.NewGuid(), steps, options, sink, CancellationToken.None);

        Assert.Equal(BuildStatus.Failed, result.Completion.Status);
        Assert.Equal(5, result.Completion.ExitCode);
        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[1]);
        Assert.Equal(BuildStepStatus.Failed, sink.Statuses[2]);
        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[3]);
        Assert.Contains("right v=7", sink.Logs[3]);
        Assert.Equal(BuildStepStatus.Skipped, sink.Statuses[4]);
        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[5]); // The branch that depends only on right is unaffected by left
        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[6]); // The failure condition sees the upstream failure
        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[7]);
        Assert.Contains("與其他步驟平行執行", result.Completion.Log);
    }

    [Fact]
    public async Task StepsWithoutDependenciesStartTogetherUpToLimit()
    {
        if (OperatingSystem.IsWindows()) return;

        var directory = Directory.CreateTempSubdirectory("walis-dag");
        var job = new Job { Id = Guid.NewGuid(), Name = "並行", Command = "true", TimeoutMinutes = 1, WorkingDirectory = directory.FullName };
        var options = new BuildExecutionOptions(directory.FullName, null, new Dictionary<string, string>(), [], false);
        var steps = Enumerable.Range(1, 4).Select(number => new BuildStep
        {
            Id = Guid.NewGuid(), Number = number, Name = $"p{number}", Command = "sleep 0.4; echo done",
            DependsOn = number == 4 ? [1, 2, 3] : []
        }).ToList();
        // Step 4 has a dependency, the other three are entry points -> the graph is explicit
        var sink = new RecordingSink();
        var started = DateTime.UtcNow;

        var result = await new BuildStepSequence(new ShellCommandExecutor()).RunAsync(job, Guid.NewGuid(), steps, options, sink, CancellationToken.None);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.True((DateTime.UtcNow - started) < TimeSpan.FromSeconds(1.6), "三個起點應同時跑，總時間約兩輪 sleep");
        Assert.All(Enumerable.Range(1, 4), number => Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[number]));
    }

    private sealed class RecordingSink : IBuildStepSink
    {
        public Dictionary<int, string> Statuses { get; } = [];
        public Dictionary<int, string> Logs { get; } = [];

        public Task MarkRunningAsync(BuildStep step, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportStepLogAsync(BuildStep step, string log, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteStepAsync(BuildStep step, string status, int? exitCode, string log,
            IReadOnlyDictionary<string, string> outputs, CancellationToken cancellationToken)
        {
            Statuses[step.Number] = status;
            Logs[step.Number] = log;
            return Task.CompletedTask;
        }

        public Task SkipRemainingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportBuildLogAsync(string log, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public class StepDependencyCompileTests
{
    private static readonly Dictionary<string, ActionDefinition> Definitions = BuiltInActions.All.ToDictionary(definition => definition.Id);

    private static ActionStep Step(string id, string command, string? key = null, IReadOnlyList<string>? dependsOn = null, bool enabled = true)
    {
        return new ActionStep
        {
            Id = id, ActionId = "shell.run", Key = key, DependsOn = dependsOn, Enabled = enabled,
            Parameters = new Dictionary<string, string?> { ["command"] = command }
        };
    }

    [Fact]
    public void ExplicitDependenciesBecomeStepNumbersInPlanAndDisabledOnesAreFollowedUpstream()
    {
        var result = ActionScriptCompiler.Plan(
        [
            Step("a", "echo a", key: "a", dependsOn: []),
            Step("b", "echo b", dependsOn: ["a"], enabled: false),
            Step("c", "echo ${{ steps.a.x }}", dependsOn: ["b"]),
            Step("d", "echo d", dependsOn: ["a", "c"])
        ], Definitions);

        Assert.True(result.IsValid, string.Join(";", result.Errors));
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal([], result.Steps[0].DependsOn);
        Assert.Equal([1], result.Steps[1].DependsOn); // c depends on the disabled b -> now depends on a
        Assert.Equal([1, 2], result.Steps[2].DependsOn);
    }

    [Fact]
    public void ReferencesOnlyToPredecessorsAndCyclesOrMissingDependenciesAreErrors()
    {
        var sibling = ActionScriptCompiler.Plan(
        [
            Step("a", "echo a", key: "a", dependsOn: []),
            Step("b", "echo ${{ steps.a.x }}", dependsOn: []) // Parallel with a, not a predecessor
        ], Definitions);
        Assert.Contains(sibling.Errors, error => error.Contains("steps.a.x"));

        var cycle = ActionScriptCompiler.Plan([Step("a", "echo a", dependsOn: ["b"]), Step("b", "echo b", dependsOn: ["a"])], Definitions);
        Assert.Contains(cycle.Errors, error => error.Contains("循環"));

        var missing = ActionScriptCompiler.Plan([Step("a", "echo a", dependsOn: ["zzz"])], Definitions);
        Assert.Contains(missing.Errors, error => error.Contains("不存在"));
    }

    [Fact]
    public void SingleScriptFollowsTopologicalOrderAndNotesDependencies()
    {
        var compiled = ActionScriptCompiler.Compile(
        [
            Step("late", "echo late", dependsOn: ["early"]),
            Step("early", "echo early", dependsOn: [])
        ], Definitions);

        Assert.True(compiled.IsValid);
        Assert.True(compiled.Script.IndexOf("echo early", StringComparison.Ordinal) < compiled.Script.IndexOf("echo late", StringComparison.Ordinal));
        Assert.Contains("# 依賴：", compiled.Script);
    }
}
