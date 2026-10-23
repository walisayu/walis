using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Walis.Api.Services;
using Walis.Core.Backup;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

/// <summary>Structured build data, event ids and HMAC signatures on webhook notifications.</summary>
public sealed class NotificationWebhookTests
{
    private const string Secret = "notification-signing-secret-with-32-plus-characters";

    [Fact]
    public async Task BuildEventWebhookCarriesEventIdAndBuildObjectAndKeepsLegacyFields()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var (dispatcher, handler) = await CreateAsync(database, NotificationChannelKind.Webhook);
        var (job, build) = FailedBuild();

        await dispatcher.DispatchBuildCompletedAsync(job, build, BuildStatus.Succeeded, false, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        var payload = JsonDocument.Parse(request.Body).RootElement;

        Assert.Equal(NotificationEventKind.BuildFailed, payload.GetProperty("event").GetString());
        Assert.Equal($"build.failed:{build.Id}", payload.GetProperty("eventId").GetString());
        Assert.StartsWith("建置失敗", payload.GetProperty("title").GetString());
        Assert.Contains("任務：", payload.GetProperty("body").GetString());
        Assert.Equal(JsonValueKind.String, payload.GetProperty("url").ValueKind);
        Assert.Equal(DateTimeKind.Utc, payload.GetProperty("sentAt").GetDateTime().Kind);

        var data = payload.GetProperty("build");
        Assert.Equal(build.Id, data.GetProperty("id").GetGuid());
        Assert.Equal(job.Id, data.GetProperty("jobId").GetGuid());
        Assert.Equal(job.Name, data.GetProperty("jobName").GetString());
        Assert.Equal(7, data.GetProperty("number").GetInt32());
        Assert.Equal(BuildStatus.Failed, data.GetProperty("status").GetString());
        Assert.Equal("agentgate:run-42", data.GetProperty("externalRef").GetString());
        Assert.Equal("NODE=22", data.GetProperty("matrixLabel").GetString());
        Assert.Equal(build.StartedAt, data.GetProperty("startedAt").GetDateTime().ToUniversalTime());
        Assert.Equal(build.FinishedAt, data.GetProperty("finishedAt").GetDateTime().ToUniversalTime());
        Assert.Equal(2, data.GetProperty("exitCode").GetInt32());

        // Only the agreed fields: no log, parameters or anything secret leaks through the build object.
        Assert.Equal(
            ["id", "jobId", "jobName", "number", "status", "externalRef", "matrixLabel", "startedAt", "finishedAt", "exitCode"],
            data.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task SignedWebhookCanBeVerifiedWithTheSharedSecret()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var (dispatcher, handler) = await CreateAsync(database, NotificationChannelKind.Webhook, Secret);
        var (job, build) = FailedBuild();

        await dispatcher.DispatchBuildCompletedAsync(job, build, null, true, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        var signature = Assert.Single(request.Headers[NotificationSignature.SignatureHeader]);
        var parts = signature.Split(',').Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1]);

        var timestamp = long.Parse(parts["t"], CultureInfo.InvariantCulture);
        Assert.InRange(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp, 0, 60);

        // Verified independently of the production helper, exactly as a receiver would.
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret),
            Encoding.UTF8.GetBytes($"{parts["t"]}.{request.Body}")));
        Assert.Equal(expected, parts["v1"]);
        Assert.True(CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts["v1"])));

        var eventId = JsonDocument.Parse(request.Body).RootElement.GetProperty("eventId").GetString();
        Assert.Equal($"build.timeout:{build.Id}", eventId);
        Assert.Equal(eventId, Assert.Single(request.Headers[NotificationSignature.EventIdHeader]));

        // A tampered body no longer matches.
        Assert.NotEqual(parts["v1"], NotificationSignature.Compute(Secret, timestamp, request.Body + " "));
    }

    [Fact]
    public async Task UnsignedWebhookHasNoSignatureHeadersAndTestEventsGetFreshIds()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var (dispatcher, handler) = await CreateAsync(database, NotificationChannelKind.Webhook);
        var channel = (await database.Notifications.ListEnabledChannelsAsync()).Single();

        await dispatcher.SendTestAsync(channel, CancellationToken.None);
        await dispatcher.SendTestAsync(channel, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.False(request.Headers.ContainsKey(NotificationSignature.SignatureHeader));
            Assert.False(request.Headers.ContainsKey(NotificationSignature.EventIdHeader));
        });

        var payloads = handler.Requests.Select(request => JsonDocument.Parse(request.Body).RootElement).ToList();
        var ids = payloads.Select(payload => payload.GetProperty("eventId").GetString()).ToList();
        Assert.All(ids, id => Assert.True(Guid.TryParse(id, out _)));
        Assert.NotEqual(ids[0], ids[1]);
        Assert.All(payloads, payload => Assert.False(payload.TryGetProperty("build", out _)));
        Assert.All(payloads, payload => Assert.Equal("test", payload.GetProperty("event").GetString()));
    }

    [Fact]
    public async Task SlackPayloadIsUnchangedAndUnsignedEvenWithSecret()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var (dispatcher, handler) = await CreateAsync(database, NotificationChannelKind.Slack, Secret);
        var (job, build) = FailedBuild();

        await dispatcher.DispatchBuildCompletedAsync(job, build, null, false, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        var payload = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal(["text"], payload.EnumerateObject().Select(property => property.Name));
        Assert.False(request.Headers.ContainsKey(NotificationSignature.SignatureHeader));
    }

    [Fact]
    public void SignatureFormatMatchesTheDocumentedScheme()
    {
        const string body = """{"event":"test"}""";

        var header = NotificationSignature.Create("k", 1_700_000_000, body);

        var expected = Convert.ToHexStringLower(HMACSHA256.HashData("k"u8, Encoding.UTF8.GetBytes($"1700000000.{body}")));
        Assert.Equal($"t=1700000000,v1={expected}", header);
    }

    private static (Job Job, Build Build) FailedBuild()
    {
        var job = new Job { Id = Guid.NewGuid(), Name = "release", Branch = "main", NotifyOnFailure = true };
        var started = new DateTime(2026, 10, 3, 1, 2, 3, DateTimeKind.Utc);
        var build = new Build
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            JobName = job.Name,
            Number = 7,
            Status = BuildStatus.Failed,
            StartedAt = started,
            FinishedAt = started.AddSeconds(42),
            ExitCode = 2,
            MatrixLabel = "NODE=22",
            ExternalRef = "agentgate:run-42",
            TriggerHash = "should-never-leave",
            Log = "secret log output",
            ParametersJson = """{"ENV":"prod"}"""
        };
        return (job, build);
    }

    private static async Task<(NotificationDispatcher Dispatcher, CapturingHandler Handler)> CreateAsync(
        TemporaryDatabase database,
        string kind,
        string? secret = null)
    {
        var settings = await database.Settings.GetAsync();
        settings.NotificationSigningSecret = secret;
        settings.BaseUrl = "https://walis.example.test";
        await database.Settings.SaveAsync(settings);

        await database.Notifications.CreateChannelAsync(
            new NotificationChannelDraft(kind, "hook", "https://hooks.example.test/notify", true));

        var handler = new CapturingHandler();
        var dispatcher = new NotificationDispatcher(
            database.Notifications,
            database.Settings,
            new StubHttpClientFactory(handler),
            NullLogger<NotificationDispatcher>.Instance);
        return (dispatcher, handler);
    }

    private sealed record CapturedRequest(string Body, IReadOnlyDictionary<string, string[]> Headers);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                body,
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(handler, false);
        }
    }
}

/// <summary>NotificationSigningSecret is handled like the other secrets in the system settings.</summary>
public sealed class NotificationSigningSecretSettingsTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SigningSecretIsMaskedValidatedAndKeptWhenMaskIsSentBack()
    {
        var client = factory.CreateClient();
        var settings = await LoadAsync(client);

        settings.NotificationSigningSecret = "too-short";
        var weak = await client.PutAsJsonAsync("/api/settings", settings);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("notificationSigningSecret", await weak.Content.ReadAsStringAsync());

        settings.NotificationSigningSecret = new string('s', 1025);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings", settings)).StatusCode);

        const string secret = "notification-signing-secret-with-32-plus-characters";
        settings.NotificationSigningSecret = secret;
        var saved = await client.PutAsJsonAsync("/api/settings", settings);
        saved.EnsureSuccessStatusCode();
        Assert.DoesNotContain(secret, await saved.Content.ReadAsStringAsync());

        var raw = await client.GetStringAsync("/api/settings");
        Assert.DoesNotContain(secret, raw);
        var masked = await LoadAsync(client);
        Assert.Equal(BackupTarget.MaskedValue, masked.NotificationSigningSecret);

        // Sending the mask back unchanged keeps the stored value.
        (await client.PutAsJsonAsync("/api/settings", masked)).EnsureSuccessStatusCode();
        var repository = factory.Services.GetRequiredService<ISettingsRepository>();
        Assert.Equal(secret, (await repository.GetAsync()).NotificationSigningSecret);

        // Clearing it turns signing off.
        masked.NotificationSigningSecret = "";
        (await client.PutAsJsonAsync("/api/settings", masked)).EnsureSuccessStatusCode();
        Assert.Null((await repository.GetAsync()).NotificationSigningSecret);
    }

    [Fact]
    public void BackupSettingsProtectorMasksAndRestoresSigningSecret()
    {
        var stored = new WalisSettings { NotificationSigningSecret = "stored-secret-value-with-32-characters!" };

        var masked = BackupSettingsProtector.Mask(stored);
        Assert.Equal(BackupTarget.MaskedValue, masked.NotificationSigningSecret);
        Assert.Equal("stored-secret-value-with-32-characters!", stored.NotificationSigningSecret);

        var merged = BackupSettingsProtector.Merge(
            new WalisSettings { NotificationSigningSecret = BackupTarget.MaskedValue }, stored);
        Assert.Equal(stored.NotificationSigningSecret, merged.NotificationSigningSecret);
    }

    private static async Task<WalisSettings> LoadAsync(HttpClient client)
    {
        var current = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        return current.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
    }
}
