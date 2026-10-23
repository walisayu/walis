using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Walis.Core.Security;

namespace Walis.Core.Backup;

/// <summary>
///     The part shared by Dropbox, Google Drive and OneDrive: exchanging a refresh token for a short-lived access token.
///     All three require the user to create an app in their console, so the client id/secret also come from the settings.
/// </summary>
public abstract class OAuthTargetClient(IBackupHttpClientFactory httpClientFactory) : IBackupTargetClient
{
    private const int MaximumAccessTokenCharacters = 16 * 1024;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshLocks = new();
    private readonly ConcurrentDictionary<Guid, CachedToken> _tokens = new();

    protected abstract string TokenEndpoint { get; }

    /// <summary>Extra fields to send with the token request (e.g. OneDrive's scope).</summary>
    protected virtual IEnumerable<KeyValuePair<string, string>> ExtraTokenFields => [];

    public abstract string Provider { get; }

    public abstract Task TestAsync(BackupTarget target, CancellationToken cancellationToken = default);

    public abstract Task UploadAsync(BackupTarget target, string filePath,
        CancellationToken cancellationToken = default);

    public abstract Task<IReadOnlyList<RemoteBackupFile>> ListAsync(
        BackupTarget target,
        CancellationToken cancellationToken = default);

    public abstract Task DeleteAsync(
        BackupTarget target,
        RemoteBackupFile file,
        CancellationToken cancellationToken = default);

    protected HttpClient CreateClient()
    {
        return httpClientFactory.Create();
    }

    /// <summary>Gets an access token; reused for the same destination while still valid.</summary>
    protected async Task<string> GetAccessTokenAsync(BackupTarget target, CancellationToken cancellationToken)
    {
        var fingerprint = CredentialFingerprint(target);
        if (TryGetCached(target.Id, fingerprint, out var cached)) return cached!;

        var refreshLock = _refreshLocks.GetOrAdd(target.Id, static _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);

        try
        {
            if (TryGetCached(target.Id, fingerprint, out cached)) return cached!;

            var fields = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "refresh_token"),
                new("refresh_token", Require(target.RefreshToken, "Refresh Token", 8192)),
                new("client_id", Require(target.ClientId, "Client ID", 1024)),
                new("client_secret", Require(target.ClientSecret, "Client Secret"))
            };
            fields.AddRange(ExtraTokenFields);

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(fields)
            };

            using var response = await SendRawAsync(target, request, cancellationToken);
            var payload = await BackupHttpContent.ReadJsonAsync(response.Content, cancellationToken);

            var token = payload.TryGetProperty("access_token", out var accessToken) &&
                        accessToken.ValueKind == JsonValueKind.String
                ? accessToken.GetString()
                : null;

            if (string.IsNullOrEmpty(token) || token.Length > MaximumAccessTokenCharacters ||
                token.Any(character => character is '\r' or '\n' or '\0'))
                throw new BackupTargetException("取得存取權杖失敗：access_token 缺少、過長或格式無效。");

            var seconds = payload.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var parsed)
                ? Math.Clamp(parsed, 60, 86_400)
                : 3600;
            _tokens[target.Id] = new CachedToken(token, DateTimeOffset.UtcNow.AddSeconds(seconds), fingerprint);

            return token;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    protected async Task<HttpResponseMessage> SendAsync(
        BackupTarget target,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessTokenAsync(target, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await SendRawAsync(target, request, cancellationToken, accessToken);
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        BackupTarget target,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        string? accessToken = null)
    {
        HttpResponseMessage response;

        try
        {
            using var client = CreateClient();
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new BackupTargetException(
                $"連不上 {request.RequestUri?.Host}：{Sanitize(target, exception.Message, accessToken)}",
                exception);
        }

        if (!response.IsSuccessStatusCode)
            try
            {
                var body = await BackupHttpContent.ReadStringAsync(response.Content, cancellationToken);
                throw new BackupTargetException(
                    $"{Provider} 回應 {(int)response.StatusCode}：{Summarize(Sanitize(target, body, accessToken))}");
            }
            finally
            {
                response.Dispose();
            }

        return response;
    }

    protected static string Require(string? value, string name, int maximumLength = 4096)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new BackupTargetException($"請填寫 {name}。");

        var result = value.Trim();
        if (result.Length > maximumLength || result.Any(character => character is '\0' or '\r' or '\n'))
            throw new BackupTargetException($"{name} 格式無效或過長。");

        return result;
    }

    /// <summary>Normalizes a folder path to the /a/b form; the root is the empty string.</summary>
    protected static string NormalizeFolder(string? path)
    {
        var value = (path ?? string.Empty).Trim().Trim('/');

        if (value.Length > 1024 || value.Contains('\\') ||
            (value.Length != 0 && value.Split('/').Any(segment =>
                segment.Length == 0 || segment is "." or ".." || segment.Any(char.IsControl))))
            throw new BackupTargetException("遠端資料夾路徑包含不安全的片段或超過長度上限。");

        return string.IsNullOrEmpty(value) ? string.Empty : $"/{value}";
    }

    protected static string Summarize(string body)
    {
        return body.Length <= 300 ? body : body[..300];
    }

    private bool TryGetCached(Guid targetId, string fingerprint, out string? token)
    {
        if (_tokens.TryGetValue(targetId, out var cached) &&
            cached.CredentialFingerprint == fingerprint &&
            cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            token = cached.Token;
            return true;
        }

        token = null;
        return false;
    }

    private static string CredentialFingerprint(BackupTarget target)
    {
        var value = string.Join('\0', target.ClientId, target.ClientSecret, target.RefreshToken);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string Sanitize(BackupTarget target, string value, string? accessToken)
    {
        value = SecretRedactor.Redact(
            value,
            target.ClientId,
            target.ClientSecret,
            target.RefreshToken,
            accessToken);
        return new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
    }

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt, string CredentialFingerprint);
}