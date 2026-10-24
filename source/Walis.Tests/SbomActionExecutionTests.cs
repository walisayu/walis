using System.Diagnostics;
using Walis.Core.Actions;

namespace Walis.Tests;

public class SbomActionExecutionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("walis-sbom-tests-").FullName;

    [Theory]
    [InlineData(0, "never", 0)]
    [InlineData(1, "never", 0)]
    [InlineData(127, "never", 127)]
    [InlineData(128, "never", 128)]
    [InlineData(1, "all", 1)]
    [InlineData(0, "all", 0)]
    public async Task OsvNeverSuppressesOperationalFailures(int toolExit, string threshold, int expected)
    {
        if (OperatingSystem.IsWindows()) return;
        Tool("osv-scanner", $"exit {toolExit}\n");
        File.WriteAllText(Path.Combine(_root, "bom.json"), "{}");
        var result = await Run("sbom.scan", ("tool", "osv-scanner"), ("file", "bom.json"), ("failOn", threshold));
        Assert.Equal(expected, result.ExitCode);
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("high")]
    [InlineData("medium")]
    public async Task OsvDoesNotSilentlyReplaceASeverityPolicy(string threshold)
    {
        if (OperatingSystem.IsWindows()) return;
        Tool("osv-scanner", "touch invoked\nexit 0\n");
        File.WriteAllText(Path.Combine(_root, "bom.json"), "{}");
        var result = await Run("sbom.scan", ("tool", "osv-scanner"), ("file", "bom.json"), ("failOn", threshold));
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(_root, "invoked")));
    }

    [Fact]
    public async Task GrypeDoesNotSilentlyWeakenTheAllFindingsPolicy()
    {
        if (OperatingSystem.IsWindows()) return;
        Tool("grype", "touch grype-invoked\nexit 0\n");
        Tool("osv-scanner", "touch osv-invoked\nexit 1\n");
        File.WriteAllText(Path.Combine(_root, "bom.json"), "{}");
        var explicitGrype = await Run("sbom.scan", ("tool", "grype"), ("file", "bom.json"), ("failOn", "all"));
        Assert.NotEqual(0, explicitGrype.ExitCode);
        Assert.False(File.Exists(Path.Combine(_root, "grype-invoked")));
        var automatic = await Run("sbom.scan", ("tool", "auto"), ("file", "bom.json"), ("failOn", "all"));
        Assert.Equal(1, automatic.ExitCode);
        Assert.True(File.Exists(Path.Combine(_root, "osv-invoked")));
        Assert.False(File.Exists(Path.Combine(_root, "grype-invoked")));
    }

    [Fact]
    public async Task MicrosoftSbomUsesManifestDirectoryAndCopiesTheRequestedFile()
    {
        if (OperatingSystem.IsWindows()) return;
        Tool("dotnet-sbom", """
            while [ "$#" -gt 0 ]; do
              case "$1" in
                -m) shift; manifest="$1" ;;
                -mi) shift; [ "$1" = 'SPDX:2.2' ] || exit 40 ;;
                -o) exit 41 ;;
              esac
              shift
            done
            [ -n "$manifest" ] || exit 42
            mkdir -p "$manifest/_manifest/spdx_2.2"
            printf '{"spdxVersion":"SPDX-2.2"}' > "$manifest/_manifest/spdx_2.2/manifest.spdx.json"
            """);
        var result = await Run("sbom.generate", ("tool", "dotnet"), ("format", "spdx-2.2-json"), ("path", "."), ("output", "out/result.json"));
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("SPDX-2.2", File.ReadAllText(Path.Combine(_root, "out/result.json")));
    }

    [Theory]
    [InlineData("cyclonedx-json")]
    [InlineData("spdx-json")]
    public async Task MicrosoftSbomRejectsFormatsItCannotGenerate(string format)
    {
        if (OperatingSystem.IsWindows()) return;
        Tool("dotnet-sbom", "touch invoked\nexit 0\n");
        var result = await Run("sbom.generate", ("tool", "dotnet"), ("format", format), ("path", "."), ("output", "result.json"));
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(_root, "invoked")));
        Assert.False(File.Exists(Path.Combine(_root, "result.json")));
    }

    private void Tool(string name, string body)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private async Task<(int ExitCode, string Output)> Run(string action, params (string Name, string Value)[] parameters)
    {
        var compiled = ActionScriptCompiler.Compile([new ActionStep
        {
            Id = "scan", ActionId = action,
            Parameters = parameters.ToDictionary(p => p.Name, p => (string?)p.Value)
        }], BuiltInActions.All.ToDictionary(a => a.Id));
        Assert.True(compiled.IsValid, string.Join("; ", compiled.Errors));
        var script = Path.Combine(_root, "run.sh");
        File.WriteAllText(script, compiled.Script);
        var start = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(script);
        start.Environment.Clear();
        start.Environment["PATH"] = _root + ":/usr/bin:/bin";
        start.Environment["WALIS_OUTPUT_FILE"] = Path.Combine(_root, "outputs");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
