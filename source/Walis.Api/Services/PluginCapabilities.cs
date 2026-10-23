using System.Net;
using System.Text;
using System.Text.Json;
using Walis.Core.Models;
using Walis.Core.Plugins;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Api.Services;

/// <summary>
///     The outside world a plugin can reach. Each item is granted only when declared in the manifest:
///     network only to the declared hosts (matched in <see cref="PluginHost" />), and secrets can be "used" but never "read".
/// </summary>
public sealed class PluginCapabilities(
    IHttpClientFactory httpClientFactory,
    IEnvironmentVariableRepository variables,
    ILogger<PluginCapabilities> logger) : IPluginCapabilities
{
    private const int MaximumBodyBytes = 1024 * 1024;
    private const int MaximumUrlCharacters = 2048;
    private const int MaximumHeaders = 32;
    private const int MaximumHeaderBytes = 64 * 1024;
    private const int MaximumHeaderNameCharacters = 128;
    private const int MaximumHeaderValueCharacters = 8192;

    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Content-Length",
        "Host",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    /// <summary>The longest line a plugin's log() may write; the script is third-party code and must not flood or forge the server log.</summary>
    public const int MaximumLogMessageLength = 500;

    public void Log(Plugin plugin, string message)
    {
        logger.LogInformation("外掛 {Plugin}：{Message}", plugin.PluginId, AuditText.Clean(message, MaximumLogMessageLength));
    }

    public IReadOnlyDictionary<string, object?> Fetch(
        Plugin plugin,
        string url,
        IReadOnlyDictionary<string, JsonElement>? options)
    {
        var method = ReadString(options, "method") ?? "GET";
        var body = ReadString(options, "body");

        method = method.ToUpperInvariant();
        if (method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE"))
            throw new PluginException($"外掛 HTTP 方法不允許：{method}");

        if (url.Length > MaximumUrlCharacters ||
            !PluginHost.IsAllowed(url, plugin.Manifest.Permissions?.Http ?? []))
            throw new PluginException("外掛 HTTP 網址無效、過長，或不在允許清單內。");

        if (body is not null && Encoding.UTF8.GetByteCount(body) > MaximumBodyBytes)
            throw new PluginException("外掛 HTTP 請求本體不可超過 1 MiB。");

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (AllowsExplicitPrivateLiteral(request.RequestUri!))
            // Private destinations are only allowed when the administrator wrote the literal IP
            // (or localhost) in the manifest. A public hostname cannot DNS-rebind into the LAN.
            request.Options.Set(OutboundHttpHandlers.AllowPrivateNetwork, true);

        if (body is not null) request.Content = new StringContent(body);

        if (options is not null && options.TryGetValue("headers", out var headers) &&
            headers.ValueKind == JsonValueKind.Object)
        {
            if (headers.GetRawText().Length > MaximumHeaderBytes || headers.EnumerateObject().Count() > MaximumHeaders)
                throw new PluginException("外掛 HTTP 標頭數量或長度超過上限。");

            foreach (var header in headers.EnumerateObject())
            {
                var value = header.Value.ToString();
                if (!IsSafeHeader(header.Name, value)) throw new PluginException($"外掛 HTTP 標頭不允許：{header.Name}");

                if (!request.Headers.TryAddWithoutValidation(header.Name, value))
                    throw new PluginException($"外掛 HTTP 標頭格式無效：{header.Name}");
            }
        }

        var appliedSecret = ApplySecret(plugin, request, options);

        // The plugin host API is synchronous (glue code is easier to write synchronously than with Promises),
        // and plugins only run on background threads during builds or previews, so blocking here never stalls request handling.
        var client = httpClientFactory.CreateClient("plugins");
        using var response = client.Send(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None);
        var content = RedactSecretEchoes(ReadBounded(response.Content), appliedSecret);

        return new Dictionary<string, object?>
        {
            ["status"] = (int)response.StatusCode,
            ["ok"] = response.IsSuccessStatusCode,
            ["body"] = content
        };
    }

    /// <summary>
    ///     A plugin can only say "put this secret into this header" and never gets the value itself;
    ///     otherwise a plugin with outbound access could POST the token anywhere.
    /// </summary>
    private string? ApplySecret(
        Plugin plugin,
        HttpRequestMessage request,
        IReadOnlyDictionary<string, JsonElement>? options)
    {
        if (options is null ||
            !options.TryGetValue("withSecret", out var descriptor) ||
            descriptor.ValueKind != JsonValueKind.Object)
            return null;

        var name = descriptor.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
        var header = descriptor.TryGetProperty("header", out var headerValue) ? headerValue.GetString() : null;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(header))
            throw new PluginException("withSecret 需要 name 與 header。");

        if (plugin.Manifest.Permissions?.Secrets?.Contains(name, StringComparer.Ordinal) != true)
            throw new PluginException($"外掛沒有宣告可以使用機密「{name}」。");

        // Plugins do not belong to a job; use the API explicitly marked trusted-service so no special Job ID
        // can accidentally obtain global secrets.
        var resolved = variables.ResolveGlobalsForTrustedServiceAsync().GetAwaiter().GetResult();

        if (!resolved.TryGetValue(name, out var secret) || string.IsNullOrEmpty(secret))
            throw new PluginException($"找不到機密「{name}」。");

        if (!IsSafeHeader(header, secret) ||
            !request.Headers.TryAddWithoutValidation(header, secret))
            throw new PluginException($"外掛機密標頭不允許或格式無效：{header}");

        return secret;
    }

    private static string? ReadString(IReadOnlyDictionary<string, JsonElement>? options, string name)
    {
        return options is not null && options.TryGetValue(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool IsSafeHeader(string name, string value)
    {
        return name.Length is > 0 and <= MaximumHeaderNameCharacters &&
               value.Length <= MaximumHeaderValueCharacters &&
               !ForbiddenHeaders.Contains(name) &&
               !name.Any(character =>
                   char.IsControl(character) || char.IsWhiteSpace(character) || character is ':' or ',') &&
               !value.Any(character => character is '\r' or '\n' or '\0');
    }

    private static bool AllowsExplicitPrivateLiteral(Uri uri)
    {
        var host = uri.IdnHost.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               (IPAddress.TryParse(host, out var address) && !OutboundHttpHandlers.IsPublicAddress(address));
    }

    private static string ReadBounded(HttpContent content)
    {
        if (content.Headers.ContentLength > MaximumBodyBytes) throw new PluginException("外掛 HTTP 回應不可超過 1 MiB。");

        using var input = content.ReadAsStream();
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;

        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + read > MaximumBodyBytes) throw new PluginException("外掛 HTTP 回應不可超過 1 MiB。");

            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static string RedactSecretEchoes(string value, string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return value;

        return SecretRedactor.Redact(value, secret);
    }
}