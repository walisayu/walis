namespace Walis.Api.Services;

/// <summary>
///     The allowed CORS origins live in the system settings, but the CORS policy must be registered before startup,
///     so this shared object is updated after startup and whenever the settings change.
/// </summary>
public sealed class CorsOriginStore
{
    private static readonly string[] Defaults =
    [
        "http://127.0.0.1:11124",
        "http://localhost:11124"
    ];

    private volatile string[] _origins = Defaults;

    public IReadOnlyList<string> Origins => _origins;

    public void Update(string? configured)
    {
        var text = configured ?? string.Empty;
        var candidates = text
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Settings PUT validates this too, but restore writes the Settings row directly. Keep the
        // authorization store fail-closed if an old or crafted archive contains a non-origin value
        // such as "null", credentials, a path, or an oversized list.
        if (text.Length > 16_384 || candidates.Length > 32 ||
            candidates.Any(candidate => !TryNormalize(candidate, out _)))
        {
            _origins = Defaults;
            return;
        }

        var origins = candidates
            .Select(origin =>
            {
                TryNormalize(origin, out var normalized);
                return normalized!;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _origins = origins.Length == 0 ? Defaults : origins;
    }

    public bool IsAllowed(string origin)
    {
        return _origins.Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryNormalize(string value, out string? normalized)
    {
        normalized = null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return false;

        normalized = uri.GetLeftPart(UriPartial.Authority);
        return string.Equals(value.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase);
    }
}