using System.Collections.Concurrent;
using System.Security;
using System.Text;
using Walis.Core.Actions;
using Walis.Core.Models;

namespace Walis.Core.Execution;

/// <summary>
///     The sink that step execution reports to. The server side writes to the database, the remote runner sends HTTP instead;
///     both share the same loop so the behavior cannot drift apart.
/// </summary>
public interface IBuildStepSink
{
    Task MarkRunningAsync(BuildStep step, CancellationToken cancellationToken);

    /// <summary>Live output while running.</summary>
    Task ReportStepLogAsync(BuildStep step, string log, CancellationToken cancellationToken);

    /// <param name="outputs">The outputs the step wrote to <c>$WALIS_OUTPUT_FILE</c>, with secrets already masked.</param>
    Task CompleteStepAsync(
        BuildStep step,
        string status,
        int? exitCode,
        string log,
        IReadOnlyDictionary<string, string> outputs,
        CancellationToken cancellationToken);

    /// <summary>Marks the steps that never started as skipped after an earlier step failed.</summary>
    Task SkipRemainingAsync(CancellationToken cancellationToken);

    /// <summary>The aggregated output of the whole build; a remote runner may leave this unimplemented.</summary>
    Task ReportBuildLogAsync(string log, CancellationToken cancellationToken);
}

/// <summary>
///     Runs every step of the build in order.
///     Each step is an independent shell: the workspace is shared but environment variables do not carry over automatically, matching the semantics of other CI systems.
///     To pass a value to later steps, append <c>KEY=VALUE</c> to <c>$WALIS_ENV_FILE</c>;
///     to make it "this step's output", referenced later via <c>${{ steps.x.y }}</c> and kept in the build record, write it to <c>$WALIS_OUTPUT_FILE</c>.
/// </summary>
public sealed class BuildStepSequence(ShellCommandExecutor executor)
{
    private const int MaximumAggregateLength = 200_000;

    // Using case-insensitive keys is the conservative choice: Windows and common APFS volumes are case-insensitive;
    // on a case-sensitive volume this at worst serializes two distinct directories and never misses mutual exclusion on the same one.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WorkspaceLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<BuildExecutionResult> RunAsync(
        Job job,
        Guid buildId,
        IReadOnlyList<BuildStep> steps,
        BuildExecutionOptions options,
        IBuildStepSink sink,
        CancellationToken cancellationToken)
    {
        if (steps.Count == 0) return await executor.ExecuteAsync(job, options, cancellationToken);

        var deadline = options.DeadlineUtc ?? DateTime.UtcNow.AddMinutes(job.TimeoutMinutes);
        var variables = new Dictionary<string, string>(options.Variables, StringComparer.Ordinal);
        var completed = new BoundedAggregateLog(MaximumAggregateLength);
        var requestedWorkingDirectory = ShellCommandExecutor.ResolveWorkingDirectory(job, options);
        var hasCache = options.CacheRoot is not null && options.CachePaths.Count != 0;
        var hasPrivilegedWorkspaceIo = options.RequireWorkspaceAllowlist || options.CleanWorkspace ||
                                       options.CleanWorkspaceAfterBuild || hasCache;

        // Cleanup and caching run in the host process, outside the command sandbox. The allowlist, realpath, link and
        // sandbox availability checks must all pass before any such I/O.
        if (!ShellCommandExecutor.TryPreflight(
                requestedWorkingDirectory,
                options,
                hasPrivilegedWorkspaceIo,
                out var workingDirectory,
                out var preflightFailure))
        {
            await sink.SkipRemainingAsync(CancellationToken.None);
            return new BuildExecutionResult(
                new BuildCompletion(BuildStatus.Failed, null, $"建置前安全檢查失敗：{preflightFailure}\n"),
                requestedWorkingDirectory);
        }

        // Within one process, two builds must never run a script while another cleans/restores/saves the same workspace.
        // This also narrows the race window between the realpath check and the actual I/O that another Walis build could exploit.
        using var workspaceLease = options.WorkspaceLockAlreadyHeld
            ? null
            : await AcquireWorkspaceAsync(workingDirectory, cancellationToken);

        // While waiting for the same workspace, the previous build or an external process may change the pathname; after acquiring the lease
        // and before any clean/cache I/O it must be re-resolved. A timed-out job must not clean the directory before declaring failure either.
        if (!ShellCommandExecutor.TryPreflight(
                requestedWorkingDirectory,
                options,
                hasPrivilegedWorkspaceIo,
                out workingDirectory,
                out preflightFailure))
        {
            await sink.SkipRemainingAsync(CancellationToken.None);
            return new BuildExecutionResult(
                new BuildCompletion(BuildStatus.Failed, null, $"建置前安全檢查失敗：{preflightFailure}\n"),
                requestedWorkingDirectory);
        }

        if (options.WorkspaceLockAlreadyHeld &&
            (string.IsNullOrWhiteSpace(options.LockedWorkingDirectory) ||
             !BuildIsolation.PathsEqual(workingDirectory, options.LockedWorkingDirectory)))
        {
            await sink.SkipRemainingAsync(CancellationToken.None);
            return new BuildExecutionResult(
                new BuildCompletion(
                    BuildStatus.Failed,
                    null,
                    "建置前安全檢查失敗：等待 workspace lease 期間工作目錄的實體路徑已改變。\n"),
                requestedWorkingDirectory);
        }

        if (deadline <= DateTime.UtcNow)
        {
            await sink.SkipRemainingAsync(CancellationToken.None);
            return new BuildExecutionResult(
                new BuildCompletion(
                    BuildStatus.Failed,
                    null,
                    $"已超過任務設定的 {job.TimeoutMinutes} 分鐘逾時時間，沒有執行清理、快取或步驟。\n")
                {
                    TimedOut = true
                },
                workingDirectory);
        }

        // Cleaning and caching belong to the whole build, not to a single step; the order matters too:
        // clean first, then restore, otherwise the freshly restored cache would be wiped.
        if (options.CleanWorkspace && Directory.Exists(workingDirectory))
            completed.AppendLine(WorkspaceCleaner.Clean(workingDirectory, options.AllowedWorkingDirectories))
                .AppendLine();

        if (hasCache && Directory.Exists(workingDirectory))
        {
            var restored = BuildCache.Restore(
                options.CacheRoot!,
                workingDirectory,
                options.CachePaths,
                cancellationToken);
            completed.AppendLine($"還原快取：{restored} / {options.CachePaths.Count} 個路徑").AppendLine();
        }

        // The step files live in a private directory of this build inside the workspace, never in the shared temp directory
        // (see BuildScratchDirectory); it goes away with this method, before artifacts are collected and the workspace cleaned.
        BuildScratchDirectory scratch;

        try
        {
            scratch = BuildScratchDirectory.Create(workingDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            await sink.SkipRemainingAsync(CancellationToken.None);
            return new BuildExecutionResult(
                new BuildCompletion(BuildStatus.Failed, null, $"無法建立建置暫存目錄：{exception.Message}\n"),
                workingDirectory);
        }

        using var scratchLifetime = scratch;

        var status = BuildStatus.Succeeded;
        int? exitCode = 0;
        var timedOut = false;

        // Dependency graph: old data derives it from order and parallel flags, new data follows the graph; whatever can run concurrently does.
        var dependencies = BuildStepGraph.Resolve(steps, out var graphError);
        if (graphError is not null)
        {
            await sink.SkipRemainingAsync(CancellationToken.None);
            return new BuildExecutionResult(
                new BuildCompletion(BuildStatus.Failed, null, $"步驟依賴無效：{graphError}\n"),
                workingDirectory);
        }

        var position = steps.Select((step, index) => (step.Number, index)).ToDictionary(pair => pair.Number, pair => pair.index);
        var outcomes = new Dictionary<int, StepOutcome>(); // Keyed by step number
        var running = new Dictionary<Task<StepRun>, int>();
        var activeCount = 0;
        var variablesGate = new object();

        async Task<StepRun> RunOneAsync(BuildStep step, TimeSpan remaining)
        {
            // A previous step may have run git clean -fdx in the workspace and taken the scratch directory with it.
            lock (variablesGate) scratch.EnsureExists();
            using var stepEnvFile = StepEnvironmentFile.Create(scratch.Path, "env");
            using var stepOutputFile = StepEnvironmentFile.Create(scratch.Path, "output");
            Dictionary<string, string> stepVariables;
            lock (variablesGate)
            {
                stepVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal)
                {
                    ["WALIS_ENV_FILE"] = stepEnvFile.Path,
                    [StepExpressions.OutputFileVariable] = stepOutputFile.Path
                };
            }

            await sink.MarkRunningAsync(step, cancellationToken);

            // Retry after failure: each attempt is a brand-new shell, and earlier output is kept in front so the attempt number is visible.
            var attempts = 1 + Math.Clamp(step.Retries, 0, BuildStepPlan.MaximumRetries);
            var earlier = new StringBuilder();
            BuildExecutionResult? outcome = null;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var left = deadline - DateTime.UtcNow;
                if (attempt > 1 && left <= TimeSpan.Zero) break;

                var stepOptions = options with
                {
                    Variables = stepVariables,
                    CleanWorkspace = false,
                    Timeout = attempt == 1 ? remaining : left,
                    StepName = step.Name,
                    StepNumber = step.Number
                };

                var attemptPrefix = earlier.ToString();
                var prefix = completed.Snapshot();
                var progress = new Progress<string>(log =>
                {
                    _ = sink.ReportStepLogAsync(step, attemptPrefix + log, CancellationToken.None);

                    // The aggregated log would fight itself when steps run in parallel, so only update it while this step is the only one running.
                    if (Volatile.Read(ref activeCount) == 1)
                        _ = sink.ReportBuildLogAsync(
                            Truncate($"{prefix}{Header(step)}\n{attemptPrefix}{log}"),
                            CancellationToken.None);
                });

                outcome = await executor.ExecuteAsync(job, step.Command, stepOptions, progress, cancellationToken);

                if (outcome.Completion.Status == BuildStatus.Succeeded ||
                    outcome.Completion.TimedOut ||
                    cancellationToken.IsCancellationRequested ||
                    attempt == attempts)
                    break;

                earlier.Append(outcome.Completion.Log.TrimEnd())
                    .Append('\n')
                    .Append($"（失敗，進行第 {attempt} 次重試，最多 {attempts - 1} 次）")
                    .Append('\n');
            }

            var result = earlier.Length == 0
                ? outcome!
                : outcome! with { Completion = outcome.Completion with { Log = earlier + outcome.Completion.Log } };

            // Outputs and the env file are both read here (the files close with the using); merging into the shared variable table is left to the main loop.
            var problems = new List<string>();
            var outputs = stepOutputFile.ReadOutputs(problems);
            var exported = new Dictionary<string, string>(StringComparer.Ordinal);
            stepEnvFile.MergeInto(exported);
            return new StepRun(step, result, outputs, problems, exported);
        }

        while (outcomes.Count < steps.Count)
        {
            var remaining = deadline - DateTime.UtcNow;

            // Cancelled (an admin asked for it, or the service is stopping): nothing further starts; the steps already running
            // have been killed by the executor and are collected below, the rest are marked skipped after the loop.
            if (cancellationToken.IsCancellationRequested && running.Count == 0)
            {
                status = BuildStatus.Failed;
                break;
            }

            // Timeout: everything not started yet is marked failed; running steps are wound up by the executor's own timeout.
            if (remaining <= TimeSpan.Zero && running.Count == 0)
            {
                var message = $"已超過任務設定的 {job.TimeoutMinutes} 分鐘逾時時間，這一步沒有開始。";
                foreach (var step in steps.Where(step => !outcomes.ContainsKey(step.Number)))
                {
                    await sink.CompleteStepAsync(step, BuildStepStatus.Failed, null, message, NoOutputs, CancellationToken.None);
                    completed.AppendLine(Header(step)).AppendLine(message).AppendLine();
                    outcomes[step.Number] = new StepOutcome(BuildStepStatus.Failed, true);
                }

                status = BuildStatus.Failed;
                timedOut = true;
                break;
            }

            // Find the steps whose dependencies have all finished and that have not run yet; mark the ones that should not run as skipped and start the rest.
            var startedAny = false;
            foreach (var step in steps)
            {
                if (outcomes.ContainsKey(step.Number) || running.ContainsValue(step.Number)) continue;
                if (remaining <= TimeSpan.Zero) break;

                var upstream = dependencies[position[step.Number]];
                if (!upstream.All(outcomes.ContainsKey)) continue;

                var upstreamFailed = upstream.Any(number => outcomes[number].FailedUpstream);
                var upstreamOk = upstream.All(number => outcomes[number].Status == BuildStepStatus.Succeeded);
                var shouldRun = StepRunCondition.Normalize(step.RunWhen) switch
                {
                    StepRunCondition.Always => true,
                    StepRunCondition.Failure => upstreamFailed,
                    _ => upstreamOk && !upstreamFailed
                };

                if (!shouldRun)
                {
                    var reason = upstreamFailed
                        ? "（前面已有步驟失敗，這一步只在成功時執行，已略過）"
                        : "（這一步只在失敗時執行，已略過）";
                    await sink.CompleteStepAsync(step, BuildStepStatus.Skipped, null, string.Empty, NoOutputs, CancellationToken.None);
                    completed.AppendLine(Header(step)).AppendLine(reason).AppendLine();
                    outcomes[step.Number] = new StepOutcome(BuildStepStatus.Skipped, upstreamFailed);
                    startedAny = true; // Progress was made, scan again
                    continue;
                }

                // Data-driven condition: evaluate against the current outputs and environment variables; when false the step is skipped (not a failure, downstream is judged as usual).
                if (!string.IsNullOrWhiteSpace(step.Condition))
                {
                    var parsed = StepCondition.Parse(step.Condition);
                    bool holds;
                    if (!parsed.IsValid)
                    {
                        var message = $"條件有錯：{parsed.Error}";
                        await sink.CompleteStepAsync(step, BuildStepStatus.Failed, null, message, NoOutputs, CancellationToken.None);
                        completed.AppendLine(Header(step)).AppendLine(message).AppendLine();
                        outcomes[step.Number] = new StepOutcome(BuildStepStatus.Failed, true);
                        status = BuildStatus.Failed;
                        startedAny = true;
                        continue;
                    }

                    lock (variablesGate)
                    {
                        holds = StepCondition.Evaluate(parsed.Root!, variables);
                    }

                    if (!holds)
                    {
                        var reason = $"（條件不成立，已略過：{step.Condition.Trim()}）";
                        await sink.CompleteStepAsync(step, BuildStepStatus.Skipped, null, string.Empty, NoOutputs, CancellationToken.None);
                        completed.AppendLine(Header(step)).AppendLine(reason).AppendLine();
                        outcomes[step.Number] = new StepOutcome(BuildStepStatus.Skipped, false);
                        startedAny = true;
                        continue;
                    }
                }

                if (running.Count >= BuildStepGraph.MaximumConcurrentSteps) break;

                Interlocked.Increment(ref activeCount);
                running[RunOneAsync(step, remaining)] = step.Number;
                startedAny = true;
            }

            if (running.Count == 0)
            {
                if (startedAny) continue;
                if (outcomes.Count < steps.Count)
                {
                    // The graph was already checked for cycles, so this should be unreachable; if it happens anyway, do not hang.
                    var message = "步驟依賴無法解析，這一步沒有開始。";
                    foreach (var step in steps.Where(step => !outcomes.ContainsKey(step.Number)))
                    {
                        await sink.CompleteStepAsync(step, BuildStepStatus.Failed, null, message, NoOutputs, CancellationToken.None);
                        completed.AppendLine(Header(step)).AppendLine(message).AppendLine();
                        outcomes[step.Number] = new StepOutcome(BuildStepStatus.Failed, true);
                    }

                    status = BuildStatus.Failed;
                }

                break;
            }

            var finishedTask = await Task.WhenAny(running.Keys);
            var concurrent = running.Count > 1;
            running.Remove(finishedTask);
            Interlocked.Decrement(ref activeCount);
            var run = await finishedTask;
            var finished = run.Step;
            var completion = run.Result.Completion;
            workingDirectory = run.Result.WorkingDirectory;
            var succeeded = completion.Status == BuildStatus.Succeeded;

            // Outputs: injected as WALIS_STEP_<key>_<name>, masked when reported (visible in the UI); the env file is merged as before.
            lock (variablesGate)
            {
                if (!string.IsNullOrEmpty(finished.StepKey))
                    foreach (var (name, value) in run.Outputs)
                        variables[StepExpressions.VariableName(finished.StepKey, name)] = value;
                foreach (var (name, value) in run.Exported) variables[name] = value;
            }

            await sink.CompleteStepAsync(
                finished,
                succeeded ? BuildStepStatus.Succeeded : BuildStepStatus.Failed,
                completion.ExitCode,
                completion.Log,
                MaskOutputs(run.Outputs, options),
                CancellationToken.None);

            completed.AppendLine(Header(finished));
            if (concurrent) completed.AppendLine("（與其他步驟平行執行）");
            completed.AppendLine(completion.Log.TrimEnd());
            foreach (var problem in run.Problems) completed.AppendLine($"（輸出：{problem}）");
            if (run.Outputs.Count != 0)
                completed.AppendLine($"（輸出 {run.Outputs.Count} 個：{string.Join(", ", run.Outputs.Keys)}）");
            completed.AppendLine();

            if (succeeded)
            {
                outcomes[finished.Number] = new StepOutcome(BuildStepStatus.Succeeded, false);
                continue;
            }

            if (completion.TimedOut) timedOut = true;

            if (finished.ContinueOnError)
            {
                completed.AppendLine($"（步驟 {finished.Number} 失敗，但設定為繼續執行）").AppendLine();
                // Equivalent to success for downstream: the build continues and the result is not counted as failed.
                outcomes[finished.Number] = new StepOutcome(BuildStepStatus.Succeeded, false);
                continue;
            }

            outcomes[finished.Number] = new StepOutcome(BuildStepStatus.Failed, true);
            status = BuildStatus.Failed;
            exitCode = completion.ExitCode;

            // Do not abort outright: downstream steps marked always/failure (cleanup, notifications) still have to run;
            // success-only steps are marked skipped in the next round. A timeout or a cancel really stops everything.
            if (timedOut || cancellationToken.IsCancellationRequested)
            {
                foreach (var task in running.Keys)
                {
                    try { await task; } catch { /* Winding up after a timeout; individual results no longer matter */ }
                }

                break;
            }
        }

        // A normal run leaves no Pending steps; after a timeout or an exception the remainder is marked skipped.
        await sink.SkipRemainingAsync(CancellationToken.None);

        // Only save on a successful build: the directory state after a failure should not be handed to the next run.
        if (status == BuildStatus.Succeeded && hasCache)
        {
            if (ShellCommandExecutor.TryPreflight(
                    workingDirectory,
                    options,
                    true,
                    out var safeWorkingDirectory,
                    out var saveFailure))
            {
                var saved = BuildCache.Save(
                    options.CacheRoot!,
                    safeWorkingDirectory,
                    options.CachePaths,
                    cancellationToken);
                completed.AppendLine($"保存快取：{saved} 個路徑");
            }
            else
            {
                completed.AppendLine($"略過保存快取：{saveFailure}");
                status = BuildStatus.Failed;
                exitCode = null;
            }
        }

        return new BuildExecutionResult(
            new BuildCompletion(status, exitCode, completed.Snapshot()) { TimedOut = timedOut },
            workingDirectory);
    }

    private static string Header(BuildStep step)
    {
        return $"===== 步驟 {step.Number}：{step.Name} =====";
    }

    /// <summary>
    ///     The post-build clean. Called by the host (local worker or remote runner) once artifacts and the cache are saved,
    ///     while it still holds the workspace lease; the same allowlist and realpath checks as the pre-build clean apply.
    /// </summary>
    public static string CleanAfterBuild(string workingDirectory, BuildExecutionOptions options)
    {
        if (!ShellCommandExecutor.TryPreflight(workingDirectory, options, true, out var safeWorkingDirectory, out var failure))
            return $"略過建置後清空工作目錄：{failure}";

        if (!Directory.Exists(safeWorkingDirectory)) return "建置後清空工作目錄：目錄已不存在。";

        return $"建置後{WorkspaceCleaner.Clean(safeWorkingDirectory, options.AllowedWorkingDirectories)}";
    }

    /// <summary>What a finished step brings back; the files are already read and closed, the main loop only merges and reports.</summary>
    private sealed record StepRun(
        BuildStep Step,
        BuildExecutionResult Result,
        IReadOnlyDictionary<string, string> Outputs,
        List<string> Problems,
        Dictionary<string, string> Exported);

    /// <summary>FailedUpstream: this step failed, or was skipped because an upstream step failed, so downstream failure conditions can see it.</summary>
    private sealed record StepOutcome(string Status, bool FailedUpstream);

    private static readonly IReadOnlyDictionary<string, string> NoOutputs = new Dictionary<string, string>();

    /// <summary>Outputs stored in the database and shown in the UI must mask secrets like the log does; the values injected into later steps are the originals.</summary>
    private static IReadOnlyDictionary<string, string> MaskOutputs(
        IReadOnlyDictionary<string, string> outputs,
        BuildExecutionOptions options)
    {
        if (outputs.Count == 0 || !options.MaskSecrets || options.Secrets.Count == 0) return outputs;

        return outputs.ToDictionary(
            pair => pair.Key,
            pair => ShellCommandExecutor.SanitizeLog(pair.Value, options.Secrets, options.MaskSecrets),
            StringComparer.Ordinal);
    }

    public static async Task<IDisposable> AcquireWorkspaceAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var gate = WorkspaceLocks.GetOrAdd(workingDirectory, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new WorkspaceLease(gate);
    }

    private static string Truncate(string value)
    {
        return value.Length <= MaximumAggregateLength
            ? value
            : $"{value[..MaximumAggregateLength]}\n\n[日誌已截斷，完整內容請看各步驟]";
    }

    private sealed class WorkspaceLease(SemaphoreSlim gate) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            gate.Release();
        }
    }

    /// <summary>
    ///     Each step owns an unpredictable environment file created atomically with CreateNew.
    ///     After the script finishes it is only read through the originally opened handle, never reopened by pathname; even if the script
    ///     swaps the path for a symlink pointing at the database, the host process only reads the original inode.
    /// </summary>
    private sealed class StepEnvironmentFile : IDisposable
    {
        private const int MaximumBytes = 64 * 1024;
        private readonly FileStream _stream;
        private bool _disposed;

        private StepEnvironmentFile(string path, FileStream stream)
        {
            Path = path;
            _stream = stream;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _stream.Dispose();

            try
            {
                // If the script replaced the pathname with a symlink, File.Delete removes only the link itself and never touches the target.
                File.Delete(Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A failed temporary-file cleanup does not change the build result.
            }
        }

        /// <param name="directory">The build's private scratch directory; never the shared temp directory.</param>
        public static StepEnvironmentFile Create(string directory, string purpose)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var path = System.IO.Path.Combine(directory, $"{purpose}-{Guid.NewGuid():N}");

                try
                {
                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.ReadWrite | FileShare.Delete,
                        Options = FileOptions.Asynchronous
                    };

                    if (!OperatingSystem.IsWindows())
                        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

                    return new StepEnvironmentFile(path, new FileStream(path, options));
                }
                catch (IOException) when (attempt < 9)
                {
                    // Extremely unlikely name collision; retry with another random name.
                }
            }

            throw new IOException("無法安全建立步驟環境變數檔。");
        }

        /// <summary>Reads the step outputs; likewise only from the original handle, and an oversized file is ignored entirely.</summary>
        public IReadOnlyDictionary<string, string> ReadOutputs(List<string> problems)
        {
            var content = ReadContent();
            if (content is null)
            {
                problems.Add($"輸出檔超過 {MaximumBytes / 1024} KB，整份忽略。");
                return new Dictionary<string, string>();
            }

            return content.Length == 0
                ? new Dictionary<string, string>()
                : StepExpressions.ParseOutputs(content, problems);
        }

        private string? ReadContent()
        {
            _stream.Flush();
            if (_stream.Length > MaximumBytes) return null;

            _stream.Position = 0;
            var bytes = new byte[(int)_stream.Length];
            _stream.ReadExactly(bytes);
            return Encoding.UTF8.GetString(bytes);
        }

        public void MergeInto(Dictionary<string, string> variables)
        {
            // An oversized env file is itself a memory DoS; ignore the whole file when over budget rather than parsing half a line.
            var content = ReadContent();
            if (content is null) return;

            foreach (var line in content.Split('\r', '\n'))
            {
                var separator = line.IndexOf('=');

                if (separator <= 0 || line.StartsWith('#')) continue;

                var name = line[..separator].Trim();

                // WALIS_* (build id, step outputs, upstream status) and the pinned HOME/ZDOTDIR are never overridden from a step.
                if (name.Length != 0 && ShellCommandExecutor.IsExportableVariableName(name))
                    variables[name] = line[(separator + 1)..];
            }
        }
    }

    /// <summary>Caps the multi-step aggregate; each step already has its own limit, but the step count may also be user-controlled.</summary>
    private sealed class BoundedAggregateLog(int capacity)
    {
        private const string Marker = "\n\n[彙總日誌已截斷，完整內容請看各步驟]";
        private readonly StringBuilder _value = new(capacity);
        private bool _truncated;

        public BoundedAggregateLog AppendLine(string? value)
        {
            Append(value ?? string.Empty);
            Append(Environment.NewLine);
            return this;
        }

        public BoundedAggregateLog AppendLine()
        {
            Append(Environment.NewLine);
            return this;
        }

        public string Snapshot()
        {
            if (!_truncated) return _value.ToString();

            var keep = Math.Max(0, capacity - Marker.Length);
            var text = _value.ToString();
            return $"{text[..Math.Min(text.Length, keep)]}{Marker}";
        }

        private void Append(string value)
        {
            var remaining = capacity - _value.Length;

            if (remaining > 0) _value.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));

            if (value.Length > remaining) _truncated = true;
        }
    }
}