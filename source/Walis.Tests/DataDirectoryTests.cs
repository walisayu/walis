using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Walis.Api.Services;

namespace Walis.Tests;

public class DataDirectoryTests
{
    private static IConfiguration Configuration(string? dataDirectory)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DataDirectory.ConfigurationKey] = dataDirectory })
            .Build();
    }

    [Fact]
    public void DefaultsToDataUnderContentRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "walis-root");
        var resolved = DataDirectory.Resolve(new FakeEnvironment(root), Configuration(null));
        Assert.Equal(Path.Combine(root, "data"), resolved);
    }

    [Fact]
    public void AbsoluteSettingWinsOverContentRoot()
    {
        var shared = Path.Combine(Path.GetTempPath(), "walis-shared");
        var resolved = DataDirectory.Resolve(new FakeEnvironment("/somewhere/else"), Configuration($" {shared} "));
        Assert.Equal(Path.GetFullPath(shared), resolved);
    }

    [Fact]
    public void RelativeSettingIsAnchoredAtContentRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "walis-root");
        var resolved = DataDirectory.Resolve(new FakeEnvironment(root), Configuration("../state"));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "..", "state")), resolved);
    }

    [Fact]
    public void CombineAppendsUnderRoot()
    {
        var data = new DataDirectory("/var/walis/data");
        Assert.Equal(Path.Combine("/var/walis/data", "artifacts", "x"), data.Combine("artifacts", "x"));
    }

    private sealed class FakeEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Walis.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}