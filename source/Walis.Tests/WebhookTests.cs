using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Walis.Api.Endpoints;
using Walis.Api.Services;
using Walis.Core.Models;

namespace Walis.Tests;

public class PushWebhookServiceTests
{
    [Theory]
    [InlineData("https://github.com/example/walis.git", "github.com/example/walis")]
    [InlineData("http://github.com/example/walis", "github.com/example/walis")]
    [InlineData("git@github.com:example/walis.git", "github.com/example/walis")]
    [InlineData("https://user:token@gitlab.com/example/walis.git/", "gitlab.com/example/walis")]
    [InlineData("HTTPS://GitHub.com/EXAMPLE/Walis", "github.com/example/walis")]
    public void DifferentRepositoryAddressFormsNormalizeToSameResult(string url, string expected)
    {
        Assert.Equal(expected, PushWebhookService.Normalize(url));
    }

    [Fact]
    public void BlankRepositoryAddressNormalizesToEmptyString()
    {
        Assert.Equal(string.Empty, PushWebhookService.Normalize(null));
        Assert.Equal(string.Empty, PushWebhookService.Normalize("  "));
    }

    [Theory]
    [InlineData("refs/heads/main", "main")]
    [InlineData("refs/heads/feature/leo_260901_x", "feature/leo_260901_x")]
    [InlineData("main", "main")]
    [InlineData(null, "")]
    public void ExtractsBranchNameFromRef(string? reference, string expected)
    {
        Assert.Equal(expected, PushWebhookService.BranchFromRef(reference));
    }

    [Fact]
    public async Task ServiceLayerNeitherQueriesNorTriggersOnEventWithoutRepository()
    {
        var service = new PushWebhookService(
            null!,
            null!,
            NullLogger<PushWebhookService>.Instance);

        var builds = await service.TriggerAsync(new PushEvent(null, "main", null, null), CancellationToken.None);

        Assert.Empty(builds);
    }
}

public class WebhookApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task AuthenticateAsAdminAsync(HttpClient client)
    {
        const string username = "webhook-admin";
        const string password = "webhook-password";
        var response = await client.PostAsJsonAsync("/api/auth/setup?bearer=true", new
        {
            username,
            displayName = "Webhook Admin",
            password
        });

        if (response.StatusCode == HttpStatusCode.Conflict)
            response = await client.PostAsJsonAsync("/api/auth/login?bearer=true", new { username, password });

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            payload.GetProperty("token").GetString());
    }

    private static async Task SetWebhookSecretAsync(
        HttpClient client,
        string secret = "top-secret-with-at-least-32-characters")
    {
        await AuthenticateAsAdminAsync(client);
        var payload = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        var settings = payload.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
        settings.WebhookSecret = secret;
        (await client.PutAsJsonAsync("/api/settings", settings)).EnsureSuccessStatusCode();
    }

    private static async Task<Job> CreatePushJobAsync(HttpClient client, string branch, string? repository)
    {
        var response = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"推送測試 {Guid.NewGuid():N}",
            project = "Walis",
            command = "echo push",
            branch,
            repository,
            triggerKind = "push",
            timeoutMinutes = 5,
            notifyOnFailure = true
        });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Job>(Json))!;
    }

    [Fact]
    public async Task GitHubFormatPushTriggersMatchingJob()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();
        await SetWebhookSecretAsync(client);
        var job = await CreatePushJobAsync(client, "main", "https://github.com/example/walis.git");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/push")
        {
            Content = JsonContent.Create(new
            {
                @ref = "refs/heads/main",
                repository = new { clone_url = "git@github.com:example/walis.git" }
            })
        };
        request.Headers.Add("X-Walis-Token", "top-secret-with-at-least-32-characters");
        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(1, payload.GetProperty("triggered").GetInt32());
        // The shared secret is not a role: the response names no job and no build id.
        Assert.False(payload.TryGetProperty("builds", out _));
        Assert.DoesNotContain(job.Name, payload.GetRawText());
    }

    [Fact]
    public async Task PushNeverStartsATrustedJob()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();
        await SetWebhookSecretAsync(client);
        var created = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"信任推送 {Guid.NewGuid():N}",
            project = "Walis",
            command = "echo trusted",
            branch = "main",
            repository = "https://github.com/example/trusted.git",
            triggerKind = "push",
            timeoutMinutes = 5,
            trustedUnsandboxed = true,
            runRole = "admin"
        });
        created.EnsureSuccessStatusCode();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/push")
        {
            Content = JsonContent.Create(new { repository = "https://github.com/example/trusted.git", branch = "main" })
        };
        request.Headers.Add("X-Walis-Token", "top-secret-with-at-least-32-characters");
        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("triggered").GetInt32());
    }

    [Fact]
    public async Task DoesNotTriggerWhenBranchDiffers()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();
        await SetWebhookSecretAsync(client);
        await CreatePushJobAsync(client, "main", "https://github.com/example/walis.git");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/push")
        {
            Content = JsonContent.Create(new
            {
                @ref = "refs/heads/develop",
                repository = new { clone_url = "https://github.com/example/walis.git" }
            })
        };
        request.Headers.Add("X-Walis-Token", "top-secret-with-at-least-32-characters");
        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal(0,
            (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("triggered").GetInt32());
    }

    [Fact]
    public async Task DoesNotTriggerWhenRepositoryDiffers()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();
        await SetWebhookSecretAsync(client);
        await CreatePushJobAsync(client, "main", "https://github.com/example/walis.git");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/push")
        {
            Content = JsonContent.Create(new
            {
                @ref = "refs/heads/main",
                repository = new { clone_url = "https://github.com/other/project.git" }
            })
        };
        request.Headers.Add("X-Walis-Token", "top-secret-with-at-least-32-characters");
        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal(0,
            (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("triggered").GetInt32());
    }

    [Fact]
    public async Task ReturnsErrorWhenBranchIsMissing()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();
        await SetWebhookSecretAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/push")
        {
            Content = JsonContent.Create(new { repository = new { clone_url = "x" } })
        };
        request.Headers.Add("X-Walis-Token", "top-secret-with-at-least-32-characters");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WebhookFailsClosedWithoutSecret()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/webhooks/push", new
        {
            @ref = "refs/heads/main",
            repository = "https://github.com/example/walis.git"
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ReturnsErrorWhenRepositoryIsMissing()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();
        await SetWebhookSecretAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/push")
        {
            Content = new StringContent("""{"ref":"refs/heads/main"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Walis-Token", "top-secret-with-at-least-32-characters");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task SignatureVerificationUsesRawBytesAndRejectsInvalidUtf8AndOverlongFields()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();
        await SetWebhookSecretAsync(client);

        var invalidUtf8 = Encoding.UTF8.GetBytes(
            """{"ref":"refs/heads/main","repository":"https://github.com/example/walis.git"}""");
        invalidUtf8[8] = 0xff;
        using var invalidRequest = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/push")
        {
            Content = new ByteArrayContent(invalidUtf8)
        };
        invalidRequest.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes("top-secret-with-at-least-32-characters"),
                invalidUtf8)).ToLowerInvariant());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(invalidRequest)).StatusCode);

        var oversized = JsonSerializer.Serialize(new
        {
            branch = new string('x', 256),
            repository = "https://github.com/example/walis.git"
        });
        using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/push")
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json")
        };
        oversizedRequest.Headers.Add("X-Walis-Token", "top-secret-with-at-least-32-characters");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(oversizedRequest)).StatusCode);
    }

    [Fact]
    public async Task SignatureOrTokenIsRequiredOnceSecretIsSet()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();
        await SetWebhookSecretAsync(client);
        await CreatePushJobAsync(client, "main", "https://github.com/example/walis.git");

        var body = """{"ref":"refs/heads/main","repository":"https://github.com/example/walis.git"}""";

        using var unsigned = new StringContent(body, Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/webhooks/push", unsigned)).StatusCode);

        using var withToken = new StringContent(body, Encoding.UTF8, "application/json");
        var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/push") { Content = withToken };
        tokenRequest.Headers.Add("X-Walis-Token", "top-secret-with-at-least-32-characters");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(tokenRequest)).StatusCode);

        var signedBody =
            """{"ref":"refs/heads/main","repository":"https://github.com/example/walis.git","after":"second"}""";
        using var signedContent = new StringContent(signedBody, Encoding.UTF8, "application/json");
        var signature = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes("top-secret-with-at-least-32-characters"),
                Encoding.UTF8.GetBytes(signedBody))).ToLowerInvariant();
        var signedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/push") { Content = signedContent };
        signedRequest.Headers.Add("X-Hub-Signature-256", signature);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(signedRequest)).StatusCode);
    }

    [Fact]
    public async Task ReplayedDeliveryIsRejectedWithRetryTime()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();
        await SetWebhookSecretAsync(client);
        const string body = """{"ref":"refs/heads/main","repository":"https://github.com/example/walis.git"}""";

        async Task<HttpResponseMessage> SendAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/push")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Walis-Token", "top-secret-with-at-least-32-characters");
            request.Headers.Add("X-GitHub-Delivery", "delivery-123");
            return await client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.OK, (await SendAsync()).StatusCode);
        var replay = await SendAsync();

        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.True(replay.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task SameBodyUnderANewDeliveryIdIsStillAReplayAndStaleTimestampsAreRefused()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();
        await SetWebhookSecretAsync(client);
        const string body = """{"ref":"refs/heads/main","repository":"https://github.com/example/walis.git"}""";

        using var first = AuthorizedWebhook(body, "delivery-a");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(first)).StatusCode);

        using var forged = AuthorizedWebhook(body, "delivery-b");
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(forged)).StatusCode);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string freshBody = """{"ref":"refs/heads/main","repository":"https://github.com/example/walis.git","after":"c2"}""";

        using var stale = AuthorizedWebhook(freshBody, "delivery-c");
        stale.Headers.Add(WebhookEndpoints.TimestampHeader, (now - 700).ToString());
        var staleResponse = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Unauthorized, staleResponse.StatusCode);
        Assert.Contains("X-Walis-Timestamp", await staleResponse.Content.ReadAsStringAsync());

        using var future = AuthorizedWebhook(freshBody, "delivery-c");
        future.Headers.Add(WebhookEndpoints.TimestampHeader, (now + 700).ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(future)).StatusCode);

        using var garbage = AuthorizedWebhook(freshBody, "delivery-c");
        garbage.Headers.Add(WebhookEndpoints.TimestampHeader, "yesterday");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(garbage)).StatusCode);

        // A refused stamp never reaches the replay table, so the properly stamped request still goes through.
        using var fresh = AuthorizedWebhook(freshBody, "delivery-c");
        fresh.Headers.Add(WebhookEndpoints.TimestampHeader, now.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(fresh)).StatusCode);
    }

    [Fact]
    public async Task Returns429WhenSingleSourceRateLimitExceeded()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();
        await SetWebhookSecretAsync(client);
        const string body = """{"ref":"refs/heads/main","repository":"https://github.com/example/walis.git"}""";

        // Each push is a distinct event (different commit): the same body under a new delivery id would be a replay.
        for (var index = 0; index < 60; index++)
        {
            using var request = AuthorizedWebhook(body.Replace("}", $",\"after\":\"commit-{index}\"}}"), $"rate-{index}");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }

        using var limitedRequest = AuthorizedWebhook(body.Replace("}", ",\"after\":\"commit-over-limit\"}"), "rate-over-limit");
        var limited = await client.SendAsync(limitedRequest);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    private static HttpRequestMessage AuthorizedWebhook(string body, string delivery)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/push")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Walis-Token", "top-secret-with-at-least-32-characters");
        request.Headers.Add("X-Walis-Delivery", delivery);
        return request;
    }
}

public class WebhookRequestGuardTests
{
    [Fact]
    public async Task ReplayStateIsKeptInDatabaseAcrossInstances()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Walis-Delivery"] = "persistent-delivery";

        var first = new WebhookRequestGuard(database.Factory);
        Assert.Equal(
            WebhookRequestDecision.Allowed,
            (await first.EvaluateAsync(context.Request, "same-body")).Decision);

        var restarted = new WebhookRequestGuard(database.Factory);
        Assert.Equal(
            WebhookRequestDecision.Replay,
            (await restarted.EvaluateAsync(context.Request, "same-body")).Decision);
    }

    [Fact]
    public void ContentHashCatchesReplayWithoutDeliveryHeader()
    {
        var clock = new FakeTime(DateTimeOffset.Parse("2026-09-05T00:00:00Z"));
        var guard = new WebhookRequestGuard(clock, 2, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), 10);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;

        Assert.Equal(WebhookRequestDecision.Allowed, guard.Evaluate(context.Request, "same-body").Decision);
        var replay = guard.Evaluate(context.Request, "same-body");
        Assert.Equal(WebhookRequestDecision.Replay, replay.Decision);
        Assert.True(replay.RetryAfter > TimeSpan.Zero);

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(WebhookRequestDecision.Allowed, guard.Evaluate(context.Request, "same-body").Decision);
    }

    [Fact]
    public void DifferentEventsOverSourceRateLimitAreThrottled()
    {
        var clock = new FakeTime(DateTimeOffset.Parse("2026-09-05T00:00:00Z"));
        var guard = new WebhookRequestGuard(clock, 2, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), 10);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;

        context.Request.Headers["X-Walis-Delivery"] = "one";
        Assert.Equal(WebhookRequestDecision.Allowed, guard.Evaluate(context.Request, "body-one").Decision);
        context.Request.Headers["X-Walis-Delivery"] = "two";
        Assert.Equal(WebhookRequestDecision.Allowed, guard.Evaluate(context.Request, "body-two").Decision);
        context.Request.Headers["X-Walis-Delivery"] = "three";
        var limited = guard.Evaluate(context.Request, "body-three");

        Assert.Equal(WebhookRequestDecision.RateLimited, limited.Decision);
        Assert.Equal(TimeSpan.FromMinutes(1), limited.RetryAfter);
    }

    [Fact]
    public void ReplayIsCaughtByEitherTheDeliveryIdOrTheBody()
    {
        var clock = new FakeTime(DateTimeOffset.Parse("2026-09-05T00:00:00Z"));
        var guard = new WebhookRequestGuard(clock, 10, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), 10);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;

        context.Request.Headers["X-Walis-Delivery"] = "first";
        Assert.Equal(WebhookRequestDecision.Allowed, guard.Evaluate(context.Request, "same-body").Decision);

        // The signature does not cover the delivery header, so a captured body under a fresh id is still the same event.
        context.Request.Headers["X-Walis-Delivery"] = "forged-second";
        Assert.Equal(WebhookRequestDecision.Replay, guard.Evaluate(context.Request, "same-body").Decision);

        // And the provider re-sending its own id with a changed body is a re-delivery too.
        context.Request.Headers["X-Walis-Delivery"] = "first";
        Assert.Equal(WebhookRequestDecision.Replay, guard.Evaluate(context.Request, "other-body").Decision);

        // Both new: a genuinely different event.
        context.Request.Headers["X-Walis-Delivery"] = "third";
        Assert.Equal(WebhookRequestDecision.Allowed, guard.Evaluate(context.Request, "third-body").Decision);

        Assert.Equal(2, WebhookRequestGuard.DeliveryKeys(context.Request, "x").Count);
        context.Request.Headers.Remove("X-Walis-Delivery");
        Assert.Single(WebhookRequestGuard.DeliveryKeys(context.Request, "x"));
    }

    [Fact]
    public async Task PersistentReplayIsCaughtByEitherKeyToo()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var guard = new WebhookRequestGuard(database.Factory);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;

        context.Request.Headers["X-GitHub-Delivery"] = "gh-1";
        Assert.Equal(WebhookRequestDecision.Allowed, (await guard.EvaluateAsync(context.Request, "push-1")).Decision);

        context.Request.Headers["X-GitHub-Delivery"] = "gh-2";
        Assert.Equal(WebhookRequestDecision.Replay, (await guard.EvaluateAsync(context.Request, "push-1")).Decision);

        context.Request.Headers["X-GitHub-Delivery"] = "gh-1";
        Assert.Equal(WebhookRequestDecision.Replay, (await guard.EvaluateAsync(context.Request, "push-2")).Decision);

        context.Request.Headers["X-GitHub-Delivery"] = "gh-2";
        Assert.Equal(WebhookRequestDecision.Allowed, (await guard.EvaluateAsync(context.Request, "push-2")).Decision);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("0", false)]
    [InlineData("abc", false)]
    [InlineData("-599", true)]
    [InlineData("+599", true)]
    [InlineData("+601", false)]
    [InlineData("-601", false)]
    [InlineData("-600", true)]
    public void TimestampMustBeUnixSecondsWithinTenMinutes(string? offset, bool expected)
    {
        var now = DateTimeOffset.Parse("2026-09-05T00:00:00Z");
        var context = new DefaultHttpContext();

        if (offset is not null)
            context.Request.Headers[WebhookEndpoints.TimestampHeader] = offset.StartsWith('+') || offset.StartsWith('-')
                ? (now.ToUnixTimeSeconds() + long.Parse(offset)).ToString()
                : offset;

        Assert.Equal(expected, WebhookEndpoints.HasFreshTimestamp(context.Request, now));
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan value)
        {
            _now += value;
        }
    }
}