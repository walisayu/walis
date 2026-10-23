using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Walis.Api.Services;
using Walis.Core.Execution;
using Walis.Core.Models;

namespace Walis.Tests;

public class ManagedWorkspaceTests
{
    [Fact]
    public void RootIsCreatedPrivateAndRefusesALink()
    {
        var directory = Directory.CreateTempSubdirectory("walis-ws-root");

        try
        {
            var root = ManagedWorkspaces.EnsureRoot(Path.Combine(directory.FullName, "workspaces"));
            Assert.True(Directory.Exists(root));

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(root));

                var elsewhere = Directory.CreateDirectory(Path.Combine(directory.FullName, "elsewhere")).FullName;
                var link = Path.Combine(directory.FullName, "linked");
                File.CreateSymbolicLink(link, elsewhere);
                Assert.Throws<IOException>(() => ManagedWorkspaces.EnsureRoot(link));
            }

            var jobId = Guid.NewGuid();
            var workspace = ManagedWorkspaces.EnsureJobWorkspace(root, jobId);
            Assert.Equal(Path.Combine(root, jobId.ToString("N")), workspace);
            Assert.True(Directory.Exists(workspace));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void ReclaimRemovesOnlyWorkspacesOfMissingJobs()
    {
        var directory = Directory.CreateTempSubdirectory("walis-ws-reclaim");

        try
        {
            var root = ManagedWorkspaces.EnsureRoot(Path.Combine(directory.FullName, "workspaces"));
            var live = Guid.NewGuid();
            var gone = Guid.NewGuid();

            var liveWorkspace = ManagedWorkspaces.EnsureJobWorkspace(root, live);
            File.WriteAllText(Path.Combine(liveWorkspace, "keep.txt"), "keep");
            var goneWorkspace = ManagedWorkspaces.EnsureJobWorkspace(root, gone);
            File.WriteAllText(Path.Combine(goneWorkspace, "stale.txt"), "stale");

            // Names that are not job ids are never touched, whatever they contain.
            var foreign = Directory.CreateDirectory(Path.Combine(root, "not-a-job")).FullName;
            File.WriteAllText(Path.Combine(foreign, "x.txt"), "x");

            string? linkTarget = null;

            if (!OperatingSystem.IsWindows())
            {
                // A link planted under a dead job id is removed as a link; its target stays intact.
                linkTarget = Directory.CreateDirectory(Path.Combine(directory.FullName, "target")).FullName;
                File.WriteAllText(Path.Combine(linkTarget, "precious.txt"), "precious");
                Directory.CreateSymbolicLink(Path.Combine(root, Guid.NewGuid().ToString("N")), linkTarget);
            }

            var removed = ManagedWorkspaces.Reclaim(root, new HashSet<Guid> { live });

            Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, removed);
            Assert.True(File.Exists(Path.Combine(liveWorkspace, "keep.txt")));
            Assert.False(Directory.Exists(goneWorkspace));
            Assert.True(Directory.Exists(foreign));

            if (linkTarget is not null) Assert.True(File.Exists(Path.Combine(linkTarget, "precious.txt")));

            // Running again finds nothing more to do.
            Assert.Equal(0, ManagedWorkspaces.Reclaim(root, new HashSet<Guid> { live }));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void SandboxProfileAllowsTheWorkspaceAfterDenyingTheDataDirectory()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var directory = Directory.CreateTempSubdirectory("walis-ws-profile");

        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(directory.FullName, "workspaces", "job")).FullName;
            var profile = BuildIsolation.CreateProfile([directory.FullName], [workspace])!;
            var real = BuildIsolation.RealPath(workspace);

            var deny = profile.IndexOf("(deny file-read* file-write*", StringComparison.Ordinal);
            var allow = profile.IndexOf($"(allow file-read* file-write* (subpath \"{real}\"))", StringComparison.Ordinal);

            // SBPL: the last matching rule wins, so the allow must follow the deny.
            Assert.True(deny >= 0);
            Assert.True(allow > deny);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void ProtectedPathsSwapTheDataRootForItsOtherEntries()
    {
        var directory = Directory.CreateTempSubdirectory("walis-ws-protected");

        try
        {
            var data = Directory.CreateDirectory(Path.Combine(directory.FullName, "data")).FullName;
            var workspaces = Directory.CreateDirectory(Path.Combine(data, "workspaces")).FullName;
            Directory.CreateDirectory(Path.Combine(data, "artifacts"));
            File.WriteAllText(Path.Combine(data, "walis.db"), string.Empty);
            var extra = Directory.CreateDirectory(Path.Combine(directory.FullName, "denied-elsewhere")).FullName;

            var policy = new BuildIsolationPolicy(
                new DataDirectory(data),
                new FakeEnvironment(directory.FullName),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Walis"] = $"Data Source={Path.Combine(data, "walis.db")}"
                }).Build());
            var settings = new WalisSettings { SandboxDeniedPaths = extra };

            var protectedPaths = policy.ProtectedPathsForManagedWorkspace(settings, workspaces);
            var workspace = Path.Combine(workspaces, Guid.NewGuid().ToString("N"));

            // The database, artifacts and the extra path are protected; the data root itself is not listed, so the workspace passes.
            Assert.Contains(protectedPaths, path => BuildIsolation.PathsEqual(path, Path.Combine(data, "walis.db")));
            Assert.Contains(protectedPaths, path => BuildIsolation.PathsEqual(path, Path.Combine(data, "artifacts")));
            Assert.Contains(protectedPaths, path => BuildIsolation.PathsEqual(path, extra));
            Assert.DoesNotContain(protectedPaths, path => BuildIsolation.PathsEqual(path, data));
            Assert.DoesNotContain(protectedPaths, path => BuildIsolation.PathsEqual(path, workspaces));
            Assert.True(BuildIsolation.IsDisjointFrom(workspace, protectedPaths));

            // Without the carve-out the same workspace is rejected, which is why the swap exists.
            Assert.False(BuildIsolation.IsDisjointFrom(workspace, policy.DeniedPaths(settings)));

            // A workspace pointed at the database directory is still refused.
            Assert.False(BuildIsolation.IsDisjointFrom(Path.Combine(data, "artifacts", "x"), protectedPaths));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class FakeEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Walis.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
