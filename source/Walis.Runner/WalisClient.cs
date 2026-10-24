using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Walis.Core.Execution;
using Walis.Core.Models;

namespace Walis.Runner;

public sealed record ClaimedWork(
    Build Build,
    Job Job,
    Dictionary<string, string> Variables,
    List<BuildStep> Steps,
    List<string>? Secrets = null,
    bool MaskSecrets = true,
    bool RequireSandbox = true,
    bool ExternalIsolationSatisfied = false,
    bool CleanWorkspace = false,
    bool TrustedUnsandboxed = false,
    bool CleanWorkspaceAfterBuild = false);

public sealed class WalisClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly bool _externalIsolation;
    private readonly string? _username;
    private readonly string? _password;

    public WalisClient(RunnerOptions options)
    {
        _externalIsolation = options.ExternalIsolation;
        _username = options.Username;
        _password = options.Password;
        _client = new HttpClient(new SocketsHttpHandler
        {
            // A redirect target may be a different host or downgrade to HTTP; the runner must not hand its long-lived token, credentials
            // or job secrets to an endpoint not explicitly named in the configuration.
            AllowAutoRedirect = false
        })
        {
            BaseAddress = options.Server,
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 8 * 1024 * 1024
        };

        if (!string.IsNullOrWhiteSpace(options.Token))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public void Dispose()
    {
        _client.Dispose();
    }

    /// <summary>
    ///     Signs in as a command-line client: <c>?bearer=true</c> asks the server for a separate short-lived bearer session
    ///     instead of the browser cookie. The session lasts an hour, so <see cref="ClaimAsync" /> signs in again when it expires.
    /// </summary>
    public async Task<string?> SignInAsync(string username, string password, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/auth/login?bearer=true",
            new { username, password },
            Json,
            cancellationToken);

        if (!response.IsSuccessStatusCode) return null;

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
        var token = payload.TryGetProperty("token", out var property) ? property.GetString() : null;

        if (string.IsNullOrWhiteSpace(token)) return null;

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return token;
    }

    /// <summary>A bearer session has expired: sign in again with the credentials given at startup, if any.</summary>
    private async Task<bool> TryRenewSessionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password)) return false;

        return await SignInAsync(_username, _password, cancellationToken) is not null;
    }

    public async Task<bool> EnsureRegisteredAsync(RunnerOptions options, CancellationToken cancellationToken)
    {
        var runners =
            await _client.GetFromJsonAsync<List<Core.Models.Runner>>("/api/runners/", Json, cancellationToken);

        if (runners?.Any(runner => string.Equals(runner.Name, options.Name, StringComparison.OrdinalIgnoreCase)) ==
            true) return true;

        using var response = await _client.PostAsJsonAsync(
            "/api/runners/",
            new
            {
                name = options.Name,
                operatingSystem = RuntimeInformation.OSDescription,
                version = typeof(WalisClient).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                labels = options.Labels ?? string.Empty,
                totalSlots = options.Slots,
                isPaused = false
            },
            Json,
            cancellationToken);

        return response.IsSuccessStatusCode;
    }

    /// <summary>Raised when the server says, on any report, that the build currently running was asked to stop.</summary>
    public event Action? CancelRequested;

    private void NoticeCancel(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Walis-Cancel-Requested", out var values) &&
            values.Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)))
            CancelRequested?.Invoke();
    }

    public async Task<ClaimedWork?> ClaimAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await SendClaimAsync(name, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NoContent) return null;

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                await DescribeRefusalAsync(response, cancellationToken),
                null,
                response.StatusCode);

        return await response.Content.ReadFromJsonAsync<ClaimedWork>(Json, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendClaimAsync(string name, CancellationToken cancellationToken)
    {
        var response = await _client.SendAsync(CreateClaimRequest(name), cancellationToken);

        // A password sign-in only lasts an hour; renew once and retry before reporting the refusal.
        if (response.StatusCode != HttpStatusCode.Unauthorized || !await TryRenewSessionAsync(cancellationToken))
            return response;

        response.Dispose();
        return await _client.SendAsync(CreateClaimRequest(name), cancellationToken);
    }

    private HttpRequestMessage CreateClaimRequest(string name)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/runners/{Uri.EscapeDataString(name)}/claim");
        request.Headers.Add("X-Walis-Runner-Protocol", "2");

        // The runner does not currently claim an isolation capability it does not really apply. When the server requires a sandbox
        // it refuses before the claim, so an old or incapable runner never silently runs untrusted commands.
        request.Headers.Add("X-Walis-Runner-Isolation", _externalIsolation ? "external" : "none");
        return request;
    }

    /// <summary>
    ///     Turns a refused claim into the server's own explanation. The server says why it hands out no work (isolation not
    ///     satisfied, runner not approved, HTTPS required); a bare status code leaves the operator with a runner that idles forever.
    /// </summary>
    private static async Task<string> DescribeRefusalAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        const int maxMessageLength = 500;
        var status = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();

        try
        {
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);

            if (payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String &&
                message.GetString() is { } text &&
                !string.IsNullOrWhiteSpace(text))
                return $"{(text.Length > maxMessageLength ? text[..maxMessageLength] : text)}（{status}）";
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or HttpRequestException)
        {
            // A proxy or an older server may answer with HTML or an empty body; the status alone is still worth reporting.
        }

        return $"伺服器拒絕認領（{status}）。";
    }

    /// <summary>Reports a single step's output while running.</summary>
    public async Task ReportStepLogAsync(
        Guid buildId,
        Guid stepId,
        string log,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.PostAsJsonAsync(
                $"/api/builds/{buildId}/steps/{stepId}/log",
                new { log },
                Json,
                cancellationToken);
            NoticeCancel(response);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"回報步驟輸出失敗：{exception.Message}");
        }
    }

    public async Task ReportStepResultAsync(
        Guid buildId,
        Guid stepId,
        string status,
        int? exitCode,
        string log,
        IReadOnlyDictionary<string, string> outputs,
        CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/builds/{buildId}/steps/{stepId}/result",
            new { status, exitCode, log, outputs },
            Json,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        NoticeCancel(response);
    }

    /// <summary>Reports the current output while running; a failure does not affect the build, the next report simply sends it again.</summary>
    public async Task ReportLogAsync(Guid buildId, string log, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.PostAsJsonAsync(
                $"/api/builds/{buildId}/log",
                new { log },
                Json,
                cancellationToken);
            NoticeCancel(response);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"回報執行中的輸出失敗：{exception.Message}");
        }
    }

    public async Task ReportAsync(Guid buildId, BuildCompletion completion, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/builds/{buildId}/result",
            new { status = completion.Status, exitCode = completion.ExitCode, log = completion.Log },
            Json,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task<int> UploadArtifactsAsync(
        Guid buildId,
        string directory,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var streams = new List<Stream>();
        var count = 0;
        var totalBytes = 0L;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

        try
        {
            foreach (var relative in relativePaths.Take(ArtifactCollector.MaximumFiles))
            {
                var normalized = relative.Replace('\\', '/');

                if (string.IsNullOrWhiteSpace(normalized) ||
                    Path.IsPathRooted(normalized) ||
                    normalized.Split('/').Any(segment => segment is "" or "." or ".."))
                    continue;

                var path = Path.GetFullPath(Path.Combine(
                    root,
                    normalized.Replace('/', Path.DirectorySeparatorChar)));

                if (!path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) ||
                    !SafeRegularFile.TryOpenRead(path, out var stream))
                    continue;

                var length = stream.Length;

                if (length > ArtifactCollector.MaximumTotalBytes ||
                    totalBytes > ArtifactCollector.MaximumTotalBytes - length)
                {
                    stream.Dispose();
                    continue;
                }

                totalBytes += length;
                streams.Add(stream);
                content.Add(new StreamContent(stream), "files", normalized);
                count++;
            }

            if (count == 0) return 0;

            using var response =
                await _client.PostAsync($"/api/builds/{buildId}/artifacts", content, cancellationToken);
            response.EnsureSuccessStatusCode();
            return count;
        }
        finally
        {
            foreach (var stream in streams) await stream.DisposeAsync();
        }
    }
}