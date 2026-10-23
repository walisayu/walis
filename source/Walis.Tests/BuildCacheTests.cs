using System.Diagnostics;
using Walis.Core.Execution;

namespace Walis.Tests;

public class BuildCacheTests
{
    [Fact]
    public void AcceptsOnlyRelativePathsUnderWorkspace()
    {
        Assert.True(BuildCache.IsSafe("node_modules"));
        Assert.True(BuildCache.IsSafe("build/deps"));

        // These forms would write outside the workspace.
        Assert.False(BuildCache.IsSafe("/etc"));
        Assert.False(BuildCache.IsSafe("~/.npm"));
        Assert.False(BuildCache.IsSafe("../../別人的專案"));
        Assert.False(BuildCache.IsSafe("build/../../外面"));
    }

    [Fact]
    public void ParsingSkipsCommentsAndDangerousPaths()
    {
        var paths = BuildCache.ParsePaths("node_modules\n# 註解\n/etc\n\nbuild/deps/\n../外面");

        Assert.Equal(["node_modules", "build/deps"], paths);
    }

    [Fact]
    public void SavedCacheRestoresIntoNewWorkspace()
    {
        var root = Directory.CreateTempSubdirectory("walis-cache");

        try
        {
            var cache = Path.Combine(root.FullName, "cache");
            var first = Directory.CreateDirectory(Path.Combine(root.FullName, "第一次"));
            var nested = Directory.CreateDirectory(Path.Combine(first.FullName, "node_modules", "套件"));

            File.WriteAllText(Path.Combine(nested.FullName, "index.js"), "內容");

            var saved = BuildCache.Save(cache, first.FullName, ["node_modules"]);
            Assert.Equal(1, saved);

            var second = Directory.CreateDirectory(Path.Combine(root.FullName, "第二次"));
            var restored = BuildCache.Restore(cache, second.FullName, ["node_modules"]);

            Assert.Equal(1, restored);
            Assert.Equal("內容", File.ReadAllText(Path.Combine(second.FullName, "node_modules", "套件", "index.js")));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void ExistingDirectoryIsNotOverwritten()
    {
        var root = Directory.CreateTempSubdirectory("walis-cache");

        try
        {
            var cache = Path.Combine(root.FullName, "cache");
            var work = Directory.CreateDirectory(Path.Combine(root.FullName, "work"));

            var cachedPath = Path.Combine(cache, BuildCache.CacheKey("node_modules"));
            Directory.CreateDirectory(cachedPath);
            File.WriteAllText(Path.Combine(cachedPath, "舊的.txt"), "快取");

            Directory.CreateDirectory(Path.Combine(work.FullName, "node_modules"));
            File.WriteAllText(Path.Combine(work.FullName, "node_modules", "現有的.txt"), "工作目錄");

            var restored = BuildCache.Restore(cache, work.FullName, ["node_modules"]);

            // Leave existing content alone, so a fresh checkout is not overwritten.
            Assert.Equal(0, restored);
            Assert.False(File.Exists(Path.Combine(work.FullName, "node_modules", "舊的.txt")));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void RestoreDoesNotTraverseSymbolicLinksInWorkspace()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("walis-cache-link");

        try
        {
            var cache = Directory.CreateDirectory(Path.Combine(root.FullName, "cache"));
            var work = Directory.CreateDirectory(Path.Combine(root.FullName, "work"));
            var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "outside"));
            var cached = Directory.CreateDirectory(
                Path.Combine(cache.FullName, BuildCache.CacheKey("redirect/packages")));
            File.WriteAllText(Path.Combine(cached.FullName, "poison.txt"), "不該寫出來");
            Directory.CreateSymbolicLink(Path.Combine(work.FullName, "redirect"), outside.FullName);

            var restored = BuildCache.Restore(cache.FullName, work.FullName, ["redirect/packages"]);

            Assert.Equal(0, restored);
            Assert.False(File.Exists(Path.Combine(outside.FullName, "packages", "poison.txt")));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void RefusesToSaveWhenCacheRootIsSymbolicLink()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("walis-cache-root-link");

        try
        {
            var work = Directory.CreateDirectory(Path.Combine(root.FullName, "work"));
            var source = Directory.CreateDirectory(Path.Combine(work.FullName, "packages"));
            File.WriteAllText(Path.Combine(source.FullName, "package.txt"), "內容");

            var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "outside"));
            var cacheLink = Path.Combine(root.FullName, "cache-link");
            Directory.CreateSymbolicLink(cacheLink, outside.FullName);

            Assert.Equal(0, BuildCache.Save(cacheLink, work.FullName, ["packages"]));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside.FullName));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void CacheDirectoryNameDoesNotGrowNestedStructure()
    {
        Assert.StartsWith(
            "build_deps--",
            BuildCache.Sanitize("build/deps"),
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            "node_modules--",
            BuildCache.Sanitize("node_modules"),
            StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(BuildCache.CacheKey("foo/bar"), BuildCache.CacheKey("foo_bar"));
    }

    [Fact]
    public void ExceedingAnyQuotaPublishesNoPartialCacheAndKeepsOldVersion()
    {
        var root = Directory.CreateTempSubdirectory("walis-cache-quota");

        try
        {
            var cache = Path.Combine(root.FullName, "cache");
            var work = Directory.CreateDirectory(Path.Combine(root.FullName, "work"));
            var packages = Directory.CreateDirectory(Path.Combine(work.FullName, "packages"));
            File.WriteAllText(Path.Combine(packages.FullName, "a.txt"), "old");
            Assert.Equal(1, BuildCache.Save(cache, work.FullName, ["packages"]));

            File.WriteAllText(Path.Combine(packages.FullName, "a.txt"), "new-value-too-large");
            File.WriteAllText(Path.Combine(packages.FullName, "b.txt"), "second");
            var tiny = new BuildCacheQuota(
                1,
                5,
                5,
                2);

            Assert.Equal(0, BuildCache.Save(cache, work.FullName, ["packages"], quota: tiny));

            var published = Path.Combine(cache, BuildCache.CacheKey("packages"));
            Assert.Equal("old", File.ReadAllText(Path.Combine(published, "a.txt")));
            Assert.False(File.Exists(Path.Combine(published, "b.txt")));
            Assert.Empty(Directory.EnumerateDirectories(cache, ".walis-cache-save-*"));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void DestinationStaysAbsentWhenRestoreExceedsQuota()
    {
        var root = Directory.CreateTempSubdirectory("walis-cache-restore-quota");

        try
        {
            var cache = Directory.CreateDirectory(Path.Combine(root.FullName, "cache"));
            var cached = Directory.CreateDirectory(Path.Combine(cache.FullName, BuildCache.CacheKey("packages")));
            File.WriteAllText(Path.Combine(cached.FullName, "a.txt"), "1234");
            File.WriteAllText(Path.Combine(cached.FullName, "b.txt"), "5678");
            var work = Directory.CreateDirectory(Path.Combine(root.FullName, "work"));
            var tiny = new BuildCacheQuota(
                1,
                5,
                5,
                10);

            Assert.Equal(0, BuildCache.Restore(
                cache.FullName,
                work.FullName,
                ["packages"],
                quota: tiny));
            Assert.False(Directory.Exists(Path.Combine(work.FullName, "packages")));
            Assert.Empty(Directory.EnumerateDirectories(work.FullName, ".walis-cache-restore-*"));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void FileCountSingleFileTotalAndEntryLimitsEachRejectWholeCache()
    {
        var root = Directory.CreateTempSubdirectory("walis-cache-all-quotas");

        try
        {
            var work = Directory.CreateDirectory(Path.Combine(root.FullName, "work"));
            var cache = Path.Combine(root.FullName, "cache");

            var fileCount = Directory.CreateDirectory(Path.Combine(work.FullName, "file-count"));
            File.WriteAllText(Path.Combine(fileCount.FullName, "a"), string.Empty);
            File.WriteAllText(Path.Combine(fileCount.FullName, "b"), string.Empty);
            Assert.Equal(0, BuildCache.Save(
                cache,
                work.FullName,
                ["file-count"],
                quota: new BuildCacheQuota(1, 100, 100, 10)));

            var single = Directory.CreateDirectory(Path.Combine(work.FullName, "single"));
            File.WriteAllText(Path.Combine(single.FullName, "large"), "123456");
            Assert.Equal(0, BuildCache.Save(
                cache,
                work.FullName,
                ["single"],
                quota: new BuildCacheQuota(10, 100, 5, 10)));

            var total = Directory.CreateDirectory(Path.Combine(work.FullName, "total"));
            File.WriteAllText(Path.Combine(total.FullName, "a"), "1234");
            File.WriteAllText(Path.Combine(total.FullName, "b"), "5678");
            Assert.Equal(0, BuildCache.Save(
                cache,
                work.FullName,
                ["total"],
                quota: new BuildCacheQuota(10, 5, 5, 10)));

            var entries = Directory.CreateDirectory(Path.Combine(work.FullName, "entries"));
            Directory.CreateDirectory(Path.Combine(entries.FullName, "a"));
            Directory.CreateDirectory(Path.Combine(entries.FullName, "b"));
            Assert.Equal(0, BuildCache.Save(
                cache,
                work.FullName,
                ["entries"],
                quota: new BuildCacheQuota(10, 100, 100, 1)));

            foreach (var path in new[] { "file-count", "single", "total", "entries" })
                Assert.False(Directory.Exists(Path.Combine(cache, BuildCache.CacheKey(path))));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task FifoDoesNotBlockCacheSaveForever()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("walis-cache-fifo");

        try
        {
            var work = Directory.CreateDirectory(Path.Combine(root.FullName, "work"));
            var packages = Directory.CreateDirectory(Path.Combine(work.FullName, "packages"));
            var fifo = Path.Combine(packages.FullName, "hang");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/mkfifo",
                UseShellExecute = false,
                ArgumentList = { fifo }
            });
            Assert.NotNull(process);
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);

            var save = Task.Run(() => BuildCache.Save(
                Path.Combine(root.FullName, "cache"),
                work.FullName,
                ["packages"]));

            Assert.Equal(0, await save.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            root.Delete(true);
        }
    }
}