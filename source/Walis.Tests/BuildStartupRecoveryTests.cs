using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Walis.Api.Services;
using Walis.Core.Database;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

public class BuildStartupRecoveryTests
{
    [Fact]
    public async Task StartupWaitsForRecoveryBeforeAcceptingNewBuilds()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var root = Directory.CreateTempSubdirectory("walis-recovery-");
        var jobs = DispatchProxy.Create<IJobRepository, RecoveryGate>();
        var gate = (RecoveryGate)(object)jobs;
        gate.Inner = database.Jobs;
        var job = await database.Jobs.CreateJobAsync(new JobDraft(
            "startup recovery", null, null, null, "main", "remote", null, "echo ok", 5, null,
            TriggerMode.Manual, null, true, false, ScriptModes.Source, null));
        var interrupted = await database.Jobs.QueueBuildAsync(job.Id, "test", "remote");
        Assert.True(await database.Jobs.MarkBuildRunningAsync(interrupted.Id, "remote"));

        // Only startup/recovery runs here; no build is queued on the local execution channel.
        using var worker = new BuildExecutionWorker(new BuildQueue(), jobs, database.Variables, database.Settings,
            null!, null!, null!, new DataDirectory(root.FullName), null!, null!,
            new NodeIdentity("recovery-test", DatabaseProvider.Sqlite), NullLogger<BuildExecutionWorker>.Instance);
        var starting = worker.StartAsync(CancellationToken.None);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(starting.IsCompleted, "The host must not become ready while interrupted builds are still being recovered.");
            gate.Release.TrySetResult();
            await starting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(BuildStatus.Failed, (await database.Jobs.GetBuildAsync(interrupted.Id))!.Status);

            var accepted = await database.Jobs.QueueBuildAsync(job.Id, "test", "remote");
            Assert.True(await database.Jobs.MarkBuildRunningAsync(accepted.Id, "remote"));
            await worker.StopAsync(CancellationToken.None);
            Assert.Equal(BuildStatus.Running, (await database.Jobs.GetBuildAsync(accepted.Id))!.Status);
        }
        finally
        {
            gate.Release.TrySetResult();
            await starting;
            await worker.StopAsync(CancellationToken.None);
            root.Delete(true);
        }
    }

    // Delay just the real repository recovery call to control the startup race deterministically.
    public class RecoveryGate : DispatchProxy
    {
        public IJobRepository Inner { get; set; } = null!;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IJobRepository.ResetStuckBuildsAsync)) return Recover(method, args);
            return method.Invoke(Inner, args);
        }

        private async Task<int> Recover(MethodInfo method, object?[]? args)
        {
            Entered.TrySetResult();
            await Release.Task;
            return await (Task<int>)method.Invoke(Inner, args)!;
        }
    }
}
