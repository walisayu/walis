using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Walis.Api.Services;
using Walis.Core.Repositories;

namespace Walis.Api.Endpoints;

public static class WebhookEndpoints
{
    public const int MaximumBodyBytes = 1024 * 1024;
    public const string TimestampHeader = "X-Walis-Timestamp";

    /// <summary>How far a stamped request may sit from the server clock; equal to the replay window.</summary>
    public static readonly TimeSpan TimestampTolerance = TimeSpan.FromMinutes(10);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var webhooks = app.MapGroup("/api/webhooks").WithTags("Webhooks");

        webhooks.MapPost("/push", async (
            HttpRequest request,
            ISettingsRepository settings,
            PushWebhookService pushes,
            WebhookRequestGuard guard,
            SecurityEventService securityEvents,
            CancellationToken cancellationToken) =>
        {
            // This route needs no sign-in, so the body cap must be enforced here: a real push event is already large at tens of KB.
            if (request.ContentLength > MaximumBodyBytes)
                return Results.Json(new { message = "推送內容過大。" }, statusCode: StatusCodes.Status413PayloadTooLarge);

            var configuration = await settings.GetAsync(cancellationToken);

            // The webhook is a public entry point; no secret means setup is not securely complete, and it must not degrade to accepting anonymously.
            if (string.IsNullOrWhiteSpace(configuration.WebhookSecret) ||
                configuration.WebhookSecret.Length is < 32 or > 1024)
                return Results.Json(
                    new { message = "webhook 尚未設定至少 32 字元的有效密鑰。" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var bodyBytes = await ReadBodyAsync(request, cancellationToken);

            if (bodyBytes is null)
                return Results.Json(new { message = "推送內容過大。" }, statusCode: StatusCodes.Status413PayloadTooLarge);

            if (!IsAuthorized(request, bodyBytes, configuration.WebhookSecret))
            {
                await securityEvents.AccessDeniedAsync(request.HttpContext, AccessDeniedReason.WebhookUnauthorized,
                    "webhook 簽章或權杖不符", SecurityEventService.AnonymousActor, cancellationToken);
                return Results.Json(new { message = "webhook 驗證失敗。" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // Walis-native senders may stamp the request; a stamp outside the replay window is refused outright, so a captured
            // token-authenticated request stops being usable once the replay table has forgotten it.
            if (!HasFreshTimestamp(request))
            {
                await securityEvents.AccessDeniedAsync(request.HttpContext, AccessDeniedReason.WebhookReplay,
                    "X-Walis-Timestamp 超出允許範圍", SecurityEventService.AnonymousActor, cancellationToken);
                return Results.Json(
                    new { message = $"X-Walis-Timestamp 必須是 Unix 秒數，且與伺服器時間相差不超過 {TimestampTolerance.TotalMinutes:0} 分鐘。" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            string body;
            try
            {
                body = StrictUtf8.GetString(bodyBytes);
            }
            catch (DecoderFallbackException)
            {
                return Results.BadRequest(new { message = "推送內容不是有效的 UTF-8。" });
            }

            if (!TryParse(body, out var push, out var error)) return Results.BadRequest(new { message = error });

            var decision = await guard.EvaluateAsync(request, body, cancellationToken);

            if (decision.Decision != WebhookRequestDecision.Allowed)
            {
                await securityEvents.AccessDeniedAsync(request.HttpContext,
                    decision.Decision == WebhookRequestDecision.Replay
                        ? AccessDeniedReason.WebhookReplay
                        : AccessDeniedReason.WebhookRateLimited,
                    decision.Decision == WebhookRequestDecision.Replay ? "重複送出的 webhook 事件" : "webhook 請求過於頻繁",
                    SecurityEventService.AnonymousActor, cancellationToken);

                request.HttpContext.Response.Headers.RetryAfter = Math.Max(
                    1,
                    (int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString();

                return decision.Decision == WebhookRequestDecision.Replay
                    ? Results.Json(new { message = "webhook 事件已處理。" }, statusCode: StatusCodes.Status409Conflict)
                    : Results.Json(new { message = "webhook 請求過於頻繁。" },
                        statusCode: StatusCodes.Status429TooManyRequests);
            }

            var builds = await pushes.TriggerAsync(push!, cancellationToken);

            // Only the count: the caller holds the shared webhook secret, not a Walis role, and must not learn the names or ids of
            // jobs it cannot see (ids are what the runner-report endpoints take).
            return Results.Ok(new { triggered = builds.Count });
        }).RequireRateLimiting("webhook-ingress");

        return app;
    }

    /// <summary>Reads at most 1 MB; returns null beyond that and the caller responds 413.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;

        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaximumBodyBytes) return null;

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Accepts a GitHub signature, a GitLab token or X-Walis-Token.</summary>
    private static bool IsAuthorized(HttpRequest request, ReadOnlySpan<byte> body, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return false;

        if (request.Headers.TryGetValue("X-Hub-Signature-256", out var signature))
        {
            var expected = "sha256=" + Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToString()),
                Encoding.UTF8.GetBytes(expected));
        }

        foreach (var header in new[] { "X-Gitlab-Token", "X-Walis-Token" })
            if (request.Headers.TryGetValue(header, out var token) &&
                FixedTimeTextEquals(token.ToString(), secret))
                return true;

        return false;
    }

    /// <summary>Without the header nothing changes; with it, the value must be Unix seconds within the tolerance either way.</summary>
    public static bool HasFreshTimestamp(HttpRequest request, DateTimeOffset? now = null)
    {
        if (!request.Headers.TryGetValue(TimestampHeader, out var values)) return true;

        var text = values.ToString().Trim();

        if (text.Length is 0 or > 20 || !long.TryParse(text, out var seconds)) return false;

        var difference = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() - seconds;
        return Math.Abs(difference) <= TimestampTolerance.TotalSeconds;
    }

    private static bool FixedTimeTextEquals(string supplied, string expected)
    {
        // Hash to a fixed width first. CryptographicOperations returns quickly for unequal input
        // lengths, which would otherwise expose the configured token length through timing.
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }

    /// <summary>Supports GitHub, GitLab and the simple {repository, branch} format.</summary>
    private static bool TryParse(string body, out PushEvent? push, out string? error)
    {
        push = null;
        error = null;

        if (string.IsNullOrWhiteSpace(body))
        {
            error = "請提供推送內容。";
            return false;
        }

        JsonElement root;

        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException exception)
        {
            error = $"無法解析內容：{exception.Message}";
            return false;
        }

        var branch = Text(root, "branch");
        var repository = Text(root, "repository");

        if (branch.Length == 0) branch = PushWebhookService.BranchFromRef(Text(root, "ref"));

        if (root.TryGetProperty("repository", out var repositoryObject) &&
            repositoryObject.ValueKind == JsonValueKind.Object)
        {
            repository = Text(repositoryObject, "clone_url");

            if (repository.Length == 0) repository = Text(repositoryObject, "html_url");

            if (repository.Length == 0) repository = Text(repositoryObject, "git_http_url");

            if (repository.Length == 0) repository = Text(repositoryObject, "url");
        }

        if (repository.Length == 0 && root.TryGetProperty("project", out var project) &&
            project.ValueKind == JsonValueKind.Object)
        {
            repository = Text(project, "git_http_url");

            if (repository.Length == 0) repository = Text(project, "web_url");
        }

        if (branch.Length == 0)
        {
            error = "找不到推送的分支，請提供 ref 或 branch。";
            return false;
        }

        if (repository.Length == 0)
        {
            error = "找不到推送的儲存庫。";
            return false;
        }

        var pusher = Text(root, "pusher") is { Length: > 0 } namedPusher
            ? namedPusher
            : Text(root, "user_name");
        var commit = Text(root, "after") is { Length: > 0 } after
            ? after
            : Text(root, "checkout_sha");

        if (branch.Length > 255 || repository.Length > 4096 ||
            pusher.Length > 512 || commit.Length > 512)
        {
            error = "推送事件中的分支、儲存庫或識別資訊過長。";
            return false;
        }

        push = new PushEvent(repository, branch, pusher, commit);

        return true;
    }

    private static string Text(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }
}