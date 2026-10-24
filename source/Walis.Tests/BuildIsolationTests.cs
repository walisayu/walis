using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Walis.Api.Services;
using Walis.Core.Backup;
using Walis.Core.Execution;
using Walis.Core.Models;

namespace Walis.Tests;

public class BuildIsolationTests
{
    [Fact]
    public void ReopeningDeniedWorkspaceRequiresAnExplicitAdministratorAllowlist()
    {
        var root = Directory.CreateTempSubdirectory("walis-workspace-policy-");
        try
        {
            var install = Directory.CreateDirectory(Path.Combine(root.FullName, "install"));
            var workspace = Directory.CreateDirectory(Path.Combine(install.FullName, "wwwroot"));
            var data = Directory.CreateDirectory(Path.Combine(root.FullName, "data"));
            var policy = new BuildIsolationPolicy(new DataDirectory(data.FullName),
                new StubEnvironment(install.FullName), new ConfigurationBuilder().Build());
            var settings = new WalisSettings();
            var job = new Job();
            var options = new BuildExecutionOptions(workspace.FullName, "/bin/sh", new Dictionary<string, string>(), [], true)
            {
                ProtectedPaths = policy.DeniedPaths(settings).ToArray(),
                RequireWorkspaceAllowlist = policy.RequiresWorkspaceAllowlist(settings, job, workspace.FullName)
            };
            Assert.True(options.RequireWorkspaceAllowlist);
            Assert.False(ShellCommandExecutor.TryPreflight(workspace.FullName, options,
                options.RequireWorkspaceAllowlist, out _, out _));
            options = options with { AllowedWorkingDirectories = [workspace.FullName] };
            Assert.True(ShellCommandExecutor.TryPreflight(workspace.FullName, options,
                options.RequireWorkspaceAllowlist, out _, out _));
            // Explicit permission for one workspace does not authorize its sibling.
            var sibling = Directory.CreateDirectory(Path.Combine(install.FullName, "private"));
            Assert.False(ShellCommandExecutor.TryPreflight(sibling.FullName, options, true, out _, out _));
            if (BuildIsolationPolicy.HomeDirectory() is { } home)
                Assert.True(policy.RequiresWorkspaceAllowlist(settings, job, Path.Combine(home, ".ssh")));
            Assert.False(policy.RequiresWorkspaceAllowlist(settings, new Job { TrustedUnsandboxed = true }, workspace.FullName));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void SandboxDeniesActionPacksAndKeepsToolPrefixesReadOnly()
    {
        var root = Directory.CreateTempSubdirectory("walis-tool-prefix-");
        try
        {
            var data = Directory.CreateDirectory(Path.Combine(root.FullName, "data"));
            var packs = Directory.CreateDirectory(Path.Combine(root.FullName, "action-packs"));
            var tools = Directory.CreateDirectory(Path.Combine(root.FullName, "bin"));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Walis:ActionPacks:0"] = packs.FullName })
                .Build();
            var policy = new BuildIsolationPolicy(new DataDirectory(data.FullName),
                new StubEnvironment(root.FullName), configuration);

            // Pack JSON is shell every job using the action runs: no sandboxed build may read or edit it.
            Assert.Contains(policy.SandboxDeniedPaths(new WalisSettings()),
                path => BuildIsolation.PathsEqual(path, packs.FullName));

            if (!OperatingSystem.IsWindows())
            {
                // A PATH directory this account owns is writable (a build could replace git there); one owned by root is not.
                var writable = BuildIsolationPolicy.WritableSearchPathDirectories(
                    string.Join(Path.PathSeparator, tools.FullName, "/usr/bin", Path.Combine(root.FullName, "missing")));
                Assert.Equal([Path.TrimEndingDirectorySeparator(tools.FullName)], writable);
            }

            var profile = BuildIsolation.CreateProfile([], new SandboxProfileOptions { WriteDeniedPaths = [tools.FullName] });
            Assert.NotNull(profile);
            var realTools = BuildIsolation.RealPath(tools.FullName);
            Assert.Contains($"(deny file-write* (subpath \"{realTools}\"))", profile);
            Assert.DoesNotContain($"(deny file-read* file-write* (subpath \"{realTools}\"))", profile);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void ProcessControlAndNetworkRulesAreAddedOnRequest()
    {
        var pathsOnly = BuildIsolation.CreateProfile([Path.GetTempPath()]);
        Assert.DoesNotContain("(deny signal", pathsOnly);
        Assert.DoesNotContain("(deny network", pathsOnly);

        var hardened = BuildIsolation.CreateProfile([Path.GetTempPath()], new SandboxProfileOptions { DenyNetwork = true });
        Assert.Contains("(deny signal (target others))", hardened);
        Assert.Contains("(deny mach-lookup)", hardened);
        Assert.Contains("(deny process-info* (target others))", hardened);
        Assert.Contains("(deny network*)", hardened);

        // sysctl-read stops even the shell from starting; the profile must never contain it.
        Assert.DoesNotContain("sysctl", hardened);

        var relaxed = BuildIsolation.CreateProfile([Path.GetTempPath()], new SandboxProfileOptions
        {
            DenyProcessControl = false
        });
        Assert.DoesNotContain("(deny signal", relaxed);

        // With nothing to deny there is no profile, unless one of the extra rules asks for one.
        Assert.Null(BuildIsolation.CreateProfile([], new SandboxProfileOptions { DenyProcessControl = false }));
        Assert.NotNull(BuildIsolation.CreateProfile([], new SandboxProfileOptions { DenyNetwork = true }));
    }

    [Fact]
    public void WorkspaceIsAllowedAfterEveryDenial()
    {
        var root = Directory.CreateTempSubdirectory("walis-profile");

        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "ws"));
            var profile = BuildIsolation.CreateProfile([root.FullName], new SandboxProfileOptions
            {
                AllowedWorkspace = workspace.FullName
            })!;

            var deny = profile.IndexOf("(deny file-read* file-write*", StringComparison.Ordinal);
            var allow = profile.IndexOf("(allow file-read* file-write*", StringComparison.Ordinal);

            // Later rules win in sandbox profiles, so the allow must come last.
            Assert.True(deny >= 0 && allow > deny, profile);
            Assert.Contains(BuildIsolation.RealPath(workspace.FullName), profile);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void SandboxDeniesInstallationAndHomeButHostIoProtectsOnlyStorage()
    {
        var root = Directory.CreateTempSubdirectory("walis-install");

        try
        {
            var prefix = Directory.CreateDirectory(Path.Combine(root.FullName, "walis"));
            var release = Directory.CreateDirectory(Path.Combine(prefix.FullName, "releases", "20261021-120000"));
            Directory.CreateDirectory(Path.Combine(prefix.FullName, "var"));
            var data = Directory.CreateDirectory(Path.Combine(prefix.FullName, "data"));
            var current = Path.Combine(prefix.FullName, "current");

            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(current);
            else
                Directory.CreateSymbolicLink(current, release.FullName);

            var policy = new BuildIsolationPolicy(
                new DataDirectory(data.FullName),
                new StubEnvironment(current),
                new ConfigurationBuilder().Build());
            var settings = new WalisSettings();

            var sandbox = policy.SandboxDeniedPaths(settings).Select(Normalize).ToList();
            var storage = policy.DeniedPaths(settings).Select(Normalize).ToList();

            Assert.Contains(Normalize(current), sandbox);
            Assert.Contains(Normalize(Path.Combine(prefix.FullName, "var")), sandbox);
            Assert.Contains(Normalize(data.FullName), sandbox);

            if (!OperatingSystem.IsWindows())
            {
                Assert.Contains(Normalize(Path.Combine(prefix.FullName, "releases")), sandbox);
                var home = BuildIsolationPolicy.HomeDirectory();
                Assert.NotNull(home);
                Assert.Contains(Normalize(home), sandbox);

                // Workspaces legitimately live under the home directory and inside the installation; the host-side overlap
                // check must not refuse them, only the sandbox denies and then re-allows the workspace itself.
                Assert.DoesNotContain(Normalize(home), storage);
            }

            Assert.DoesNotContain(Normalize(current), storage);
            Assert.Contains(Normalize(data.FullName), storage);

            // A trusted job gets no profile at all; an ordinary job gets one on platforms with a sandbox.
            Assert.Null(policy.CreateProfile(settings, new Job { TrustedUnsandboxed = true }, release.FullName));

            if (BuildIsolation.IsSandboxAvailable)
            {
                var profile = policy.CreateProfile(settings, new Job { DenyNetwork = true }, release.FullName);
                Assert.Contains("(deny network*)", profile);
                Assert.Contains("(deny mach-lookup)", profile);
                Assert.DoesNotContain("(deny mach-lookup)",
                    policy.CreateProfile(new WalisSettings { SandboxDenyProcessControl = false }, null, null));
            }
        }
        finally
        {
            root.Delete(true);
        }

        static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    [Fact]
    public void InstallationPathsOutsideTheReleaseLayoutStopAtTheContentRoot()
    {
        var root = Directory.CreateTempSubdirectory("walis-content");

        try
        {
            var paths = BuildIsolationPolicy.InstallationPaths(root.FullName).ToList();
            Assert.Single(paths);
            Assert.Equal(Path.TrimEndingDirectorySeparator(root.FullName), paths[0]);
            Assert.Empty(BuildIsolationPolicy.InstallationPaths(""));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task SandboxWithProcessControlRulesStillRunsAShellThatKillsItsOwnChild()
    {
        if (!BuildIsolation.IsSandboxAvailable) return;

        var directory = Directory.CreateTempSubdirectory("walis-sandbox-pc");

        try
        {
            var job = new Job { Id = Guid.NewGuid(), Name = "程序控制", TimeoutMinutes = 1, WorkingDirectory = directory.FullName };
            var options = new BuildExecutionOptions(
                directory.FullName, null, new Dictionary<string, string>(), [], false)
            {
                SandboxProfile = BuildIsolation.CreateProfile([Path.Combine(directory.FullName, "denied")],
                    new SandboxProfileOptions { DenyNetwork = true })
            };

            var result = await new ShellCommandExecutor().ExecuteAsync(
                job, "sleep 5 & child=$!; kill \"$child\" && echo killed-own-child; /bin/bash -c 'echo bash-ok'",
                options, null, CancellationToken.None);

            Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
            Assert.Contains("killed-own-child", result.Completion.Log);
            Assert.Contains("bash-ok", result.Completion.Log);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task SandboxAllowsTheWorkspaceUnderADeniedDirectory()
    {
        if (!BuildIsolation.IsSandboxAvailable) return;

        var denied = Directory.CreateTempSubdirectory("walis-sandbox-home");

        try
        {
            var secret = Path.Combine(denied.FullName, "id_ed25519");
            await File.WriteAllTextAsync(secret, "假裝是私鑰");
            var workspace = Directory.CreateDirectory(Path.Combine(denied.FullName, "Projects", "app"));
            var job = new Job { Id = Guid.NewGuid(), Name = "家目錄下的工作目錄", TimeoutMinutes = 1, WorkingDirectory = workspace.FullName };
            var options = new BuildExecutionOptions(
                workspace.FullName, null, new Dictionary<string, string>(), [], false)
            {
                SandboxProfile = BuildIsolation.CreateProfile([denied.FullName], new SandboxProfileOptions
                {
                    AllowedWorkspace = workspace.FullName
                })
            };

            var inside = await new ShellCommandExecutor().ExecuteAsync(
                job, "echo built > output.txt && cat output.txt", options, null, CancellationToken.None);
            Assert.Equal(BuildStatus.Succeeded, inside.Completion.Status);
            Assert.True(File.Exists(Path.Combine(workspace.FullName, "output.txt")));

            var outside = await new ShellCommandExecutor().ExecuteAsync(
                job, $"cat '{secret}'", options, null, CancellationToken.None);
            Assert.Equal(BuildStatus.Failed, outside.Completion.Status);
            Assert.Contains("Operation not permitted", outside.Completion.Log);
        }
        finally
        {
            denied.Delete(true);
        }
    }

    [Fact]
    public async Task TrustedJobLogSaysTheSandboxWasSkippedOnPurpose()
    {
        var job = new Job { Id = Guid.NewGuid(), Name = "信任任務", TimeoutMinutes = 1 };
        var options = new BuildExecutionOptions(
            Path.GetTempPath(), null, new Dictionary<string, string>(), [], false)
        {
            TrustedUnsandboxed = true
        };

        var result = await new ShellCommandExecutor().ExecuteAsync(job, "echo hi", options, null, CancellationToken.None);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.Contains(ShellCommandExecutor.TrustedUnsandboxedNotice, result.Completion.Log);
        Assert.DoesNotContain("未套用沙箱", result.Completion.Log);
    }

    private sealed class StubEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Walis.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void FreshInstallRunsBuildsWhereNoSandboxExists()
    {
        // The default keeps the sandbox on where the platform has one, but does not refuse a platform that has none.
        var defaults = new WalisSettings();

        Assert.True(defaults.IsolateBuilds);
        Assert.False(BuildIsolationPolicy.RequiresSandbox(defaults));
    }

    [Fact]
    public void RequiringIsolationIsOneSwitchIndependentOfTheSandboxSwitch()
    {
        Assert.True(BuildIsolationPolicy.RequiresSandbox(new WalisSettings
        {
            IsolateBuilds = true,
            AllowUnisolatedBuilds = false
        }));
        Assert.True(BuildIsolationPolicy.RequiresSandbox(new WalisSettings
        {
            IsolateBuilds = false,
            AllowUnisolatedBuilds = false
        }));
        Assert.False(BuildIsolationPolicy.RequiresSandbox(new WalisSettings
        {
            IsolateBuilds = false,
            AllowUnisolatedBuilds = true
        }));
    }

    [Fact]
    public void SandboxProfileResolvesSymbolicLinksToRealPaths()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var profile = BuildIsolation.CreateProfile(["/tmp"]);

        // /tmp is a symbolic link to /private/tmp; a rule written only with the former does not apply, so both must be listed.
        Assert.Contains("/private/tmp", profile);
        Assert.Contains("deny file-read*", profile);
    }

    [Fact]
    public void NoProfileWhenNothingToProtect()
    {
        Assert.Null(BuildIsolation.CreateProfile([]));
        Assert.Null(BuildIsolation.CreateProfile(["", "   "]));
    }

    [Fact]
    public void WorkspaceAllowlistPermitsOnlyPathsBeneathIt()
    {
        var root = Directory.CreateTempSubdirectory("walis-allow");
        var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "專案"));

        try
        {
            List<string> allowed = [root.FullName];

            Assert.True(BuildIsolation.IsWorkingDirectoryAllowed(root.FullName, allowed));
            Assert.True(BuildIsolation.IsWorkingDirectoryAllowed(nested.FullName, allowed));
            Assert.False(BuildIsolation.IsWorkingDirectoryAllowed(Path.GetTempPath(), allowed));

            // An empty allowlist means no restriction.
            Assert.True(BuildIsolation.IsWorkingDirectoryAllowed(Path.GetTempPath(), []));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void WorkspaceMustNotContainOrSitInsideProtectedStoragePaths()
    {
        var root = Directory.CreateTempSubdirectory("walis-protected");

        try
        {
            var data = Directory.CreateDirectory(Path.Combine(root.FullName, "data"));
            var work = Directory.CreateDirectory(Path.Combine(root.FullName, "work"));

            Assert.False(BuildIsolation.IsDisjointFrom(root.FullName, [data.FullName]));
            Assert.False(BuildIsolation.IsDisjointFrom(data.FullName, [root.FullName]));
            Assert.True(BuildIsolation.IsDisjointFrom(work.FullName, [data.FullName]));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void MacOsProtectedPathComparisonIsConservativelyCaseInsensitive()
    {
        if (!OperatingSystem.IsMacOS()) return;

        Assert.True(BuildIsolation.PathsOverlap("/USR/LOCAL/WALIS", "/usr/local/walis/data"));
    }

    [Fact]
    public void WorkspaceWithSymbolicLinkInsideAllowlistIsStillRejected()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("walis-allow-link");

        try
        {
            var real = Directory.CreateDirectory(Path.Combine(root.FullName, "real"));
            var nested = Directory.CreateDirectory(Path.Combine(real.FullName, "project"));
            var link = Path.Combine(root.FullName, "link");
            Directory.CreateSymbolicLink(link, real.FullName);

            var accepted = BuildIsolation.TryResolveWorkingDirectory(
                Path.Combine(link, nested.Name),
                [root.FullName],
                true,
                out _,
                out var reason);

            Assert.False(accepted);
            Assert.Contains("符號連結", reason);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task SandboxReallyBlocksReadingDataDirectory()
    {
        if (!BuildIsolation.IsSandboxAvailable) return;

        var directory = Directory.CreateTempSubdirectory("walis-sandbox");

        try
        {
            var secret = Path.Combine(directory.FullName, "walis.db");
            await File.WriteAllTextAsync(secret, "假裝是資料庫");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "沙箱測試",
                TimeoutMinutes = 1,
                WorkingDirectory = Path.GetTempPath()
            };

            var options = new BuildExecutionOptions(
                Path.GetTempPath(), null, new Dictionary<string, string>(), [], false)
            {
                SandboxProfile = BuildIsolation.CreateProfile([directory.FullName])
            };

            var result = await new ShellCommandExecutor().ExecuteAsync(
                job, $"cat '{secret}'", options, null, CancellationToken.None);

            Assert.Equal(BuildStatus.Failed, result.Completion.Status);
            Assert.Contains("Operation not permitted", result.Completion.Log);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task FailsOutrightWhenWorkspaceNotInAllowlist()
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Name = "白名單測試",
            TimeoutMinutes = 1,
            WorkingDirectory = Path.GetTempPath()
        };

        var options = new BuildExecutionOptions(
            null, null, new Dictionary<string, string>(), [], false)
        {
            AllowedWorkingDirectories = ["/usr/local/walis/workspace"]
        };

        var result =
            await new ShellCommandExecutor().ExecuteAsync(job, "echo hi", options, null, CancellationToken.None);

        Assert.Equal(BuildStatus.Failed, result.Completion.Status);
        Assert.Contains("不在系統設定允許的範圍內", result.Completion.Log);
    }

    [Fact]
    public async Task DoesNotRunCommandWhenSandboxRequiredButNoProfile()
    {
        var directory = Directory.CreateTempSubdirectory("walis-fail-closed");

        try
        {
            var marker = Path.Combine(directory.FullName, "不該建立");
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "隔離必須存在",
                TimeoutMinutes = 1,
                WorkingDirectory = directory.FullName
            };
            var command = OperatingSystem.IsWindows()
                ? $"type nul > \"{marker}\""
                : $"touch '{marker}'";
            var options = new BuildExecutionOptions(
                directory.FullName, null, new Dictionary<string, string>(), [], false)
            {
                RequireSandbox = true
            };

            var result = await new ShellCommandExecutor().ExecuteAsync(
                job, command, options, null, CancellationToken.None);

            Assert.Equal(BuildStatus.Failed, result.Completion.Status);
            Assert.Contains("拒絕執行", result.Completion.Log);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task BuildCannotReadServiceConnectionString()
    {
        if (OperatingSystem.IsWindows()) return;

        Environment.SetEnvironmentVariable("ConnectionStrings__Walis", "Data Source=/usr/local/walis/data/walis.db");

        try
        {
            var job = new Job { Id = Guid.NewGuid(), Name = "環境變數", TimeoutMinutes = 1 };
            var options = new BuildExecutionOptions(
                Path.GetTempPath(), null, new Dictionary<string, string>(), [], false);

            var result = await new ShellCommandExecutor().ExecuteAsync(
                job, "echo \"[${ConnectionStrings__Walis:-沒有這個變數}]\"", options, null, CancellationToken.None);

            Assert.Contains("沒有這個變數", result.Completion.Log);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Walis", null);
        }
    }

    [Fact]
    public async Task BuildDoesNotInheritUninjectedServiceEnvironment()
    {
        if (OperatingSystem.IsWindows()) return;

        var name = $"WALIS_TEST_PARENT_SECRET_{Guid.NewGuid():N}";
        var lowercaseLocaleName = $"lc_walis_test_parent_secret_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "parent-secret");
        Environment.SetEnvironmentVariable(lowercaseLocaleName, "lowercase-parent-secret");

        try
        {
            var job = new Job { Id = Guid.NewGuid(), Name = "環境白名單", TimeoutMinutes = 1 };
            var options = new BuildExecutionOptions(
                Path.GetTempPath(), null, new Dictionary<string, string>(), [], false);

            var result = await new ShellCommandExecutor().ExecuteAsync(
                job,
                $"printf '[%s][%s]' \"${{{name}:-missing}}\" \"${{{lowercaseLocaleName}:-missing}}\"",
                options,
                null,
                CancellationToken.None);

            Assert.Contains("[missing][missing]", result.Completion.Log);
            Assert.DoesNotContain("parent-secret", result.Completion.Log);
            Assert.DoesNotContain("lowercase-parent-secret", result.Completion.Log);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable(lowercaseLocaleName, null);
        }
    }
}
