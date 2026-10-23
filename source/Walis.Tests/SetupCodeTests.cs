using System.Text.RegularExpressions;
using Walis.Api.Services;

namespace Walis.Tests;

public sealed class SetupCodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"walis-setup-code-{Guid.NewGuid():N}");

    public SetupCodeTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void IssuesAReadableCodeAndSavesItWhereOnlyTheOwnerCanReadIt()
    {
        var setupCode = Create();

        Assert.Null(setupCode.Current);

        var code = setupCode.EnsureCreated();

        // Four groups of five, from an alphabet without the characters that get misread off a terminal.
        Assert.Matches(new Regex("^[A-HJKMNP-TV-Z2-9]{5}(-[A-HJKMNP-TV-Z2-9]{5}){3}$"), code);
        Assert.Equal(code, setupCode.Current);
        Assert.Equal(code, File.ReadAllText(setupCode.FilePath).Trim());

        if (!OperatingSystem.IsWindows())
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(setupCode.FilePath));
    }

    [Fact]
    public void RestartBeforeAnyoneClaimsTheInstallKeepsTheSameCode()
    {
        var code = Create().EnsureCreated();

        Assert.Equal(code, Create().EnsureCreated());
    }

    [Fact]
    public void LeftoverFileThatIsNotACodeIsReplaced()
    {
        File.WriteAllText(Path.Combine(_root, SetupCode.FileName), "not a setup code\n");

        var setupCode = Create();
        var code = setupCode.EnsureCreated();

        Assert.Equal(code, File.ReadAllText(setupCode.FilePath).Trim());
        Assert.True(setupCode.Matches(code));
    }

    [Fact]
    public void AcceptsTheCodeAsTypedAndNothingElse()
    {
        var setupCode = Create();

        Assert.False(setupCode.Matches("ANYTH-INGAT-ALLBE-FORE2"));

        var code = setupCode.EnsureCreated();
        var different = code[0] == 'A' ? 'B' + code[1..] : 'A' + code[1..];

        Assert.True(setupCode.Matches(code));
        Assert.True(setupCode.Matches(code.ToLowerInvariant()));
        Assert.True(setupCode.Matches($"  {code.Replace("-", " ")}  "));
        Assert.True(setupCode.Matches(code.Replace("-", string.Empty)));
        Assert.False(setupCode.Matches(different));
        Assert.False(setupCode.Matches(code[..^1]));
        Assert.False(setupCode.Matches(null));
        Assert.False(setupCode.Matches(string.Empty));
        Assert.False(setupCode.Matches(code + new string('-', 300)));
    }

    [Fact]
    public void ClearingRetiresTheCodeAndRemovesTheFile()
    {
        var setupCode = Create();
        var code = setupCode.EnsureCreated();

        setupCode.Clear();

        Assert.Null(setupCode.Current);
        Assert.False(setupCode.Matches(code));
        Assert.False(File.Exists(setupCode.FilePath));
    }

    private SetupCode Create()
    {
        return new SetupCode(new DataDirectory(_root));
    }
}
