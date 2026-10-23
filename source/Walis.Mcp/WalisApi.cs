using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Walis.Mcp;

/// <summary>
///     A thin wrapper around the Walis REST API. Deliberately returns only JSON strings: MCP tool output is text anyway,
///     and an extra type-mapping layer would only mean keeping the contract in sync twice.
/// </summary>
public sealed class WalisApi : IDisposable
{
    public const int MaximumResponseBytes = 1 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly HttpClient _client;

    /// <summary>The server is on another machine and reached without TLS, so the API key travels in the clear.</summary>
    public bool UsesPlaintextRemote { get; }

    public WalisApi(HttpClient client, string baseUrl, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
            throw new ArgumentException("WALIS_API_URL 必須是不帶帳密、查詢字串或 fragment 的 HTTP/HTTPS 網址。", nameof(baseUrl));

        // Plain HTTP to another machine is allowed, matching the server's default; the caller warns about it.
        UsesPlaintextRemote = parsed.Scheme == "http" && !IsLoopback(parsed.Host);

        _client = client;
        _client.BaseAddress = parsed;
        _client.Timeout = TimeSpan.FromMinutes(2);

        if (!string.IsNullOrWhiteSpace(apiKey))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    public static HttpClient CreateSecureClient()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
            MaxResponseHeadersLength = 32,
            ResponseDrainTimeout = TimeSpan.FromSeconds(2),
            MaxResponseDrainSize = 64 * 1024
        });
    }

    public async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken)
    {
        return await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);
    }

    public async Task<JsonElement> PostAsync(string path, object? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (body is not null) request.Content = JsonContent.Create(body, options: Json);

        return await SendAsync(request, cancellationToken);
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var ownedRequest = request;
        using var response =
            await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var payload = await ReadBoundedAsync(response.Content, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new WalisApiException(
                $"{request.Method} {request.RequestUri} 回應 {(int)response.StatusCode}：{Summarize(payload)}");

        return payload.Length == 0
            ? default
            : JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 }).RootElement.Clone();
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new WalisApiException($"Walis 回應超過 {MaximumResponseBytes:N0} bytes。");

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(Math.Min((int)(content.Headers.ContentLength ?? 0), MaximumResponseBytes));
        var buffer = new byte[16 * 1024];

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;

            if (output.Length + read > MaximumResponseBytes)
                throw new WalisApiException($"Walis 回應超過 {MaximumResponseBytes:N0} bytes。");

            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static bool IsLoopback(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
    }

    public static string Format(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Undefined ? "（沒有內容）" : JsonSerializer.Serialize(element, Json);
    }

    /// <summary>Trims the list to the needed fields so hundreds of KB of JSON are not pushed at the model in one go.</summary>
    public static string Summarize(JsonElement array, params string[] fields)
    {
        if (array.ValueKind != JsonValueKind.Array) return Format(array);

        var builder = new StringBuilder();

        foreach (var item in array.EnumerateArray())
        {
            var parts = fields
                .Select(field => item.TryGetProperty(field, out var value)
                    ? $"{field}={Describe(value)}"
                    : null)
                .Where(part => part is not null);

            builder.AppendLine(string.Join("  ", parts));
        }

        return builder.Length == 0 ? "（沒有資料）" : builder.ToString().TrimEnd();
    }

    private static string Describe(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => "—",
            _ => value.ToString()
        };
    }

    private static string Summarize(string payload)
    {
        return payload.Length <= 400 ? payload : payload[..400];
    }
}

public sealed class WalisApiException(string message) : Exception(message);