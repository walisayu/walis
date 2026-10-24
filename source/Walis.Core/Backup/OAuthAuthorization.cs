using System.Net;
using System.Text.Json;

namespace Walis.Core.Backup;

/// <summary>
///     The OAuth authorization flow used by the setup wizard: builds an authorization URL for the user to open themselves, then exchanges the code
///     they paste back for a refresh token. Walis never signs in on the user's behalf; it only does the last "code -> refresh token" step,
///     saving the hassle of hand-crafting curl and putting the client secret on a command line.
/// </summary>
public static class OAuthAuthorization
{
    /// <summary>There is no local page to receive a redirect; providers differ in how they handle the "desktop/copy it yourself" flow, and this is the default each accepts.</summary>
    public const string LocalhostRedirect = "http://localhost";

    public static bool Supports(string? provider) => BackupProviders.UsesOAuth(provider);

    /// <summary>The provider's authorization URL; returns null when no client id is set.</summary>
    public static string? BuildAuthorizeUrl(string provider, string? clientId, string? redirectUri = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var id = WebUtility.UrlEncode(clientId.Trim());
        var redirect = WebUtility.UrlEncode(string.IsNullOrWhiteSpace(redirectUri) ? LocalhostRedirect : redirectUri.Trim());

        return provider switch
        {
            // No redirect_uri: Dropbox then shows the code on the page for the user to copy.
            // Without token_access_type=offline only a short-lived access token is issued and no refresh token.
            BackupProviders.Dropbox =>
                $"https://www.dropbox.com/oauth2/authorize?client_id={id}&response_type=code&token_access_type=offline",

            // Besides access_type=offline, prompt=consent is required, otherwise a second authorization returns no refresh token.
            BackupProviders.GoogleDrive =>
                "https://accounts.google.com/o/oauth2/v2/auth" +
                $"?client_id={id}&redirect_uri={redirect}&response_type=code" +
                "&scope=" + WebUtility.UrlEncode("https://www.googleapis.com/auth/drive.file") +
                "&access_type=offline&prompt=consent",

            // The scope must include offline_access to get a refresh token.
            BackupProviders.OneDrive =>
                "https://login.microsoftonline.com/common/oauth2/v2.0/authorize" +
                $"?client_id={id}&redirect_uri={redirect}&response_type=code&response_mode=query" +
                "&scope=" + WebUtility.UrlEncode("Files.ReadWrite offline_access"),

            _ => null
        };
    }

    public static string? TokenEndpoint(string provider) => provider switch
    {
        BackupProviders.Dropbox => "https://api.dropbox.com/oauth2/token",
        BackupProviders.GoogleDrive => "https://oauth2.googleapis.com/token",
        BackupProviders.OneDrive => "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        _ => null
    };

    /// <summary>Exchanges the authorization code for a refresh token. The code is single-use and short-lived; failure messages never print it or the secret.</summary>
    public static async Task<string> ExchangeCodeAsync(
        IBackupHttpClientFactory httpClientFactory,
        string provider,
        string? clientId,
        string? clientSecret,
        string? code,
        string? redirectUri,
        CancellationToken cancellationToken = default)
    {
        var endpoint = TokenEndpoint(provider) ?? throw new BackupTargetException("這種目的地不需要 OAuth 授權。");
        var id = Require(clientId, "Client ID", 1024);
        var secret = Require(clientSecret, "Client Secret", 4096);
        var authorizationCode = Require(code, "授權碼", 4096);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", authorizationCode),
            new("client_id", id),
            new("client_secret", secret)
        };

        if (provider != BackupProviders.Dropbox)
        {
            fields.Add(new KeyValuePair<string, string>("redirect_uri", string.IsNullOrWhiteSpace(redirectUri) ? LocalhostRedirect : redirectUri.Trim()));
        }

        if (provider == BackupProviders.OneDrive)
        {
            fields.Add(new KeyValuePair<string, string>("scope", "Files.ReadWrite offline_access"));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(fields) };
        HttpResponseMessage response;

        try
        {
            using var client = httpClientFactory.Create();
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new BackupTargetException($"連不上 {request.RequestUri?.Host}：{Scrub(exception.Message, secret, authorizationCode)}", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await BackupHttpContent.ReadStringAsync(response.Content, cancellationToken);
                throw new BackupTargetException($"{provider} 回應 {(int)response.StatusCode}：{Summarize(Scrub(body, secret, authorizationCode))}");
            }

            var payload = await BackupHttpContent.ReadJsonAsync(response.Content, cancellationToken);
            var token = payload.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String
                ? refresh.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(token) || token.Length > 8192 || token.Any(character => character is '\r' or '\n' or '\0'))
            {
                throw new BackupTargetException(provider switch
                {
                    BackupProviders.GoogleDrive => "Google 沒有回 refresh token：Google 只在第一次同意時發 refresh token，這個應用程式之前授權過就不會再給。到 Google 帳戶的「第三方存取」移除它，再用精靈產生的網址（帶 prompt=consent）重新授權一次。",
                    BackupProviders.Dropbox => "Dropbox 沒有回 refresh token：授權網址要帶 token_access_type=offline，請用精靈產生的網址重新授權。",
                    _ => "沒有拿到 refresh token：請確認授權時的 scope 含 offline_access，再重新授權。"
                });
            }

            return token;
        }
    }

    private static string Require(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new BackupTargetException($"請填寫 {name}。");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maximumLength || trimmed.Any(character => char.IsControl(character)))
        {
            throw new BackupTargetException($"{name} 格式無效或過長。");
        }

        return trimmed;
    }

    private static string Scrub(string text, params string[] secrets)
    {
        foreach (var secret in secrets.Where(secret => secret.Length >= 4))
        {
            text = text.Replace(secret, "********", StringComparison.Ordinal);
        }

        return text;
    }

    private static string Summarize(string body)
    {
        var flat = string.Join(' ', body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length <= 300 ? flat : flat[..300] + "…";
    }
}
