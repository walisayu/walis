using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Runner;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(RunnerOptions.Usage);
    return 0;
}

if (!RunnerOptions.TryParse(args, out var options, out var parseError))
{
    Console.Error.WriteLine(parseError);
    Console.Error.WriteLine();
    Console.Error.WriteLine(RunnerOptions.Usage);
    return 1;
}

// The token is now held in memory; a build running under the same account must not be able to read it back out of this
// process's environment (ps -E on macOS, /proc/<pid>/environ in a container).
RunnerOptions.ClearSensitiveEnvironment();

if (options!.UsesPlaintextRemote)
    Console.Error.WriteLine($"警告：{options.Server} 不是 HTTPS，權杖與建置機密會以明文傳送；正式環境請改用 HTTPS。");

using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    Console.WriteLine("收到中斷訊號，準備結束…");
    shutdown.Cancel();
};

using var client = new WalisClient(options!);
var executor = new ShellCommandExecutor();

if (!string.IsNullOrWhiteSpace(options!.Username) && !string.IsNullOrWhiteSpace(options.Password))
{
    var token = await client.SignInAsync(options.Username, options.Password, shutdown.Token);

    if (token is null)
    {
        Console.Error.WriteLine("登入失敗，請確認帳號密碼。");
        return 1;
    }

    Console.WriteLine("已登入。");
}

try
{
    if (!await client.EnsureRegisteredAsync(options, shutdown.Token))
    {
        Console.Error.WriteLine("無法註冊執行器，請確認名稱沒有重複、且權杖有效。");
        return 1;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"無法連線到 {options.Server}：{exception.Message}");
    return 1;
}

Console.WriteLine($"執行器 {options.Name} 已連線到 {options.Server}，每 {options.Interval.TotalSeconds:0} 秒輪詢一次。");

while (!shutdown.IsCancellationRequested)
    try
    {
        var work = await client.ClaimAsync(options.Name, shutdown.Token);

        if (work is null)
        {
            await Task.Delay(options.Interval, shutdown.Token);
            continue;
        }

        await RunAsync(work);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
        break;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"輪詢失敗：{exception.Message}");

        try
        {
            await Task.Delay(options.Interval, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }

Console.WriteLine("執行器已停止。");
return 0;

async Task RunAsync(ClaimedWork work)
{
    Console.WriteLine($"開始執行 {work.Job.Name} #{work.Build.Number}");
    // The cache root is per account (never the shared temp directory, where another local user could create it first and
    // swap a job's cache between builds) and must be private to this account; otherwise the build runs without a cache.
    var cacheStorageRoot = RunnerCacheRoot();
    var collectsArtifacts = !string.IsNullOrWhiteSpace(work.Job.ArtifactPath);

    // Jobs without their own directory each get a subdirectory of --workdir (or the current directory), never one shared
    // workspace: a job must not find another job's checkout, or files it left for the next build to run.
    var workRoot = Path.GetFullPath(options!.WorkingDirectory ?? Environment.CurrentDirectory);
    var jobWorkspace = Path.Combine(workRoot, work.Job.Id.ToString("N"));
    if (string.IsNullOrWhiteSpace(work.Job.WorkingDirectory)) Directory.CreateDirectory(jobWorkspace);

    var executionOptions = new BuildExecutionOptions(
        jobWorkspace,
        null,
        work.Variables,
        work.Secrets ?? [],
        work.MaskSecrets)
    {
        RequireSandbox = work.RequireSandbox && !work.ExternalIsolationSatisfied,
        TrustedUnsandboxed = work.TrustedUnsandboxed,

        // The server resolved the job option against the system setting before handing the work over.
        CleanWorkspace = work.CleanWorkspace,
        CleanWorkspaceAfterBuild = work.CleanWorkspaceAfterBuild,

        // The cache lives under the runner's own directory, one per job.
        CachePaths = BuildCache.ParsePaths(work.Job.CachePaths),
        CacheRoot = cacheStorageRoot is null ? null : Path.Combine(cacheStorageRoot, work.Job.Id.ToString("N")),
        ProtectedPaths = cacheStorageRoot is null ? [] : [cacheStorageRoot],
        RequireWorkspaceAllowlist = collectsArtifacts,
        DeadlineUtc = DateTime.UtcNow.AddMinutes(work.Job.TimeoutMinutes),
        AllowedWorkingDirectories = [workRoot]
    };

    // Steps are planned by the server and sent together; this only runs them in order and reports back.
    var sink = new RemoteStepSink(client, work.Build.Id);
    IDisposable? workspaceLease = null;
    var requestedWorkingDirectory = ShellCommandExecutor.ResolveWorkingDirectory(work.Job, executionOptions);
    var requiresAllowlist = executionOptions.RequireWorkspaceAllowlist || executionOptions.CleanWorkspace ||
                            executionOptions.CleanWorkspaceAfterBuild ||
                            (executionOptions.CacheRoot is not null && executionOptions.CachePaths.Count != 0);

    if (ShellCommandExecutor.TryPreflight(
            requestedWorkingDirectory,
            executionOptions,
            requiresAllowlist,
            out var lockPath,
            out _))
    {
        workspaceLease = await BuildStepSequence.AcquireWorkspaceAsync(lockPath, shutdown.Token);
        executionOptions = executionOptions with
        {
            WorkspaceLockAlreadyHeld = true,
            LockedWorkingDirectory = lockPath
        };
    }

    BuildCompletion completion;

    // The server flags a cancel on the next report (step log, step result or live output); from then on the token is
    // cancelled, the executor kills the step's process group and the build is reported back as Canceled.
    using var buildCancel = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
    var cancelRequested = false;

    void OnCancelRequested()
    {
        if (cancelRequested) return;

        cancelRequested = true;
        Console.WriteLine($"  伺服器要求取消 {work.Job.Name} #{work.Build.Number}，正在中止步驟…");
        buildCancel.Cancel();
    }

    client.CancelRequested += OnCancelRequested;

    try
    {
        BuildExecutionResult execution;

        try
        {
            execution = await new BuildStepSequence(executor).RunAsync(
                work.Job,
                work.Build.Id,
                work.Steps,
                executionOptions,
                sink,
                buildCancel.Token);
        }
        catch (OperationCanceledException) when (cancelRequested && !shutdown.IsCancellationRequested)
        {
            execution = new BuildExecutionResult(
                new BuildCompletion(BuildStatus.Failed, null, string.Empty),
                requestedWorkingDirectory);
        }

        completion = execution.Completion;

        if (cancelRequested && !shutdown.IsCancellationRequested)
            completion = completion with
            {
                Status = BuildStatus.Canceled,
                ExitCode = null,
                Log = $"{completion.Log.TrimEnd()}{Environment.NewLine}{Environment.NewLine}建置已依伺服器的取消要求中止。{Environment.NewLine}"
            };

        if (completion.Status == BuildStatus.Succeeded && collectsArtifacts)
            completion = completion with
            {
                Log = completion.Log + await UploadAsync(
                    work,
                    execution.WorkingDirectory,
                    executionOptions.ProtectedPaths)
            };

        // Same order as the server's own executor: artifacts first, then the workspace is emptied.
        if (executionOptions.CleanWorkspaceAfterBuild)
            completion = completion with
            {
                Log = $"{completion.Log}{Environment.NewLine}{BuildStepSequence.CleanAfterBuild(execution.WorkingDirectory, executionOptions)}{Environment.NewLine}"
            };
    }
    finally
    {
        client.CancelRequested -= OnCancelRequested;
        workspaceLease?.Dispose();
    }

    await client.ReportAsync(work.Build.Id, completion, CancellationToken.None);
    Console.WriteLine($"完成 {work.Job.Name} #{work.Build.Number}：{completion.Status}");
}

async Task<string> UploadAsync(
    ClaimedWork work,
    string workingDirectory,
    IReadOnlyList<string> protectedPaths)
{
    var staging = Path.Combine(Path.GetTempPath(), $"walis-artifacts-{Guid.NewGuid():N}");

    try
    {
        var collected = ArtifactCollector.Collect(
            workingDirectory,
            work.Job.ArtifactPath,
            staging,
            out var report,
            protectedPaths);

        if (collected.Count == 0) return $"{Environment.NewLine}{report}{Environment.NewLine}";

        var uploaded = await client.UploadArtifactsAsync(
            work.Build.Id,
            staging,
            collected.Select(artifact => artifact.RelativePath),
            CancellationToken.None);

        return $"{Environment.NewLine}{report} 已上傳 {uploaded} 個檔案。{Environment.NewLine}";
    }
    catch (Exception exception)
    {
        return $"{Environment.NewLine}上傳產出檔案失敗：{exception.Message}{Environment.NewLine}";
    }
    finally
    {
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
    }
}

/// <summary>Reports step progress to the server; skipped steps are handled by the server when it receives the final result.</summary>

static string? RunnerCacheRoot()
{
    var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrWhiteSpace(baseDirectory)) baseDirectory = Path.GetTempPath();

    var runnerRoot = Path.Combine(baseDirectory, "walis-runner");
    var cacheRoot = Path.Combine(runnerRoot, "cache");

    try
    {
        // Both levels: an attacker-owned parent could rename the cache directory away and put its own in place.
        Walis.Core.Backup.PrivateFileSystem.EnsurePrivateDirectory(runnerRoot);
        Walis.Core.Backup.PrivateFileSystem.EnsurePrivateDirectory(cacheRoot);
        return cacheRoot;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"警告：快取目錄 {cacheRoot} 不是本帳號的私有目錄（{exception.Message}），這次建置不使用快取。");
        return null;
    }
}

internal sealed class RemoteStepSink(WalisClient client, Guid buildId) : IBuildStepSink
{
    public Task MarkRunningAsync(BuildStep step, CancellationToken cancellationToken)
    {
        Console.WriteLine($"  步驟 {step.Number}：{step.Name}");
        return Task.CompletedTask;
    }

    public Task ReportStepLogAsync(BuildStep step, string log, CancellationToken cancellationToken)
    {
        return client.ReportStepLogAsync(buildId, step.Id, log, cancellationToken);
    }

    public Task CompleteStepAsync(
        BuildStep step,
        string status,
        int? exitCode,
        string log,
        IReadOnlyDictionary<string, string> outputs,
        CancellationToken cancellationToken)
    {
        return client.ReportStepResultAsync(buildId, step.Id, status, exitCode, log, outputs, cancellationToken);
    }

    public Task SkipRemainingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task ReportBuildLogAsync(string log, CancellationToken cancellationToken)
    {
        return client.ReportLogAsync(buildId, log, cancellationToken);
    }
}
