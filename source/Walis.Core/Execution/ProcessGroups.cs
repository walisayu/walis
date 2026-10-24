using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Walis.Core.Execution;

/// <summary>
///     Runs each step's shell in its own process group (Unix) or job object (Windows), so that when the step ends everything
///     the script started can be terminated as a unit. <c>Process.Kill(entireProcessTree)</c> walks parent/child links and
///     misses anything that double-forked or called <c>setsid</c>; a process group or job survives that.
/// </summary>
/// <remarks>
///     On Unix the group has to be created by the child itself before it execs: <c>setpgid</c> from the parent fails with
///     EACCES once the child has exec'd, and <see cref="Process.Start()" /> returns only after the exec. So the shell is
///     launched through a tiny wrapper that calls <c>setsid</c>/<c>setpgrp</c> and execs the real command in place:
///     util-linux or busybox <c>setsid</c> where it exists (Linux), otherwise <c>perl</c> (always present on macOS).
///     When neither is available the step runs as before and only the process-tree kill applies.
/// </remarks>
public static class ProcessGroups
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;
    private const int Esrch = 3;

    private static readonly string[] SetsidCandidates = ["/usr/bin/setsid", "/bin/setsid", "/usr/local/bin/setsid"];

    private static readonly string[] PerlCandidates =
        ["/usr/bin/perl", "/usr/local/bin/perl", "/opt/homebrew/bin/perl"];

    /// <summary>
    ///     setpgrp(0, 0) makes the wrapper the leader of a fresh group with pgid == pid; the exec keeps the pid, so the shell
    ///     and everything it spawns (unless something calls setsid again) stays in that group. A failed setpgrp exits with
    ///     <see cref="WrapperFailureExitCode" /> before anything else could have been started.
    /// </summary>
    private const string PerlProgram = "setpgrp(0, 0) or exit 253; exec { $ARGV[0] } @ARGV; exit 127;";

    private const int WrapperFailureExitCode = 253;

    /// <summary>How long to wait for the wrapper to create the group before giving up on confirming it.</summary>
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(2);

    private static readonly Lazy<UnixWrapper?> Unix = new(ResolveUnixWrapper);

    /// <summary>Whether this platform can place a step in its own group; false means only the tree kill is available.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() ? true : Unix.Value is not null;

    /// <summary>A description for the build log, e.g. "setsid" or "perl"; null when unsupported.</summary>
    public static string? Mechanism => OperatingSystem.IsWindows()
        ? "job object"
        : Unix.Value is { } wrapper ? Path.GetFileName(wrapper.Path) : null;

    /// <summary>Rewrites the start info so the command is launched inside its own process group. No-op where unsupported.</summary>
    public static void Wrap(ProcessStartInfo startInfo)
    {
        if (OperatingSystem.IsWindows() || Unix.Value is not { } wrapper) return;

        var original = new List<string>(startInfo.ArgumentList.Count + 1) { startInfo.FileName };
        original.AddRange(startInfo.ArgumentList);

        startInfo.ArgumentList.Clear();
        startInfo.FileName = wrapper.Path;

        if (wrapper.IsPerl)
        {
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(PerlProgram);
            startInfo.ArgumentList.Add("--");
        }

        foreach (var argument in original) startInfo.ArgumentList.Add(argument);
    }

    /// <summary>
    ///     Attaches to the group of a process that was started through <see cref="Wrap" />. Returns null when the group could not be
    ///     confirmed, in which case the caller falls back to killing the process tree only.
    /// </summary>
    public static async Task<ProcessGroupHandle?> TryAttachAsync(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsJob.TryCreate(process);

            if (Unix.Value is not { } wrapper) return null;

            // Verify rather than assume: if the wrapper failed to create the group, pid would still be in our own group and
            // killing "its" group would mean killing ourselves. Process.Start returns once the wrapper has exec'd, but the
            // wrapper calls setpgrp a moment later, so poll briefly instead of reading once.
            var pid = process.Id;
            var deadline = DateTime.UtcNow + AttachTimeout;

            while (true)
            {
                if (getpgid(pid) == pid) return new UnixGroup(pid);

                if (process.HasExited) break;

                if (DateTime.UtcNow > deadline) return null;

                await Task.Delay(2);
            }

            // The command finished before the group could be observed. The wrapper exits with its own code, without exec'ing
            // anything, when it could not create the group; any other exit means the group existed with pgid == pid.
            if (wrapper.IsPerl && process.ExitCode == WrapperFailureExitCode) return null;

            return new UnixGroup(pid);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException
                                              or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static UnixWrapper? ResolveUnixWrapper()
    {
        if (OperatingSystem.IsWindows()) return null;

        foreach (var candidate in SetsidCandidates)
            if (File.Exists(candidate)) return new UnixWrapper(candidate, false);

        foreach (var candidate in PerlCandidates)
            if (File.Exists(candidate)) return new UnixWrapper(candidate, true);

        return null;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);

    [DllImport("libc", SetLastError = true)]
    private static extern int killpg(int pgrp, int sig);

    private sealed record UnixWrapper(string Path, bool IsPerl);

    /// <summary>The group of one step; <see cref="TerminateAsync" /> is called once the step's shell has exited or been killed.</summary>
    public abstract class ProcessGroupHandle : IDisposable
    {
        /// <summary>
        ///     Terminates whatever is still alive in the group: a polite signal first, then a forced kill once the grace period
        ///     passes. Returns true when anything was still running, so the build log can say so.
        /// </summary>
        public abstract Task<bool> TerminateAsync(TimeSpan grace);

        public virtual void Dispose()
        {
        }
    }

    private sealed class UnixGroup(int pgid) : ProcessGroupHandle
    {
        public override async Task<bool> TerminateAsync(TimeSpan grace)
        {
            if (!Signal(Sigterm)) return false;

            var deadline = DateTime.UtcNow + grace;

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);

                // Signal 0 only checks whether the group still has members; ESRCH means it is empty.
                if (!Signal(0)) return true;
            }

            Signal(Sigkill);
            return true;
        }

        /// <summary>True when the group still had members to signal; ESRCH means it is already empty.</summary>
        private bool Signal(int signal)
        {
            // The pgid number stays reserved by the kernel while any member exists (Linux and XNU both refuse to hand a
            // live pgid out as a new pid), so a hit here is always our own group and never a stranger that reused the id.
            if (killpg(pgid, signal) == 0) return true;

            _ = Marshal.GetLastWin32Error() == Esrch;
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsJob : ProcessGroupHandle
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private IntPtr _job;

        private WindowsJob(IntPtr job)
        {
            _job = job;
        }

        public static WindowsJob? TryCreate(Process process)
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return null;

            var limits = new JobObjectExtendedLimitInformationData
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformation,
                    ref limits,
                    Marshal.SizeOf<JobObjectExtendedLimitInformationData>()) ||
                !AssignProcessToJobObject(job, process.Handle))
            {
                CloseHandle(job);
                return null;
            }

            return new WindowsJob(job);
        }

        public override Task<bool> TerminateAsync(TimeSpan grace)
        {
            if (_job == IntPtr.Zero) return Task.FromResult(false);

            // A job object has no "is anything left" query that is cheaper than terminating; termination of an empty job is a no-op.
            var terminated = TerminateJobObject(_job, 1);
            return Task.FromResult(terminated);
        }

        public override void Dispose()
        {
            if (_job == IntPtr.Zero) return;

            CloseHandle(_job);
            _job = IntPtr.Zero;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            ref JobObjectExtendedLimitInformationData information,
            int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformationData
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
