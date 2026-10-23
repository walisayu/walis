using System.Reflection;

namespace Walis.Api.Services;

/// <summary>
///     The service version: the number comes from the csproj <c>Version</c>, the commit is put into the informational version by git at build time
///     (<c>1.0.0+4c90a97</c>). The admin UI footer and <c>/</c> show the same value because they are the same release.
/// </summary>
public static class WalisVersion
{
    static WalisVersion()
    {
        var assembly = typeof(WalisVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                            ?? assembly.GetName().Version?.ToString(3)
                            ?? "0.0.0";

        var plus = informational.IndexOf('+');
        Number = plus < 0 ? informational : informational[..plus];
        Commit = plus < 0 || plus == informational.Length - 1 ? null : informational[(plus + 1)..];
    }

    public static string Number { get; }
    public static string? Commit { get; }
    public static string Display => Commit is null ? Number : $"{Number}+{Commit}";
}