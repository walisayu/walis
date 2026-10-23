extern alias RunnerApp;
using RunnerOptions = RunnerApp::Walis.Runner.RunnerOptions;

namespace Walis.Tests;

public class RunnerOptionsTests
{
    [Fact]
    public void TokenFileSuppliesTheTokenWithoutPuttingItOnTheCommandLine()
    {
        var directory = Directory.CreateTempSubdirectory("walis-runner-token");

        try
        {
            var file = Path.Combine(directory.FullName, "runner.token");
            WritePrivate(file, "walis_abc_secret\n# a trailing comment line is ignored\n");

            Assert.True(RunnerOptions.TryParse(
                ["--server", "https://walis.example.internal", "--name", "pi-01", "--token-file", file],
                out var options, out var error), error);
            Assert.Equal("walis_abc_secret", options!.Token);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void TokenFileAndTokenTogetherAreRefused()
    {
        var directory = Directory.CreateTempSubdirectory("walis-runner-token");

        try
        {
            var file = Path.Combine(directory.FullName, "runner.token");
            WritePrivate(file, "walis_abc_secret");

            Assert.False(RunnerOptions.TryParse(
                ["--server", "https://walis.example.internal", "--token", "other", "--token-file", file],
                out _, out var error));
            Assert.Contains("擇一", error);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void MissingOrEmptyTokenFileIsAnError()
    {
        var directory = Directory.CreateTempSubdirectory("walis-runner-token");

        try
        {
            Assert.False(RunnerOptions.TryParse(
                ["--server", "https://walis.example.internal", "--token-file", Path.Combine(directory.FullName, "none")],
                out _, out var missing));
            Assert.Contains("找不到", missing);

            var empty = Path.Combine(directory.FullName, "empty");
            WritePrivate(empty, "\n");
            Assert.False(RunnerOptions.TryParse(
                ["--server", "https://walis.example.internal", "--token-file", empty],
                out _, out var blank));
            Assert.Contains("空的", blank);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void TokenEnvironmentVariableIsReadThenRemovedFromTheProcess()
    {
        Environment.SetEnvironmentVariable("WALIS_TOKEN", "walis_env_secret");

        try
        {
            Assert.True(RunnerOptions.TryParse(["--server", "https://walis.example.internal"], out var options, out _));
            Assert.Equal("walis_env_secret", options!.Token);

            RunnerOptions.ClearSensitiveEnvironment();
            Assert.Null(Environment.GetEnvironmentVariable("WALIS_TOKEN"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WALIS_TOKEN", null);
        }
    }

    [Fact]
    public void CredentialFileReadableByOthersIsRefused()
    {
        if (OperatingSystem.IsWindows()) return;

        var directory = Directory.CreateTempSubdirectory("walis-runner-token");

        try
        {
            var file = Path.Combine(directory.FullName, "runner.token");
            File.WriteAllText(file, "walis_abc_secret");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            Assert.False(RunnerOptions.TryParse(
                ["--server", "https://walis.example.internal", "--token-file", file],
                out _, out var error));
            Assert.Contains("chmod 600", error);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void PasswordComesFromAFileOrTheEnvironmentAndIsRemovedFromTheProcess()
    {
        var directory = Directory.CreateTempSubdirectory("walis-runner-password");

        try
        {
            var file = Path.Combine(directory.FullName, "runner.password");
            WritePrivate(file, "correct horse battery\n");

            Assert.True(RunnerOptions.TryParse(
                ["--server", "https://walis.example.internal", "--user", "admin", "--password-file", file],
                out var fromFile, out var error), error);
            Assert.Equal("correct horse battery", fromFile!.Password);

            Environment.SetEnvironmentVariable("WALIS_PASSWORD", "from-environment");
            Assert.True(RunnerOptions.TryParse(
                ["--server", "https://walis.example.internal", "--user", "admin"],
                out var fromEnvironment, out error), error);
            Assert.Equal("from-environment", fromEnvironment!.Password);

            Assert.False(RunnerOptions.TryParse(
                ["--server", "https://walis.example.internal", "--user", "admin", "--password-file", file],
                out _, out error));
            Assert.Contains("擇一", error);

            RunnerOptions.ClearSensitiveEnvironment();
            Assert.Null(Environment.GetEnvironmentVariable("WALIS_PASSWORD"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WALIS_PASSWORD", null);
            directory.Delete(true);
        }
    }

    private static void WritePrivate(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
