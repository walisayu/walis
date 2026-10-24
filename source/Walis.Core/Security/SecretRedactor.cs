using System.Net;
using System.Text;
using System.Text.Json;

namespace Walis.Core.Security;

/// <summary>
///     Redacts direct secret echoes and the common encodings used in URLs, forms, JSON and
///     authorization payloads. This is a diagnostic-output safeguard, not an authorization boundary.
/// </summary>
public static class SecretRedactor
{
    public static string Redact(string value, params string?[] secrets)
    {
        return Redact(value, (IEnumerable<string?>)secrets);
    }

    public static string Redact(string value, IEnumerable<string?> secrets)
    {
        foreach (var variant in Expand(secrets)) value = value.Replace(variant, "***", StringComparison.Ordinal);

        return value;
    }

    /// <summary>
    ///     Every secret plus its common encodings, longest first so that masking a short form never leaves a fragment of a longer one.
    ///     Build logs compute this once per run and feed it to the streaming masker instead of calling <see cref="Redact" /> per chunk.
    /// </summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string?> secrets)
    {
        return
        [
            .. secrets
                .Where(secret => !string.IsNullOrEmpty(secret))
                .SelectMany(Variants)
                .Where(candidate => candidate.Length != 0)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(candidate => candidate.Length)
        ];
    }

    public static IEnumerable<string> Variants(string? nullableSecret)
    {
        if (string.IsNullOrEmpty(nullableSecret)) yield break;

        var secret = nullableSecret;
        var bytes = Encoding.UTF8.GetBytes(secret);
        var base64 = Convert.ToBase64String(bytes);
        var json = JsonSerializer.Serialize(secret);

        yield return secret;
        yield return Uri.EscapeDataString(secret);
        yield return WebUtility.UrlEncode(secret);
        yield return base64;
        yield return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        yield return json.Length >= 2 ? json[1..^1] : json;
        yield return WebUtility.HtmlEncode(secret);
    }
}