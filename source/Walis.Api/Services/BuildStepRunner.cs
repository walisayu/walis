using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Services;

/// <summary>Step execution for local builds: writes the reports of <see cref="BuildStepSequence" /> to the database.</summary>
public sealed class BuildStepRunner(IJobRepository jobs, ShellCommandExecutor executor)
{
    public async Task<BuildExecutionResult> RunAsync(
        Job job,
        Build build,
        BuildExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var steps = await jobs.ListBuildStepsAsync(build.Id, cancellationToken);
        var sink = new RepositorySink(jobs, build.Id);

        return await new BuildStepSequence(executor)
            .RunAsync(job, build.Id, steps, options, sink, cancellationToken);
    }

    private sealed class RepositorySink(IJobRepository jobs, Guid buildId) : IBuildStepSink
    {
        public Task MarkRunningAsync(BuildStep step, CancellationToken cancellationToken)
        {
            return jobs.MarkBuildStepRunningAsync(step.Id, cancellationToken);
        }

        public Task ReportStepLogAsync(BuildStep step, string log, CancellationToken cancellationToken)
        {
            return jobs.UpdateBuildStepLogAsync(step.Id, log, cancellationToken);
        }

        public Task CompleteStepAsync(
            BuildStep step,
            string status,
            int? exitCode,
            string log,
            IReadOnlyDictionary<string, string> outputs,
            CancellationToken cancellationToken)
        {
            var outputsJson = outputs.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(outputs);
            return jobs.CompleteBuildStepAsync(step.Id, status, exitCode, log, outputsJson, cancellationToken);
        }

        public Task SkipRemainingAsync(CancellationToken cancellationToken)
        {
            return jobs.SkipPendingBuildStepsAsync(buildId, cancellationToken);
        }

        public Task ReportBuildLogAsync(string log, CancellationToken cancellationToken)
        {
            return jobs.UpdateBuildLogAsync(buildId, log, cancellationToken);
        }
    }
}