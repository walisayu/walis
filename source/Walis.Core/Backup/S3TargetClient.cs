using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Walis.Core.Security;

namespace Walis.Core.Backup;

/// <summary>
///     S3-compatible storage (AWS S3, Cloudflare R2, MinIO, ...). Signs with SigV4 itself instead of
///     pulling in the AWS SDK: only PUT, GET (list) and DELETE requests are needed.
/// </summary>
public sealed class S3TargetClient(IBackupHttpClientFactory httpClientFactory) : IBackupTargetClient
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public string Provider => BackupProviders.S3;

    public async Task TestAsync(BackupTarget target, CancellationToken cancellationToken = default)
    {
        // Listing a single object is enough to verify the keys, bucket and endpoint.
        await SendListAsync(target, 1, cancellationToken);
    }

    public async Task UploadAsync(BackupTarget target, string filePath, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(target, Path.GetFileName(filePath));
        var payloadHash = await HashFileAsync(filePath, cancellationToken);

        await using var content = File.OpenRead(filePath);
        using var request = new HttpRequestMessage(HttpMethod.Put, BuildUri(target, key))
        {
            Content = new StreamContent(content)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = content.Length;

        using var response = await SendAsync(target, request, payloadHash, cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteBackupFile>> ListAsync(
        BackupTarget target,
        CancellationToken cancellationToken = default)
    {
        var body = await SendListAsync(target, 1000, cancellationToken);
        using var text = new StringReader(body);
        using var xml = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024
        });
        var document = XDocument.Load(xml, LoadOptions.None);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;

        var files = new List<RemoteBackupFile>();

        foreach (var element in document.Descendants(ns + "Contents"))
        {
            var key = element.Element(ns + "Key")?.Value ?? string.Empty;
            var name = key[(key.LastIndexOf('/') + 1)..];

            if (!BackupNaming.IsBackupFile(name)) continue;

            _ = DateTimeOffset.TryParse(
                element.Element(ns + "LastModified")?.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal,
                out var modified);
            _ = long.TryParse(element.Element(ns + "Size")?.Value, out var size);

            files.Add(new RemoteBackupFile(key, name, modified, size));
        }

        return files;
    }

    public async Task DeleteAsync(
        BackupTarget target,
        RemoteBackupFile file,
        CancellationToken cancellationToken = default)
    {
        if (!BackupNaming.IsBackupFile(file.Name) ||
            !string.Equals(file.Id, BuildKey(target, file.Name), StringComparison.Ordinal))
            throw new BackupTargetException("遠端備份物件路徑不在允許的前綴內。");

        using var request = new HttpRequestMessage(HttpMethod.Delete, BuildUri(target, file.Id));
        using var response = await SendAsync(target, request, EmptyPayloadHash, cancellationToken);
    }

    private async Task<string> SendListAsync(BackupTarget target, int maxKeys, CancellationToken cancellationToken)
    {
        var prefix = Prefix(target);
        var query = $"list-type=2&max-keys={maxKeys}";

        if (!string.IsNullOrEmpty(prefix))
            query = $"list-type=2&max-keys={maxKeys}&prefix={Uri.EscapeDataString(prefix)}";

        var builder = new UriBuilder(BuildUri(target, string.Empty)) { Query = query };

        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        using var response = await SendAsync(target, request, EmptyPayloadHash, cancellationToken);
        return await BackupHttpContent.ReadStringAsync(response.Content, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        BackupTarget target,
        HttpRequestMessage request,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        Sign(target, request, payloadHash);

        using var client = httpClientFactory.Create();

        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new BackupTargetException($"連不上 {request.RequestUri?.Host}：{exception.Message}", exception);
        }

        if (!response.IsSuccessStatusCode)
            try
            {
                var body = await BackupHttpContent.ReadStringAsync(response.Content, cancellationToken);
                throw new BackupTargetException(
                    $"S3 回應 {(int)response.StatusCode}：{Summarize(Sanitize(target, body))}");
            }
            finally
            {
                response.Dispose();
            }

        return response;
    }

    private static void Sign(BackupTarget target, HttpRequestMessage request, string payloadHash)
    {
        var accessKey = Require(target.AccessKeyId, "Access Key ID");
        var secretKey = Require(target.SecretAccessKey, "Secret Access Key");
        var region = string.IsNullOrWhiteSpace(target.Region) ? "auto" : target.Region.Trim();

        if (accessKey.Length > 1024 ||
            accessKey.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character) || character is '/' or ','))
            throw new BackupTargetException("Access Key ID 包含不允許的字元。");

        if (secretKey.Length > 4096 || secretKey.Any(character => character is '\r' or '\n' or '\0'))
            throw new BackupTargetException("Secret Access Key 格式無效或過長。");

        if (region.Length is < 1 or > 100 ||
            region.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new BackupTargetException("S3 區域名稱格式不正確。");

        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var uri = request.RequestUri!;

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        var canonicalHeaders = $"host:{uri.Host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";

        var canonicalRequest = string.Join('\n',
            request.Method.Method,
            CanonicalPath(uri.AbsolutePath),
            CanonicalQuery(uri.Query),
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        var scope = $"{dateStamp}/{region}/s3/aws4_request";
        var stringToSign = string.Join('\n', Algorithm, amzDate, scope, ToHex(Sha256(canonicalRequest)));

        var signingKey = HmacChain($"AWS4{secretKey}", dateStamp, region, "s3", "aws4_request");
        var signature = ToHex(Hmac(signingKey, stringToSign));

        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{Algorithm} Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    /// <summary>Paths are encoded segment by segment; the slashes themselves are not encoded.</summary>
    private static string CanonicalPath(string path)
    {
        return string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>The query string must be sorted by parameter name, with values encoded too.</summary>
    private static string CanonicalQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?") return string.Empty;

        var pairs = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var parts = pair.Split('=', 2);
                var name = Uri.EscapeDataString(Uri.UnescapeDataString(parts[0]));
                var value = parts.Length > 1 ? Uri.EscapeDataString(Uri.UnescapeDataString(parts[1])) : string.Empty;
                return (Name: name, Value: value);
            })
            .OrderBy(pair => pair.Name, StringComparer.Ordinal);

        return string.Join('&', pairs.Select(pair => $"{pair.Name}={pair.Value}"));
    }

    private static Uri BuildUri(BackupTarget target, string key)
    {
        var bucket = Require(target.Bucket, "Bucket").Trim().Trim('/');
        var endpoint = target.Endpoint?.Trim();

        if (bucket.Length is < 1 or > 255 || bucket.Any(character =>
                char.IsControl(character) || character is '/' or '\\' or '?' or '#'))
            throw new BackupTargetException("Bucket 名稱包含不允許的字元。");

        if (string.IsNullOrEmpty(endpoint))
        {
            // Without an endpoint, AWS uses the virtual-hosted style, matching the official default.
            var region = string.IsNullOrWhiteSpace(target.Region) ? "us-east-1" : target.Region.Trim();
            if (bucket.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '.') ||
                region.Length is < 1 or > 63 ||
                region.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                throw new BackupTargetException("AWS Bucket 或區域名稱格式不正確。");

            return new Uri($"https://{bucket}.s3.{region}.amazonaws.com/{key}");
        }

        if (!endpoint.Contains("://", StringComparison.Ordinal)) endpoint = $"https://{endpoint}";

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(endpointUri.Host) ||
            !string.IsNullOrEmpty(endpointUri.UserInfo) ||
            !string.IsNullOrEmpty(endpointUri.Query) ||
            !string.IsNullOrEmpty(endpointUri.Fragment))
            throw new BackupTargetException("S3 Endpoint 必須是不帶帳密、查詢或片段的 HTTP/HTTPS 網址。");

        var baseUri = new Uri(endpointUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        return new Uri(baseUri, $"{Uri.EscapeDataString(bucket)}/{key}");
    }

    private static string BuildKey(BackupTarget target, string fileName)
    {
        var prefix = Prefix(target);
        return string.IsNullOrEmpty(prefix) ? fileName : $"{prefix}{fileName}";
    }

    /// <summary>The object prefix, always ending with a slash (or the empty string).</summary>
    private static string Prefix(BackupTarget target)
    {
        var path = (target.Path ?? string.Empty).Trim().Trim('/');

        if (path.Length > 1024 || path.Contains('\\') ||
            (path.Length != 0 && path.Split('/').Any(segment =>
                segment.Length == 0 || segment is "." or ".." || segment.Any(char.IsControl))))
            throw new BackupTargetException("物件前綴包含不安全的路徑片段。");

        return string.IsNullOrEmpty(path) ? string.Empty : $"{path}/";
    }

    private static async Task<string> HashFileAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        return ToHex(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static byte[] HmacChain(string secret, params string[] parts)
    {
        var key = Encoding.UTF8.GetBytes(secret);

        foreach (var part in parts) key = Hmac(key, part);

        return key;
    }

    private static byte[] Hmac(byte[] key, string value)
    {
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
    }

    private static byte[] Sha256(string value)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }

    private static string ToHex(byte[] value)
    {
        return Convert.ToHexStringLower(value);
    }

    private static string Require(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new BackupTargetException($"請填寫 {name}。")
            : value.Trim();
    }

    /// <summary>The error body may be a large chunk of XML; the first part is enough to diagnose.</summary>
    private static string Summarize(string body)
    {
        return body.Length <= 300 ? body : body[..300];
    }

    private static string Sanitize(BackupTarget target, string value)
    {
        value = SecretRedactor.Redact(value, target.SecretAccessKey, target.AccessKeyId);
        return new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
    }
}