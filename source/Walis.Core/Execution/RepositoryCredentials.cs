using System.Net;
using System.Text.RegularExpressions;

namespace Walis.Core.Execution;

/// <summary>
///     Credentials embedded in repository URLs (<c>https://user:token@host/repo.git</c>). Walis refuses them on the job form,
///     the built-in git actions print the URL without them, and the executor still adds whatever it finds to the mask list,
///     because an action parameter or a hand-written script can carry such a URL without going through the job form.
/// </summary>
public static partial class RepositoryCredentials
{
    private const int MinimumSecretLength = 4;

    [GeneratedRegex(@"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*)://(?<userinfo>[^/@\s""'<>]+)@", RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoPattern();

    /// <summary>
    ///     Whether the repository address carries credentials. HTTP(S) never needs user info, so any is rejected; SSH-style
    ///     <c>ssh://git@host/repo</c> names a login and only a password after the colon counts.
    /// </summary>
    public static bool HasUserInfo(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return false;

        var match = UserInfoPattern().Match(repository.Trim());

        if (!match.Success || match.Index != 0) return false;

        var scheme = match.Groups["scheme"].Value;
        var userInfo = match.Groups["userinfo"].Value;

        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
               scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
               userInfo.Contains(':');
    }

    /// <summary>The URL with the user info removed, for logs and messages.</summary>
    public static string StripUserInfo(string? url)
    {
        return string.IsNullOrEmpty(url) ? string.Empty : UserInfoPattern().Replace(url, "${scheme}://");
    }

    /// <summary>
    ///     The secrets to mask from every URL with user info found in the texts: the whole user info, the password part after
    ///     the colon, and their URL-decoded forms. A bare SSH login such as <c>git</c> is not a secret and is skipped.
    /// </summary>
    public static IReadOnlyList<string> ExtractSecrets(params string?[] texts)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;

            foreach (Match match in UserInfoPattern().Matches(text))
            {
                var scheme = match.Groups["scheme"].Value;
                var userInfo = match.Groups["userinfo"].Value;
                var separator = userInfo.IndexOf(':');
                var isHttp = scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                             scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

                if (separator < 0 && !isHttp) continue;

                Add(userInfo);
                if (separator >= 0) Add(userInfo[(separator + 1)..]);
            }
        }

        return [.. secrets];

        void Add(string candidate)
        {
            foreach (var value in new[] { candidate, WebUtility.UrlDecode(candidate) })
                if (value.Length is >= MinimumSecretLength and <= ShellCommandExecutor.MaximumSecretLength)
                    secrets.Add(value);
        }
    }
}
