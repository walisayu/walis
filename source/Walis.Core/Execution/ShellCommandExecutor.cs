using System.Diagnostics;
using System.Text;
using Walis.Core.Models;
using Walis.Core.Security;

namespace Walis.Core.Execution;

public sealed record BuildExecutionOptions(
    string? DefaultWorkingDirectory,
    string? Shell,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<string> Secrets,
    bool MaskSecrets)
{
    /// <summary>Whether to clear the workspace contents before running; handled by the step loop before the first step.</summary>
    public bool CleanWorkspace { get; init; }

    /// <summary>
    ///     Whether to clear the workspace once the build is over (success or failure). The caller runs it after artifacts and the
    ///     cache have been saved, through <see cref="BuildStepSequence.CleanAfterBuild" />, so nothing worth keeping is lost.
    /// </summary>
    public bool CleanWorkspaceAfterBuild { get; init; }

    /// <summary>Relative paths to keep between builds.</summary>
    public IReadOnlyList<string> CachePaths { get; init; } = [];

    /// <summary>Where the cache is stored; empty means caching is disabled.</summary>
    public string? CacheRoot { get; init; }

    /// <summary>The timeout for this run; defaults to the job's minutes when unset. With multiple steps the caller computes the remaining time.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Step name and number, exposed to the script as environment variables.</summary>
    public string? StepName { get; init; }

    public int? StepNumber { get; init; }

    /// <summary>The sandbox-exec profile; when set, the whole step is wrapped in the sandbox.</summary>
    public string? SandboxProfile { get; init; }

    /// <summary>Allowed workspaces; empty means no restriction.</summary>
    public IReadOnlyList<string> AllowedWorkingDirectories { get; init; } = [];

    /// <summary>
    ///     Paths the controller/runner's own data, cache and other builds must never touch. The workspace must not be an
    ///     ancestor or descendant of any of them, so host-process I/O cannot become a proxy for bypassing the sandbox.
    /// </summary>
    public IReadOnlyList<string> ProtectedPaths { get; init; } = [];

    /// <summary>Requested explicitly by the caller when host-process I/O such as artifacts also needs the allowlist.</summary>
    public bool RequireWorkspaceAllowlist { get; init; }

    /// <summary>The caller already holds a workspace lease covering both the execution and artifact phases.</summary>
    public bool WorkspaceLockAlreadyHeld { get; init; }

    /// <summary>The canonical pathname locked when the caller acquired the lease, for comparison after locking.</summary>
    public string? LockedWorkingDirectory { get; init; }

    /// <summary>The absolute deadline counted from when the build entered Running, including the time spent waiting for the workspace lease.</summary>
    public DateTime? DeadlineUtc { get; init; }

    /// <summary>
    ///     When enabled, refuses to run if the current platform has no sandbox that can actually be applied. This keeps "isolation required" from degrading silently.
    /// </summary>
    public bool RequireSandbox { get; init; }

    /// <summary>
    ///     The job is an admin-declared trusted job that runs without the sandbox on purpose (the self-update job). Only changes
    ///     what the log says; the caller already left <see cref="SandboxProfile" /> empty and <see cref="RequireSandbox" /> off.
    /// </summary>
    public bool TrustedUnsandboxed { get; init; }
}

/// <summary>The execution result, including the workspace actually used, for collecting artifacts afterwards.</summary>
public sealed record BuildExecutionResult(BuildCompletion Completion, string WorkingDirectory);

public sealed class ShellCommandExecutor
{
    /// <summary>The first log line of a step that an administrator deliberately exempted from the sandbox.</summary>
    public const string TrustedUnsandboxedNotice = "信任任務：管理員已設定此任務不套用沙箱，腳本以執行器帳號的權限直接執行。";

    public const int MaximumLogLength = 64_000;
    public const int MaximumSecretLength = 4_096;
    public const int MaximumSecretCount = 256;

    /// <summary>Names the system injects itself (build number, step outputs, upstream status); user-defined names must not use it.</summary>
    public const string ReservedVariablePrefix = "WALIS_";

    /// <summary>
    ///     Whether a name written by a step to <c>$WALIS_ENV_FILE</c> may reach later steps. System names and the two the
    ///     executor pins (HOME, ZDOTDIR, so the shell never reads the service account's startup files) are dropped.
    ///     PATH and the like are deliberately left alone: a job that installs its own toolchain needs to prepend to them.
    /// </summary>
    public static bool IsExportableVariableName(string name)
    {
        return !name.StartsWith(ReservedVariablePrefix, StringComparison.OrdinalIgnoreCase) &&
               name is not ("HOME" or "ZDOTDIR");
    }

    private static readonly string[] SafeInheritedEnvironment =
    [
        "PATH", "USER", "LOGNAME", "SHELL",
        "LANG", "LANGUAGE", "TERM", "COLORTERM", "NO_COLOR",
        "TMPDIR", "TMP", "TEMP",
        "SystemRoot", "WINDIR", "ComSpec", "PATHEXT",
        "SSL_CERT_FILE", "SSL_CERT_DIR"
    ];

    /// <summary>How often the current output is reported while running; too frequent and the database is written constantly.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long leftover processes get after SIGTERM before the whole group is killed.</summary>
    private static readonly TimeSpan GroupGracePeriod = TimeSpan.FromSeconds(5);

    public async Task<BuildExecutionResult> ExecuteAsync(
        Job job,
        BuildExecutionOptions options,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(job, job.Command, options, null, cancellationToken);
    }

    public async Task<BuildExecutionResult> ExecuteAsync(
        Job job,
        BuildExecutionOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(job, job.Command, options, progress, cancellationToken);
    }

    /// <param name="command">The script to actually run; with multiple steps, the content of a single step.</param>
    /// <param name="progress">
    ///     Receives the complete output so far at regular intervals while running, so the admin UI can show live progress.
    /// </param>
    public async Task<BuildExecutionResult> ExecuteAsync(
        Job job,
        string command,
        BuildExecutionOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var requestedWorkingDirectory = ResolveWorkingDirectory(job, options);

        var timeoutSpan = options.Timeout ?? TimeSpan.FromMinutes(job.TimeoutMinutes);

        if (options.Secrets.Count > MaximumSecretCount ||
            options.Secrets.Any(secret => secret.Length > MaximumSecretLength))
            return new BuildExecutionResult(
                new BuildCompletion(
                    BuildStatus.Failed,
                    null,
                    $"建置前安全檢查失敗：每筆機密不可超過 {MaximumSecretLength:N0} 個字元，"
                    + $"且每個 job 不可超過 {MaximumSecretCount:N0} 筆機密。\n"),
                requestedWorkingDirectory);

        // Mask the secrets' encodings too (base64, URL, JSON, HTML), and whatever credentials sit inside repository URLs in the
        // job or the script. Expanded once per run; the streaming buffer and the final pass share the list.
        var secrets = ExpandSecrets(options.Secrets, job.Repository, command);

        var commandBuffer = new BoundedOutputBuffer(
            MaximumLogLength,
            secrets,
            options.MaskSecrets);
        AppendInChunks(commandBuffer, command);
        commandBuffer.Complete();
        var displayedCommand = commandBuffer.Snapshot();
        var commandWasTruncated = commandBuffer.IsTruncated;
        var log = new StringBuilder(Math.Min(MaximumLogLength, displayedCommand.Length + 256));

        // An administrator turned masking off: say so where anyone reading the log will see it first.
        if (!options.MaskSecrets) log.AppendLine(MaskingDisabledNotice);

        log.AppendLine($"$ {displayedCommand}")
            .AppendLine($"工作目錄：{requestedWorkingDirectory}")
            .AppendLine($"逾時時間：{timeoutSpan.TotalMinutes:0.#} 分鐘")
            .AppendLine();

        if (!TryPreflight(
                requestedWorkingDirectory,
                options,
                false,
                out var workingDirectory,
                out var failure))
        {
            log.AppendLine(failure);
            return new BuildExecutionResult(
                new BuildCompletion(BuildStatus.Failed, null, Finalize(log, secrets, options, commandWasTruncated)),
                requestedWorkingDirectory);
        }

        log.AppendLine(options.SandboxProfile is not null
            ? "已套用沙箱：Walis 自己的資料目錄、安裝目錄、服務帳號家目錄與設定中列出的路徑不可讀寫。"
            : options.TrustedUnsandboxed
                ? TrustedUnsandboxedNotice
                : "未套用沙箱：腳本以執行器帳號的權限直接執行。");

        var startInfo = CreateStartInfo(job, command, workingDirectory, options);

        // Each step gets its own process group / job object, so background processes the script leaves behind
        // (even ones that detached with setsid or a double fork) are terminated when the step ends.
        ProcessGroups.Wrap(startInfo);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutSpan);

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            log.AppendLine("無法啟動命令程序。");
            return new BuildExecutionResult(
                new BuildCompletion(BuildStatus.Failed, null, Finalize(log, secrets, options, commandWasTruncated)),
                workingDirectory);
        }

        using var group = await ProcessGroups.TryAttachAsync(process);

        // Fixed-size char buffers keep draining both pipes; ReadLine/DataReceived are not used because an attacker
        // only needs to emit a single multi-GB line to allocate an equally large string before we get a chance to truncate.
        // Stream-mask first, then apply the 64k budget; the buffer only holds a pending tail of at most secret length minus one,
        // so a secret prefix straddling a chunk/truncation boundary is never released early into progress or final logs.
        var output = new BoundedOutputBuffer(MaximumLogLength, secrets, options.MaskSecrets);
        using var capture = new CancellationTokenSource();
        var stdout = DrainAsync(process.StandardOutput, output, capture.Token);
        var stderr = DrainAsync(process.StandardError, output, capture.Token);
        var readers = Task.WhenAll(stdout, stderr);

        // Flush progress with a separate timer rather than on each received output:
        // a command that "prints one line and then stays silent for a long time" must still show that line.
        using var pump = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        var pumping = progress is null
            ? Task.CompletedTask
            : PumpAsync(progress, log, output, secrets, options, pump.Token);

        var timedOut = false;
        var survivorsTerminated = false;

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            TryKill(process);
            await Task.Delay(500, CancellationToken.None);
        }
        finally
        {
            // Whether the shell exited on its own, timed out or was cancelled: nothing it started may outlive the step.
            // Done before draining the pipes, because a straggler holding stdout is exactly what makes the drain wait.
            if (group is not null) survivorsTerminated = await group.TerminateAsync(GroupGracePeriod);

            try
            {
                // A normal process reads EOF immediately. If a detached child still holds the pipe, stop the reader
                // so a step that finished successfully cannot hang the controller forever.
                await readers.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                await capture.CancelAsync();
                process.StandardOutput.Close();
                process.StandardError.Close();

                try
                {
                    await readers.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                }
                catch (Exception exception) when (exception is OperationCanceledException or TimeoutException
                                                      or IOException)
                {
                    // A cancelled pipe reader no longer affects build completion; the buffer has stayed within its fixed cap.
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelling the reader only means the pipe did not close normally; the build status is still decided by the process.
            }
            catch (IOException)
            {
                // The pipe may close synchronously when the process exits; whatever is already in the buffer is still usable.
            }

            output.Complete();

            if (progress is not null) progress.Report(Compose(log, output, secrets, options));

            await pump.CancelAsync();
            await pumping;
        }

        var outputSnapshot = output.Snapshot();
        if (outputSnapshot.Length != 0) log.Append(outputSnapshot);

        if (survivorsTerminated) log.AppendLine().AppendLine("步驟結束時仍有背景程序在執行，已一併終止。");

        if (timedOut)
        {
            log.AppendLine().AppendLine($"已超過 {timeoutSpan.TotalMinutes:0.#} 分鐘的逾時時間，程序已被中止。");
            return new BuildExecutionResult(
                new BuildCompletion(
                    BuildStatus.Failed,
                    null,
                    Finalize(log, secrets, options, output.IsTruncated || commandWasTruncated)) { TimedOut = true },
                workingDirectory);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            log.AppendLine().AppendLine("這次建置被中斷（取消要求或服務停止）。");
            return new BuildExecutionResult(
                new BuildCompletion(
                    BuildStatus.Failed,
                    null,
                    Finalize(log, secrets, options, output.IsTruncated || commandWasTruncated)),
                workingDirectory);
        }

        log.AppendLine().AppendLine($"程序結束，exit code: {process.ExitCode}");
        return new BuildExecutionResult(
            new BuildCompletion(
                process.ExitCode == 0 ? BuildStatus.Succeeded : BuildStatus.Failed,
                process.ExitCode,
                Finalize(log, secrets, options, output.IsTruncated || commandWasTruncated)),
            workingDirectory);
    }

    /// <summary>The first line of a build log whose secrets are not masked, so nobody mistakes it for a masked log.</summary>
    public const string MaskingDisabledNotice = "機密遮蔽已關閉：建置輸出會原樣顯示機密值。";

    /// <summary>
    ///     The complete mask list for a run: the given secrets, their encodings, and any credentials embedded in repository URLs
    ///     in the job or the script text. Only the originals count against the size limits; the encodings are derived from them.
    /// </summary>
    public static IReadOnlyList<string> ExpandSecrets(IEnumerable<string> secrets, params string?[] texts)
    {
        return SecretRedactor.Expand(secrets.Concat(RepositoryCredentials.ExtractSecrets(texts)));
    }

    /// <summary>How the workspace is decided; the step loop must use the same rule so cleanup and caching hit the right place.</summary>
    public static string ResolveWorkingDirectory(Job job, BuildExecutionOptions options)
    {
        return FirstNonEmpty(job.WorkingDirectory, options.DefaultWorkingDirectory) ?? Environment.CurrentDirectory;
    }

    /// <summary>
    ///     Shared pre-build checks. The step sequence calls this before clean/cache, and the executor checks again itself,
    ///     so no other caller can bypass the security boundary.
    /// </summary>
    public static bool TryPreflight(
        string requestedWorkingDirectory,
        BuildExecutionOptions options,
        bool requireWorkspaceAllowlist,
        out string workingDirectory,
        out string failure)
    {
        if (!BuildIsolation.TryResolveWorkingDirectory(
                requestedWorkingDirectory,
                options.AllowedWorkingDirectories,
                requireWorkspaceAllowlist,
                out workingDirectory,
                out failure))
            return false;

        if (!BuildIsolation.IsDisjointFrom(workingDirectory, options.ProtectedPaths))
        {
            failure = "工作目錄與 Walis 或 runner 的受保護儲存路徑重疊。";
            workingDirectory = string.Empty;
            return false;
        }

        if (options.RequireSandbox &&
            (!BuildIsolation.IsSandboxAvailable || string.IsNullOrWhiteSpace(options.SandboxProfile)))
        {
            failure = "系統要求隔離建置，但此執行器沒有可套用的沙箱；已拒絕執行，避免靜默降級。";
            return false;
        }

        return true;
    }

    private static ProcessStartInfo CreateStartInfo(
        Job job,
        string command,
        string workingDirectory,
        BuildExecutionOptions options)
    {
        var useSandbox = options.SandboxProfile is not null && BuildIsolation.IsSandboxAvailable;

        var startInfo = new ProcessStartInfo
        {
            FileName = useSandbox ? BuildIsolation.SandboxExecutable : ResolveShell(options.Shell),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (useSandbox)
        {
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(options.SandboxProfile!);
            startInfo.ArgumentList.Add(ResolveShell(options.Shell));
        }

        if (OperatingSystem.IsWindows())
            startInfo.ArgumentList.Add("/C");
        else
            // A login shell loads the service account's profile, which may re-export controller secrets.
            startInfo.ArgumentList.Add("-c");

        startInfo.ArgumentList.Add(command);

        // The child process must not inherit the controller/runner's whole environment; besides connection strings, cloud credentials,
        // runner tokens, startup hooks and the like may hide in it. Keep only the minimal baseline needed to run a shell;
        // every other value must be injected explicitly through Walis's per-job variables.
        var inherited = startInfo.Environment
            .Where(item => IsSafeInheritedEnvironmentName(item.Key))
            .ToArray();
        startInfo.Environment.Clear();

        foreach (var (name, value) in inherited) startInfo.Environment[name] = value;

        foreach (var (name, value) in options.Variables) startInfo.Environment[name] = value;

        // Never let the shell read the service account's HOME/startup files; override even if the job explicitly defines a variable with the same name.
        startInfo.Environment["HOME"] = workingDirectory;
        startInfo.Environment["ZDOTDIR"] = Path.Combine(workingDirectory, ".walis-shell");

        startInfo.Environment["WALIS_JOB_ID"] = job.Id.ToString();
        startInfo.Environment["WALIS_JOB_NAME"] = job.Name;
        startInfo.Environment["WALIS_BRANCH"] = job.Branch;

        if (!string.IsNullOrWhiteSpace(job.Repository))
            startInfo.Environment["WALIS_REPOSITORY"] = job.Repository;

        if (options.StepName is not null) startInfo.Environment["WALIS_STEP_NAME"] = options.StepName;

        if (options.StepNumber is not null)
            startInfo.Environment["WALIS_STEP_NUMBER"] = options.StepNumber.Value.ToString();

        if (!string.IsNullOrWhiteSpace(job.ArtifactPath))
            startInfo.Environment["WALIS_ARTIFACT_PATH"] = job.ArtifactPath;

        return startInfo;
    }

    private static bool IsSafeInheritedEnvironmentName(string name)
    {
        // Windows environment names are case-insensitive. POSIX names are not: treating
        // "Path" or "lc_*" as PATH/LC_* would let a differently-cased secret bypass the
        // allowlist and reach a build process.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return SafeInheritedEnvironment.Any(candidate => string.Equals(candidate, name, comparison)) ||
               name.StartsWith("LC_", comparison);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch (InvalidOperationException)
        {
            // The process already exited, nothing to do.
        }
    }

    /// <summary>Reports the current output periodically; skips when nothing changed to avoid pointless database writes.</summary>
    private static async Task PumpAsync(
        IProgress<string> progress,
        StringBuilder header,
        BoundedOutputBuffer output,
        IReadOnlyList<string> secrets,
        BuildExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var lastVersion = -1L;

        while (!cancellationToken.IsCancellationRequested)
        {
            var version = output.Version;

            if (version != lastVersion)
            {
                lastVersion = version;
                progress.Report(Compose(header, output, secrets, options));
            }

            try
            {
                await Task.Delay(ProgressInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Builds the "header + current output" snapshot for progress reports.</summary>
    private static string Compose(
        StringBuilder header,
        BoundedOutputBuffer output,
        IReadOnlyList<string> secrets,
        BuildExecutionOptions options)
    {
        var snapshot = new StringBuilder(header.ToString());
        snapshot.Append(output.Snapshot());
        return Finalize(snapshot, secrets, options, output.IsTruncated);
    }

    private static async Task DrainAsync(
        StreamReader reader,
        BoundedOutputBuffer output,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);

            if (count == 0) return;

            output.Append(buffer.AsSpan(0, count));
        }
    }

    private static void AppendInChunks(BoundedOutputBuffer output, string value)
    {
        const int chunkSize = 4096;

        for (var offset = 0; offset < value.Length; offset += chunkSize)
            output.Append(value.AsSpan(offset, Math.Min(chunkSize, value.Length - offset)));
    }

    private static string ResolveShell(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && (File.Exists(configured) || OperatingSystem.IsWindows()))
            return configured;

        if (OperatingSystem.IsWindows()) return "cmd.exe";

        return File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/sh";
    }

    private static string Finalize(
        StringBuilder log,
        IReadOnlyList<string> secrets,
        BuildExecutionOptions options,
        bool outputWasTruncated = false)
    {
        var text = Mask(log.ToString(), secrets, options.MaskSecrets);

        if (text.Length <= MaximumLogLength && !outputWasTruncated) return text;

        return Truncate(text);
    }

    /// <summary>
    ///     Content reported by a remote runner must also be re-masked and size-limited before the server stores it.
    ///     The encodings of each secret are masked as well; callers pass the plain secrets.
    /// </summary>
    public static string SanitizeLog(
        string? value,
        IReadOnlyList<string> secrets,
        bool maskSecrets)
    {
        var text = Mask(value ?? string.Empty, maskSecrets ? SecretRedactor.Expand(secrets) : [], maskSecrets);
        return text.Length <= MaximumLogLength ? text : Truncate(text);
    }

    /// <param name="secrets">Already expanded and ordered longest first (see <see cref="SecretRedactor.Expand" />).</param>
    private static string Mask(string text, IReadOnlyList<string> secrets, bool enabled)
    {
        if (!enabled) return text;

        // Longer values first so that replacing secret "abcd" does not leave "abcdef" as "***ef"; Expand already orders them.
        foreach (var secret in secrets)
            if (secret.Length != 0) text = text.Replace(secret, "***", StringComparison.Ordinal);

        return text;
    }

    private static string Truncate(string text)
    {
        var marker = $"\n\n[日誌已截斷，僅保留前 {MaximumLogLength:N0} 個字元]";
        var keep = Math.Max(0, MaximumLogLength - marker.Length);
        return $"{text[..Math.Min(text.Length, keep)]}{marker}";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    /// <summary>
    ///     Applies the fixed cap to the masked output while still draining the pipe. A tail that might be the start of a secret is held
    ///     until the next chunk or EOF before being published, so no partial secret leaks at a progress/truncation boundary.
    /// </summary>
    private sealed class BoundedOutputBuffer
    {
        private readonly int _capacity;
        private readonly Lock _lock = new();
        private readonly int _maximumSecretLength;
        private readonly string[] _secrets;
        private readonly StringBuilder _value;
        private bool _completed;
        private string _pending = string.Empty;
        private bool _truncated;
        private long _version;

        public BoundedOutputBuffer(
            int capacity,
            IReadOnlyList<string> secrets,
            bool maskSecrets)
        {
            _capacity = capacity;
            _value = new StringBuilder(capacity);
            _secrets = maskSecrets
                ?
                [
                    .. secrets
                        .Where(secret => !string.IsNullOrEmpty(secret))
                        .Distinct(StringComparer.Ordinal)
                        .OrderByDescending(secret => secret.Length)
                ]
                : [];
            _maximumSecretLength = _secrets.Select(secret => secret.Length).DefaultIfEmpty(0).Max();
        }

        public long Version
        {
            get
            {
                lock (_lock)
                {
                    return _version;
                }
            }
        }

        public bool IsTruncated
        {
            get
            {
                lock (_lock)
                {
                    return _truncated;
                }
            }
        }

        public void Append(ReadOnlySpan<char> value)
        {
            lock (_lock)
            {
                if (_completed || value.Length == 0) return;

                if (_secrets.Length == 0)
                {
                    AppendVisible(value);
                    return;
                }

                var combined = string.Concat(_pending.AsSpan(), value);
                var safeEnd = Math.Max(0, combined.Length - (_maximumSecretLength - 1));
                var consumed = Process(combined, safeEnd);
                _pending = combined[consumed..];
            }
        }

        public void Complete()
        {
            lock (_lock)
            {
                if (_completed) return;

                _completed = true;

                if (_pending.Length != 0)
                {
                    Process(_pending, _pending.Length);
                    _pending = string.Empty;
                }
            }
        }

        private int Process(string value, int safeEnd)
        {
            var index = 0;

            while (index < safeEnd)
            {
                var matched = _secrets.FirstOrDefault(secret =>
                    value.AsSpan(index).StartsWith(secret.AsSpan(), StringComparison.Ordinal));

                if (matched is not null)
                {
                    AppendVisible("***");
                    index += matched.Length;
                }
                else
                {
                    AppendVisible(value.AsSpan(index, 1));
                    index++;
                }
            }

            return index;
        }

        private void AppendVisible(ReadOnlySpan<char> value)
        {
            var remaining = _capacity - _value.Length;

            if (remaining <= 0)
            {
                _truncated = true;
                return;
            }

            _version++;
            _value.Append(value[..Math.Min(value.Length, remaining)]);

            if (value.Length > remaining) _truncated = true;
        }

        public string Snapshot()
        {
            lock (_lock)
            {
                return _value.ToString();
            }
        }
    }
}