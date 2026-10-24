namespace Walis.Api.Services;

/// <summary>
///     Deployment secrets arrive through the process environment (the plist, a systemd unit, a compose file). The configuration
///     providers copy them at build time, so after <c>builder.Build()</c> the environment copy is only a liability: a build
///     running under the same account can read it back with <c>ps -E</c> (macOS) or <c>/proc/&lt;pid&gt;/environ</c> (containers).
///     SetupCode and the auth endpoints read <c>Security:BootstrapToken</c> from IConfiguration, never from the environment.
/// </summary>
public static class SensitiveEnvironment
{
    /// <summary>Both the <c>__</c> form Linux and macOS use and the <c>:</c> form a Windows service may be given.</summary>
    public static readonly string[] Names =
    [
        "Security__BootstrapToken",
        "Security:BootstrapToken",
        "ConnectionStrings__Walis",
        "ConnectionStrings:Walis",
        // The master key of every enc:v2 value in the database; with it and a copy of the database every secret is readable.
        "Security__EncryptionKey",
        "Security:EncryptionKey"
    ];

    public static void Clear()
    {
        foreach (var name in Names) Environment.SetEnvironmentVariable(name, null);
    }
}
