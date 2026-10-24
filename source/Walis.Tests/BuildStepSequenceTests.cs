using Walis.Core.Execution;
using Walis.Core.Models;

namespace Walis.Tests;

public class BuildStepSequenceTests
{
    private static BuildStep Step(int number, string command, string runWhen = StepRunCondition.Success,
        int retries = 0, bool continueOnError = false)
    {
        return new BuildStep
        {
            Id = Guid.NewGuid(),
            Number = number,
            Name = $"步驟 {number}",
            Command = command,
            RunWhen = runWhen,
            Retries = retries,
            ContinueOnError = continueOnError
        };
    }

    private static async Task<(BuildExecutionResult Result, RecordingSink Sink)> RunAsync(params BuildStep[] steps)
    {
        var directory = Directory.CreateTempSubdirectory("walis-seq");
        var job = new Job
        {
            Id = Guid.NewGuid(), Name = "序列測試", Command = "true", TimeoutMinutes = 1,
            WorkingDirectory = directory.FullName
        };
        var options = new BuildExecutionOptions(directory.FullName, null, new Dictionary<string, string>(), [], false);
        var sink = new RecordingSink();
        var result = await new BuildStepSequence(new ShellCommandExecutor()).RunAsync(job, Guid.NewGuid(), steps,
            options, sink, CancellationToken.None);
        return (result, sink);
    }

    [Fact]
    public async Task AfterFailureOnlyAlwaysAndFailureStepsRun()
    {
        if (OperatingSystem.IsWindows()) return;

        var (result, sink) = await RunAsync(
            Step(1, "echo 開始"),
            Step(2, "echo 壞掉; exit 3"),
            Step(3, "echo 不該跑"),
            Step(4, "echo 善後", StepRunCondition.Failure),
            Step(5, "echo 一定跑", StepRunCondition.Always));

        Assert.Equal(BuildStatus.Failed, result.Completion.Status);
        Assert.Equal(3, result.Completion.ExitCode);
        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[1]);
        Assert.Equal(BuildStepStatus.Failed, sink.Statuses[2]);
        Assert.Equal(BuildStepStatus.Skipped, sink.Statuses[3]);
        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[4]);
        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[5]);
        Assert.Contains("善後", sink.Logs[4]);
        Assert.Contains("只在成功時執行，已略過", result.Completion.Log);
    }

    [Fact]
    public async Task FailureOnlyStepsAreSkippedWhenEverythingSucceeds()
    {
        if (OperatingSystem.IsWindows()) return;

        var (result, sink) = await RunAsync(
            Step(1, "echo ok"),
            Step(2, "echo 不該跑", StepRunCondition.Failure),
            Step(3, "echo 收尾", StepRunCondition.Always));

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.Equal(BuildStepStatus.Skipped, sink.Statuses[2]);
        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[3]);
    }

    [Fact]
    public async Task RetryStartsNewShellUntilSuccess()
    {
        if (OperatingSystem.IsWindows()) return;

        var flag = Path.Combine(Path.GetTempPath(), $"walis-retry-{Guid.NewGuid():N}");
        var (result, sink) = await RunAsync(
            Step(1, $"if [ -f '{flag}' ]; then echo 第二次成功; else touch '{flag}'; echo 第一次失敗; exit 1; fi", retries: 2),
            Step(2, "echo 後續"));

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[1]);
        Assert.Contains("第一次失敗", sink.Logs[1]);
        Assert.Contains("第 1 次重試", sink.Logs[1]);
        Assert.Contains("第二次成功", sink.Logs[1]);
        Assert.Equal(BuildStepStatus.Succeeded, sink.Statuses[2]);
    }

    [Fact]
    public async Task StillFailingAfterRetriesCountsAsFailure()
    {
        if (OperatingSystem.IsWindows()) return;

        var (result, sink) = await RunAsync(Step(1, "exit 2", retries: 1), Step(2, "echo 不該跑"));

        Assert.Equal(BuildStatus.Failed, result.Completion.Status);
        Assert.Equal(BuildStepStatus.Failed, sink.Statuses[1]);
        Assert.Contains("第 1 次重試，最多 1 次", sink.Logs[1]);
        Assert.Equal(BuildStepStatus.Skipped, sink.Statuses[2]);
    }

    private sealed class RecordingSink : IBuildStepSink
    {
        public Dictionary<int, string> Statuses { get; } = [];
        public Dictionary<int, string> Logs { get; } = [];
        public Dictionary<int, IReadOnlyDictionary<string, string>> Outputs { get; } = [];

        public Task MarkRunningAsync(BuildStep step, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ReportStepLogAsync(BuildStep step, string log, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task CompleteStepAsync(BuildStep step, string status, int? exitCode, string log,
            IReadOnlyDictionary<string, string> outputs, CancellationToken cancellationToken)
        {
            Statuses[step.Number] = status;
            Logs[step.Number] = log;
            Outputs[step.Number] = outputs;
            return Task.CompletedTask;
        }

        public Task SkipRemainingAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ReportBuildLogAsync(string log, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}