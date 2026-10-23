using System.Text.Json;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Services;

public sealed class BuildExecutionWorker(
    BuildQueue queue,
    IJobRepository jobs,
    IEnvironmentVariableRepository variables,
    ISettingsRepository settings,
    BuildStepPlanner planner,
    BuildStepRunner steps,
    BuildIsolationPolicy isolation,
    DataDirectory data,
    ArtifactStore artifacts,
    BuildCompletionService completion,
    NodeIdentity node,
    ILogger<BuildExecutionWorker> logger) : BackgroundService
{
    /// <summary>The database polling interval with several nodes; a single node uses the in-memory queue and never needs it.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait before retrying after a refused claim (e.g. the maintenance lease during a restore). A single node has no polling, so without re-queueing it would sit in the queue forever.</summary>
    private static readonly TimeSpan ClaimRetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>How often a running build checks whether someone asked to cancel it.</summary>
    private static readonly TimeSpan CancelPollInterval = TimeSpan.FromSeconds(2);

    private int _running;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // In .NET 10 ExecuteAsync runs entirely in the background. Recover before the host accepts requests,
        // otherwise a newly claimed build can be reset as if it belonged to the previous process.
        await RecoverAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // When several nodes share a database, builds enqueued by other nodes never appear in this node's in-memory queue
        // and must be picked up from the database; a single node does not need this path.
        if (node.IsClustered) _ = Task.Run(() => PollAsync(stoppingToken), CancellationToken.None);

        await foreach (var buildId in queue.DequeueAllAsync(stoppingToken))
        {
            var context = await jobs.GetBuildContextAsync(buildId, stoppingToken);
            if (context is null || context.Build.Status != BuildStatus.Queued) continue;

            // Builds assigned to a remote runner stay in the queue waiting for that runner to claim them.
            if (!string.IsNullOrWhiteSpace(context.Build.RunnerName) &&
                context.Build.RunnerName != Runner.LocalName)
                continue;

            await WaitForSlotAsync(stoppingToken);
            Interlocked.Increment(ref _running);

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunAsync(context, stoppingToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "建置 {BuildId} 執行失敗", buildId);
                    await jobs.CompleteBuildAsync(
                        buildId,
                        new BuildCompletion(BuildStatus.Failed, null, $"執行器例外：{exception.Message}\n"),
                        CancellationToken.None);
                }
                finally
                {
                    Interlocked.Decrement(ref _running);
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>
    ///     At service startup, marks builds interrupted on this node as failed and re-enqueues the ones still queued.
    ///     With several nodes, builds running on other nodes must not be touched.
    /// </summary>
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        // Pass null on a single node to keep the old behavior of reclaiming every interrupted build; with several nodes only reclaim our own.
        var reset = await jobs.ResetStuckBuildsAsync(
            node.IsClustered ? node.Name : null, cancellationToken);
        if (reset != 0) logger.LogWarning("已將 {Count} 筆中斷的建置標記為失敗", reset);

        ReclaimWorkspaces(await jobs.ListJobsAsync(cancellationToken));

        foreach (var buildId in await jobs.ListQueuedBuildIdsAsync(Runner.LocalName, cancellationToken))
            queue.TryEnqueue(buildId);
    }

    /// <summary>Removes managed workspaces whose job no longer exists; a failure here is logged and never blocks startup.</summary>
    private void ReclaimWorkspaces(IReadOnlyList<Job> existing)
    {
        try
        {
            var root = ManagedWorkspaces.EnsureRoot(data.WorkspacesRoot);
            var removed = ManagedWorkspaces.Reclaim(
                root,
                existing.Select(job => job.Id).ToHashSet(),
                (path, exception) => logger.LogWarning(exception, "無法回收工作目錄 {Path}", path));

            if (removed != 0) logger.LogInformation("已回收 {Count} 個沒有對應任務的工作目錄", removed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "整理受管理的工作目錄失敗");
        }
    }

    /// <summary>
    ///     With several nodes, polls the database for queued builds with no runner assigned (or assigned to the built-in runner).
    ///     The claim is one conditional UPDATE, so when two nodes pick at once only one succeeds.
    /// </summary>
    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var pending = await jobs.ListQueuedBuildIdsAsync(Runner.LocalName, cancellationToken);
                foreach (var buildId in pending) queue.TryEnqueue(buildId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "撿取排隊建置失敗");
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RequeueLaterAsync(Guid buildId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ClaimRetryInterval, cancellationToken);
            queue.TryEnqueue(buildId);
        }
        catch (OperationCanceledException)
        {
            // The service is stopping; RecoverAsync picks up the still-queued builds after the restart.
        }
    }

    private async Task WaitForSlotAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var configuration = await settings.GetAsync(cancellationToken);
            if (Volatile.Read(ref _running) < configuration.MaxConcurrentBuilds) return;

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }
    }

    private async Task RunAsync(BuildContext context, CancellationToken cancellationToken)
    {
        var (job, build) = (context.Job, context.Build);

        if (!await jobs.MarkBuildRunningAsync(
                build.Id, build.RunnerName ?? Runner.LocalName, node.Name, cancellationToken))
        {
            // Another node may have won, or the maintenance lease (restore) has not ended yet. Re-queue a bit later: builds no longer Queued
            // are skipped on dequeue, and those under maintenance start naturally once the lease expires.
            logger.LogInformation("建置 {BuildId} 暫時無法認領，{Seconds} 秒後重試", build.Id, ClaimRetryInterval.TotalSeconds);
            _ = RequeueLaterAsync(build.Id, cancellationToken);
            return;
        }

        // Actions mode must be re-planned from definitions that "still exist and are enabled now". When planning fails, stop before reading
        // secrets, preparing the workspace or starting a shell, and never fall back to the stale output in Jobs.Command.
        try
        {
            var plans = await planner.PlanAsync(job, cancellationToken);
            await jobs.CreateBuildStepsAsync(build.Id, plans, cancellationToken);
        }
        catch (BuildPlanningException exception)
        {
            logger.LogWarning(exception, "建置 {BuildId} 的動作流程規劃失敗", build.Id);
            await jobs.CompleteBuildAsync(
                build.Id,
                new BuildCompletion(BuildStatus.Failed, null, $"建置規劃失敗：{exception.Message}\n"),
                CancellationToken.None);
            return;
        }

        var configuration = await settings.GetAsync(cancellationToken);

        // Matrix combination values are injected as environment variables, so the script reads them with a plain $NAME.
        var jobVariables = new Dictionary<string, string>(
            await variables.ResolveForJobAsync(job.Id, cancellationToken),
            StringComparer.Ordinal);

        foreach (var (name, value) in ParseMatrixValues(build.MatrixValues)) jobVariables[name] = value;

        // Build number and id (Jenkins's BUILD_NUMBER maps here).
        jobVariables["WALIS_BUILD_NUMBER"] = build.Number.ToString();
        jobVariables["WALIS_BUILD_ID"] = build.Id.ToString();

        // Build parameters (including upstream info) are environment variables too; password-type values are stored separately and added to the mask list.
        foreach (var (name, value) in BuildParameters.ParseValues(build.ParametersJson)) jobVariables[name] = value;

        var secretParameters = BuildParameters.ParseValues(build.SecretParametersJson);
        foreach (var (name, value) in secretParameters) jobVariables[name] = value;

        var secretValues = (await variables.ListSecretValuesForJobAsync(job.Id, cancellationToken))
            .Concat(secretParameters.Values.Where(value => value.Length != 0))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var protectedPaths = isolation.DeniedPaths(configuration).Distinct().ToArray();
        var collectsArtifacts = configuration.KeepArtifacts &&
                                !string.IsNullOrWhiteSpace(job.ArtifactPath);
        var defaultWorkingDirectory = configuration.DefaultWorkingDirectory;
        var allowedWorkingDirectories = BuildIsolationPolicy.AllowedDirectories(configuration).ToList();
        var sandboxAllowedPaths = new List<string>();

        // An unset path means the program's own per-job workspace: a fixed, validated directory under the data directory
        // (data/workspaces/<job id>) rather than a fresh temp root per process start, so old roots never pile up in the temp
        // directory and a restart finds the same place again. Only that exact path joins this run's allowlist.
        if (string.IsNullOrWhiteSpace(job.WorkingDirectory) &&
            string.IsNullOrWhiteSpace(defaultWorkingDirectory))
        {
            var managedRoot = ManagedWorkspaces.EnsureRoot(data.WorkspacesRoot);
            defaultWorkingDirectory = ManagedWorkspaces.EnsureJobWorkspace(managedRoot, job.Id);
            allowedWorkingDirectories.Add(defaultWorkingDirectory);

            // The workspace sits inside the (protected) data directory: swap the data root for its other entries so the
            // overlap check passes for the workspace alone, and let the sandbox open exactly this workspace again.
            protectedPaths = isolation.ProtectedPathsForManagedWorkspace(configuration, managedRoot).ToArray();
            sandboxAllowedPaths.Add(defaultWorkingDirectory);
        }
        else if (string.IsNullOrWhiteSpace(job.WorkingDirectory))
        {
            // A system default working directory is a parent, not a shared workspace: each job gets its own subdirectory, so
            // one job cannot read another's checkout or leave files (a .git/hooks script) for it to run.
            defaultWorkingDirectory = Path.Combine(defaultWorkingDirectory!, job.Id.ToString("N"));

            try
            {
                Directory.CreateDirectory(defaultWorkingDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The preflight reports a workspace that cannot be used; nothing else to do here.
            }
        }
        else
        {
            // A job with its own directory must not overlap the directory of a job its run role could not modify (an
            // administrator's or a trusted job's): the directories of those jobs become protected paths for this build, so the
            // preflight refuses it. Saving such a job is refused already; this covers jobs changed since.
            var runAs = JobAccess.NormalizeRunRole(job.RunRole);
            var foreign = (await jobs.ListJobsAsync(cancellationToken))
                .Where(other => other.Id != job.Id &&
                                !string.IsNullOrWhiteSpace(other.WorkingDirectory) &&
                                !JobAccess.CanModify(runAs, other))
                .Select(other => other.WorkingDirectory!)
                .ToArray();

            if (foreign.Length != 0) protectedPaths = [.. protectedPaths, .. foreign];
        }

        var options = new BuildExecutionOptions(
            defaultWorkingDirectory,
            configuration.DefaultShell,
            jobVariables,
            secretValues,
            configuration.MaskSecrets)
        {
            // The job decides; the system setting is only the fallback for jobs that never chose.
            CleanWorkspace = job.CleanWorkspace ?? configuration.CleanWorkspace,

            // A trusted job (admin-only flag) runs outside the sandbox on purpose and is exempt from the boundary requirement;
            // the step log says so on its first line. Every other job gets its profile below, once the workspace is known.
            RequireSandbox = !job.TrustedUnsandboxed && BuildIsolationPolicy.RequiresSandbox(configuration),
            TrustedUnsandboxed = job.TrustedUnsandboxed,
            CleanWorkspaceAfterBuild = job.CleanWorkspaceAfterBuild ?? configuration.CleanWorkspaceAfterBuild,
            AllowedWorkingDirectories = allowedWorkingDirectories,
            ProtectedPaths = protectedPaths,
            RequireWorkspaceAllowlist = collectsArtifacts,
            DeadlineUtc = DateTime.UtcNow.AddMinutes(job.TimeoutMinutes),
            CachePaths = BuildCache.ParsePaths(job.CachePaths),
            CacheRoot = data.Combine("cache", job.Id.ToString("N"))
        };

        var previousStatus = await jobs.GetPreviousBuildStatusAsync(job.Id, build.Number, cancellationToken);

        IDisposable? workspaceLease = null;

        var requestedWorkingDirectory = ShellCommandExecutor.ResolveWorkingDirectory(job, options);
        options = options with
        {
            RequireWorkspaceAllowlist = options.RequireWorkspaceAllowlist ||
                                       isolation.RequiresWorkspaceAllowlist(configuration, job, requestedWorkingDirectory)
        };
        var requiresAllowlist = options.RequireWorkspaceAllowlist || options.CleanWorkspace ||
                                options.CleanWorkspaceAfterBuild ||
                                (options.CacheRoot is not null && options.CachePaths.Count != 0);

        // The profile denies the installation and the service account's home as well as the data directory, so the workspace
        // is allowed again explicitly; the preflight above it still refuses a workspace that overlaps the storage paths.
        options = options with
        {
            SandboxProfile = isolation.CreateProfile(configuration, job, requestedWorkingDirectory, sandboxAllowedPaths)
        };

        if (ShellCommandExecutor.TryPreflight(
                requestedWorkingDirectory,
                options,
                requiresAllowlist,
                out var lockPath,
                out _))
        {
            workspaceLease = await BuildStepSequence.AcquireWorkspaceAsync(lockPath, cancellationToken);
            options = options with
            {
                WorkspaceLockAlreadyHeld = true,
                LockedWorkingDirectory = lockPath
            };
        }

        BuildExecutionResult execution;
        BuildCompletion outcome;

        // POST /cancel on a running build only leaves a request in the row; this is where it turns into a cancelled token,
        // which the executor answers by killing the step's process group.
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancelRequested = false;
        using var pollStop = new CancellationTokenSource();
        var polling = PollCancelAsync(build.Id, () =>
        {
            cancelRequested = true;
            cancel.Cancel();
        }, pollStop.Token);

        try
        {
            try
            {
                execution = await steps.RunAsync(job, build, options, cancel.Token);
                outcome = execution.Completion;
            }
            catch (OperationCanceledException) when (cancelRequested && !cancellationToken.IsCancellationRequested)
            {
                execution = new BuildExecutionResult(
                    new BuildCompletion(BuildStatus.Failed, null, string.Empty),
                    requestedWorkingDirectory);
                outcome = execution.Completion;
            }

            if (cancelRequested && !cancellationToken.IsCancellationRequested)
                outcome = outcome with
                {
                    Status = BuildStatus.Canceled,
                    ExitCode = null,
                    Log = $"{outcome.Log.TrimEnd()}{Environment.NewLine}{Environment.NewLine}建置已依要求取消。{Environment.NewLine}"
                };

            if (collectsArtifacts && outcome.Status == BuildStatus.Succeeded)
                try
                {
                    var collected = artifacts.Collect(
                        build.Id,
                        execution.WorkingDirectory,
                        job.ArtifactPath,
                        out var report,
                        protectedPaths);
                    await jobs.AddArtifactsAsync(build.Id, collected, CancellationToken.None);
                    outcome = outcome with { Log = $"{outcome.Log}{Environment.NewLine}{report}{Environment.NewLine}" };
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "保存建置 {BuildId} 的產出檔案失敗", build.Id);
                    outcome = outcome with
                    {
                        Log = $"{outcome.Log}{Environment.NewLine}保存產出檔案失敗：{exception.Message}{Environment.NewLine}"
                    };
                }

            // After artifacts and cache: whatever the build left behind (sources, tokens, password parameters in files) goes.
            if (options.CleanWorkspaceAfterBuild)
                outcome = outcome with
                {
                    Log = $"{outcome.Log}{Environment.NewLine}{BuildStepSequence.CleanAfterBuild(execution.WorkingDirectory, options)}{Environment.NewLine}"
                };
        }
        finally
        {
            await pollStop.CancelAsync();
            await polling;
            workspaceLease?.Dispose();
        }

        // An admin may have reported the result through /result first; then the wrap-up (notifications, downstream) is theirs.
        if (await jobs.CompleteBuildAsync(build.Id, outcome, CancellationToken.None) == 0)
        {
            logger.LogInformation("建置 {BuildId} 的結果已由其他來源寫入，略過通知與下游觸發", build.Id);
            return;
        }

        var finished = await jobs.GetBuildAsync(build.Id, CancellationToken.None);
        if (finished is not null)
            await completion.HandleAsync(job, finished, previousStatus, outcome.TimedOut, CancellationToken.None);
    }

    private async Task PollCancelAsync(Guid buildId, Action onCancelRequested, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CancelPollInterval, stop);

                if (!await jobs.IsCancelRequestedAsync(buildId, stop)) continue;

                onCancelRequested();
                return;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A transient database error must not stop the build; the next poll tries again.
                logger.LogWarning(exception, "檢查建置 {BuildId} 的取消要求失敗", buildId);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ParseMatrixValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

}
