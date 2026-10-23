using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Walis.Api.Endpoints;
using Walis.Api.Services;
using Walis.Core.Backup;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

/// <summary>
///     Audit coverage after the security review: sign-in failures and lockouts, triggers from every source, cancel/reveal,
///     settings changes, paging and the CSV export. Sign-in is turned on, so the class has its own database.
/// </summary>
public class AuditApiTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    private const string Password = "walis-password";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static bool _ready;

    [Fact]
    public async Task FiveWrongPasswordsLeaveFiveFailuresAndOneLockoutWithAddress()
    {
        var (client, admin) = await PrepareAsync();
        var username = $"lk{Guid.NewGuid():N}"[..12];
        await factory.Services.GetRequiredService<ISecurityRepository>()
            .CreateUserAsync(new UserDraft(username, username, Password, UserRole.Viewer));

        for (var attempt = 0; attempt < SignInThrottle.AccountLimit; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync("/api/auth/login", new { username, password = "wrong-password" })).StatusCode);

        var rows = (await Audit().ListAsync(500)).Where(entry => entry.Actor == username).ToList();
        var failures = rows.Where(entry => entry.Action == AuditAction.SignInFailed).ToList();
        var lockouts = rows.Where(entry => entry.Action == AuditAction.LockedOut).ToList();

        Assert.Equal(SignInThrottle.AccountLimit, failures.Count);
        Assert.Single(lockouts);
        Assert.All(rows, entry =>
        {
            Assert.Equal(AuditOutcome.Denied, entry.Outcome);
            Assert.Equal("127.0.0.1", entry.RemoteAddress);
            Assert.False(string.IsNullOrEmpty(entry.RequestId));
        });
        Assert.Contains(SecurityEventService.ReasonWrongPassword, failures[0].Summary);

        // The counters are exposed for Prometheus; the audit log remains the durable record.
        var metrics = await (await Send(client, HttpMethod.Get, "/metrics", admin)).Content.ReadAsStringAsync();
        Assert.Contains("walis_sign_in_failures_total", metrics);
        Assert.Contains("walis_lockouts_total", metrics);
        Assert.True(factory.Services.GetRequiredService<SecurityMetrics>().Lockouts >= 1);
    }

    [Fact]
    public async Task UnknownAccountFailureMasksTheSubmittedName()
    {
        var (client, _) = await PrepareAsync();
        // What looks like a username here may be a password typed into the wrong field: it must not be stored whole.
        var submitted = $"no\u001b[2Jbody{Guid.NewGuid():N}"[..20];

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login", new { username = submitted, password = "whatever-1" })).StatusCode);

        var expected = SecurityEventService.MaskUnknownActor(submitted);
        var entry = Assert.Single(await Audit().ListAsync(500),
            entry => entry.Action == AuditAction.SignInFailed && entry.Summary.Contains(SecurityEventService.ReasonUnknownAccount) &&
                     entry.Actor == expected);
        Assert.StartsWith("未知帳號（", entry.Actor);
        Assert.DoesNotContain(submitted[^8..], entry.Actor);
        Assert.DoesNotContain('\u001b', entry.Actor);
    }

    [Fact]
    public async Task RefusedRequestsAreAuditedOnceAMinuteAndCountedEveryTime()
    {
        var (client, admin) = await PrepareAsync();
        var metrics = factory.Services.GetRequiredService<SecurityMetrics>();
        var before = metrics.AccessDenied[AccessDeniedReason.ApiKeyInvalid];

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/jobs");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", "walis_dead00_0123456789abcdefghijklmn");
            request.Headers.Remove("X-Walis-Reauth");
            request.Headers.Add("X-Walis-Reauth", "walis-password");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
        }

        Assert.Equal(before + 3, metrics.AccessDenied[AccessDeniedReason.ApiKeyInvalid]);
        var rows = (await Audit().ListAsync(500))
            .Where(entry => entry.Action == AuditAction.AccessDenied && entry.EntityId == AccessDeniedReason.ApiKeyInvalid)
            .ToList();
        var row = Assert.Single(rows);
        Assert.Equal(AuditOutcome.Denied, row.Outcome);
        Assert.Contains("/api/jobs", row.Summary);

        var exposition = await (await Send(client, HttpMethod.Get, "/metrics", admin)).Content.ReadAsStringAsync();
        Assert.Contains("walis_access_denied_total{reason=\"api_key_invalid\"}", exposition);
    }

    [Fact]
    public async Task SignInSuccessRecordsAccountAsActor()
    {
        var (client, _) = await PrepareAsync();
        var username = $"ok{Guid.NewGuid():N}"[..12];
        await factory.Services.GetRequiredService<ISecurityRepository>()
            .CreateUserAsync(new UserDraft(username, username, Password, UserRole.Viewer));

        (await client.PostAsJsonAsync("/api/auth/login", new { username, password = Password })).EnsureSuccessStatusCode();

        var entry = Assert.Single(await Audit().ListAsync(500), entry => entry.Action == AuditAction.SignIn && entry.Actor == username);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Equal("127.0.0.1", entry.RemoteAddress);
    }

    [Fact]
    public async Task McpTriggerIsAuditedWithKeyNameAndJobId()
    {
        var (client, admin) = await PrepareAsync();
        var job = await CreateJobAsync(client, admin, $"mcp-{Guid.NewGuid():N}"[..16]);
        var keyName = $"agent-{Guid.NewGuid():N}"[..14];
        var created = await Send(client, HttpMethod.Post, "/api/api-keys/", admin, new { name = keyName, role = "operator" });
        created.EnsureSuccessStatusCode();
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;

        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new { name = "trigger_build", arguments = new { job = job.GetProperty("id").GetString() } }
            }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove("X-Walis-Reauth");
        request.Headers.Add("X-Walis-Reauth", "walis-password");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // The transport answers as an SSE stream whose JSON escapes non-ASCII; decode the data line before looking at the text.
        var body = await response.Content.ReadAsStringAsync();
        var data = body.Split('\n').First(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        var text = JsonDocument.Parse(data).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("已排入佇列", text);

        var jobId = job.GetProperty("id").GetGuid();
        var entry = Assert.Single(await Audit().ListAsync(500), entry => entry.Action == AuditAction.Run && entry.JobId == jobId);
        Assert.Equal($"金鑰：{keyName}", entry.Actor);
        Assert.Contains("MCP", entry.Summary);
        Assert.Equal("build", entry.EntityType);
    }

    [Fact]
    public async Task ScheduleTriggerIsAuditedAsSystem()
    {
        var (client, admin) = await PrepareAsync();
        var job = await CreateJobAsync(client, admin, $"cron-{Guid.NewGuid():N}"[..16]);
        var jobId = job.GetProperty("id").GetGuid();

        var result = await factory.Services.GetRequiredService<BuildLauncher>().LaunchAsync(jobId, "schedule", CancellationToken.None);
        Assert.Null(result.Error);

        var entry = Assert.Single(await Audit().ListAsync(500), entry => entry.Action == AuditAction.Run && entry.JobId == jobId);
        Assert.Equal("system", entry.Actor);
        Assert.Contains("排程", entry.Summary);
        Assert.Null(entry.RemoteAddress);
    }

    [Fact]
    public async Task CancelAndRevealAreAudited()
    {
        var (client, admin) = await PrepareAsync();

        // A job bound to a remote runner stays queued until that runner claims it, so cancelling cannot race the local executor.
        var runnerName = $"r{Guid.NewGuid():N}"[..10];
        (await Send(client, HttpMethod.Post, "/api/runners/", admin, new { name = runnerName, totalSlots = 1, isPaused = false }))
            .EnsureSuccessStatusCode();
        var job = await CreateJobAsync(client, admin, $"cancel-{Guid.NewGuid():N}"[..16], runnerName);
        var jobId = job.GetProperty("id").GetGuid();

        var triggered = await Send(client, HttpMethod.Post, $"/api/jobs/{jobId}/builds", admin, new { });
        Assert.Equal(HttpStatusCode.Accepted, triggered.StatusCode);
        var buildId = (await triggered.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();

        (await Send(client, HttpMethod.Post, $"/api/builds/{buildId}/cancel", admin)).EnsureSuccessStatusCode();

        var cancel = Assert.Single(await Audit().ListAsync(500),
            entry => entry.Action == AuditAction.Cancel && entry.EntityId == buildId.ToString());
        Assert.Equal(jobId, cancel.JobId);
        Assert.Equal("root", cancel.Actor);

        (await Send(client, HttpMethod.Get, "/api/environment-variables/?reveal=true", admin)).EnsureSuccessStatusCode();

        var reveal = (await Audit().ListAsync(500)).First(entry => entry.Action == AuditAction.Reveal);
        Assert.Equal("variable", reveal.EntityType);
        Assert.Contains("TOP_SECRET", reveal.Summary);
        Assert.DoesNotContain("hunter2", reveal.Summary);

        // The plain listing is not a reveal and leaves no such record.
        var before = (await Audit().ListAsync(500)).Count(entry => entry.Action == AuditAction.Reveal);
        (await Send(client, HttpMethod.Get, "/api/environment-variables/", admin)).EnsureSuccessStatusCode();
        Assert.Equal(before, (await Audit().ListAsync(500)).Count(entry => entry.Action == AuditAction.Reveal));
    }

    [Fact]
    public async Task SettingsChangeNamesFieldsAndAuditToggleIsAlwaysRecorded()
    {
        var (client, admin) = await PrepareAsync();
        var settings = await LoadSettingsAsync(client, admin);
        settings.SiteName = $"site-{Guid.NewGuid():N}"[..12];
        settings.AuditLog = false;
        settings.WebhookSecret = "a-brand-new-webhook-secret-of-32-chars-X";
        (await Send(client, HttpMethod.Put, "/api/settings/", admin, settings)).EnsureSuccessStatusCode();

        var rows = await Audit().ListAsync(500);
        var update = rows.First(entry => entry.EntityType == "settings" && entry.EntityId is null);
        Assert.Contains("siteName", update.Summary);
        Assert.Contains("auditLog", update.Summary);
        Assert.Contains("webhookSecret", update.Summary);
        Assert.DoesNotContain("a-brand-new-webhook-secret", update.Summary);
        Assert.DoesNotContain(settings.SiteName, update.Summary);
        Assert.Contains(rows, entry => entry.EntityId == "auditLog" && entry.Summary == "關閉稽核紀錄");
        Assert.Contains(rows, entry => entry.EntityId == "webhookSecret" && entry.Summary == "變更 Webhook 密鑰");

        // While auditing is off ordinary changes are not recorded, but turning it back on is.
        settings = await LoadSettingsAsync(client, admin);
        settings.SiteName = "quiet";
        (await Send(client, HttpMethod.Put, "/api/settings/", admin, settings)).EnsureSuccessStatusCode();
        Assert.DoesNotContain(await Audit().ListAsync(500), entry => entry.Summary.Contains("siteName") && entry.OccurredAt > update.OccurredAt);

        settings = await LoadSettingsAsync(client, admin);
        settings.AuditLog = true;
        (await Send(client, HttpMethod.Put, "/api/settings/", admin, settings)).EnsureSuccessStatusCode();
        Assert.Contains(await Audit().ListAsync(500), entry => entry.EntityId == "auditLog" && entry.Summary == "開啟稽核紀錄");
    }

    [Fact]
    public async Task AuditRetentionHasItsOwnFloorAndDefault()
    {
        var (client, admin) = await PrepareAsync();
        var settings = await LoadSettingsAsync(client, admin);
        Assert.Equal(WalisSettings.DefaultAuditRetentionDays, settings.AuditRetentionDays);

        settings.AuditRetentionDays = WalisSettings.MinimumAuditRetentionDays - 1;
        var response = await Send(client, HttpMethod.Put, "/api/settings/", admin, settings);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("auditRetentionDays", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PagedQueryFiltersByEntityAndCsvExportIsGuarded()
    {
        var (client, admin) = await PrepareAsync();
        var job = await CreateJobAsync(client, admin, $"=page-{Guid.NewGuid():N}"[..16]);
        var jobId = job.GetProperty("id").GetString()!;

        var page = await (await Send(client, HttpMethod.Get, $"/api/audit/page?entityType=job&entityId={jobId}&pageSize=10", admin))
            .Content.ReadFromJsonAsync<JsonElement>(Json);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.All(items, item => Assert.Equal(jobId, item.GetProperty("entityId").GetString()));
        Assert.Equal("success", items[0].GetProperty("outcome").GetString());
        Assert.Equal("127.0.0.1", items[0].GetProperty("remoteAddress").GetString());

        var bad = await Send(client, HttpMethod.Get, "/api/audit/page?from=2026-01-02T00:00:00Z&to=2026-01-01T00:00:00Z", admin);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var export = await Send(client, HttpMethod.Get, $"/api/audit/export.csv?entityId={jobId}", admin);
        export.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        var csv = await export.Content.ReadAsStringAsync();
        Assert.StartsWith("﻿occurredAt,actor,action,entityType,entityId,jobId,outcome,remoteAddress,requestId,summary", csv);
        Assert.Contains($"\"{jobId}\"", csv);
        Assert.DoesNotContain("\n\"", csv.Replace("\r\n", "\n").Replace("\n\"20", string.Empty));

        // Non-admins never see the audit log, paged or exported.
        var viewer = await LoginAsync(client, "vw", Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Get, "/api/audit/page", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Get, "/api/audit/export.csv", viewer)).StatusCode);
    }

    [Fact]
    public async Task NamesWithControlCharactersAreRejected()
    {
        var (client, admin) = await PrepareAsync();

        var job = await Send(client, HttpMethod.Post, "/api/jobs/", admin,
            new { name = "line\nbreak", command = "echo hi", triggerKind = "manual", timeoutMinutes = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, job.StatusCode);

        var channel = await Send(client, HttpMethod.Post, "/api/notifications/channels", admin,
            new { kind = "webhook", name = "hook\u001b[0m", target = "https://hooks.example.test/x", isEnabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, channel.StatusCode);

        var account = await Send(client, HttpMethod.Post, "/api/accounts/", admin,
            new { username = "bad\u0007name", password = Password, role = "viewer" });
        Assert.Equal(HttpStatusCode.BadRequest, account.StatusCode);

        var locale = await Send(client, HttpMethod.Put, $"/api/i18n/locales/{new string('x', 40)}", admin,
            new { name = "Long", nativeName = "Long", isEnabled = true, sortOrder = 9 });
        Assert.Equal(HttpStatusCode.BadRequest, locale.StatusCode);
    }

    [Fact]
    public async Task ChannelRunnerAndExportEventsAreAudited()
    {
        var (client, admin) = await PrepareAsync();
        var name = $"hook-{Guid.NewGuid():N}"[..14];

        var created = await Send(client, HttpMethod.Post, "/api/notifications/channels", admin,
            new { kind = "webhook", name, target = "https://hooks.example.test/x", isEnabled = false });
        created.EnsureSuccessStatusCode();
        var channelId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString();
        (await Send(client, HttpMethod.Delete, $"/api/notifications/channels/{channelId}", admin)).EnsureSuccessStatusCode();

        var runnerName = $"p{Guid.NewGuid():N}"[..10];
        var runner = await Send(client, HttpMethod.Post, "/api/runners/", admin, new { name = runnerName, totalSlots = 1, isPaused = false });
        runner.EnsureSuccessStatusCode();
        var runnerId = (await runner.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString();
        (await Send(client, HttpMethod.Patch, $"/api/runners/{runnerId}/paused", admin, new { paused = true })).EnsureSuccessStatusCode();

        (await Send(client, HttpMethod.Get, "/api/portability/export", admin)).EnsureSuccessStatusCode();

        var rows = await Audit().ListAsync(500);
        Assert.Contains(rows, entry => entry.Action == AuditAction.Create && entry.EntityType == "channel" && entry.Summary.Contains(name));
        Assert.Contains(rows, entry => entry.Action == AuditAction.Delete && entry.EntityType == "channel" && entry.Summary.Contains(name));
        Assert.Contains(rows, entry => entry.EntityType == "runner" && entry.Summary == $"暫停執行器 {runnerName}");
        Assert.Contains(rows, entry => entry.Action == AuditAction.Export && entry.EntityType == "job");
    }

    private IAuditRepository Audit()
    {
        return factory.Services.GetRequiredService<IAuditRepository>();
    }

    private static async Task<WalisSettings> LoadSettingsAsync(HttpClient client, string admin)
    {
        var payload = await (await Send(client, HttpMethod.Get, "/api/settings/", admin)).Content.ReadFromJsonAsync<JsonElement>(Json);
        return payload.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
    }

    private static async Task<JsonElement> CreateJobAsync(HttpClient client, string admin, string name, string? runnerName = null)
    {
        var response = await Send(client, HttpMethod.Post, "/api/jobs/", admin,
            new { name, command = "echo hi", triggerKind = "manual", timeoutMinutes = 5, runnerName });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private async Task<(HttpClient Client, string Admin)> PrepareAsync()
    {
        var client = factory.CreateClient();

        await SetupLock.WaitAsync();

        try
        {
            if (!_ready)
            {
                var setup = await client.PostAsJsonAsync("/api/auth/setup?bearer=true", new { username = "root", password = Password, displayName = "root" });
                setup.EnsureSuccessStatusCode();
                var admin = (await setup.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;

                (await Send(client, HttpMethod.Post, "/api/accounts/", admin,
                    new { username = "vw", password = Password, role = "viewer" })).EnsureSuccessStatusCode();
                (await Send(client, HttpMethod.Post, "/api/environment-variables/", admin,
                    new { name = "TOP_SECRET", value = "hunter2-XYZ", isSecret = true })).EnsureSuccessStatusCode();

                _ready = true;
            }
        }
        finally
        {
            SetupLock.Release();
        }

        return (client, await LoginAsync(client, "root", Password));
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string password)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login?bearer=true", new { username, password });
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove("X-Walis-Reauth");
        request.Headers.Add("X-Walis-Reauth", "walis-password");

        if (body is not null) request.Content = JsonContent.Create(body, options: Json);

        return await client.SendAsync(request);
    }
}

public class AuditUnitTests
{
    [Fact]
    public void CleanReplacesControlCharactersExceptTabAndCapsLength()
    {
        Assert.Equal("a b\tc d", AuditText.Clean("a\nb\tc\u001bd", 100));
        Assert.Equal(new string('x', 2000), AuditText.Clean(new string('x', 5000), AuditText.MaximumSummaryLength));
        Assert.Null(AuditText.CleanOrNull(null, 10));
        Assert.True(AuditText.HasControlCharacters("x\r\ny"));
        Assert.False(AuditText.HasControlCharacters("plain name"));
    }

    [Fact]
    public void CsvCellsStripControlCharactersAndGuardFormulas()
    {
        Assert.Equal("\"'=SUM(A1)\"", AuditEndpoints.Cell("=SUM(A1)"));
        Assert.Equal("\"'+1\"", AuditEndpoints.Cell("+1"));
        Assert.Equal("\"'-1\"", AuditEndpoints.Cell("-1"));
        Assert.Equal("\"'@cmd\"", AuditEndpoints.Cell("@cmd"));
        Assert.Equal("\"onetwo\"", AuditEndpoints.Cell("one\r\ntwo"));
        Assert.Equal("\"say \"\"hi\"\"\"", AuditEndpoints.Cell("say \"hi\""));
        Assert.Equal("\"\"", AuditEndpoints.Cell(null));
    }

    [Fact]
    public void SettingsChangeDescriberListsNamesOnlyAndDedicatedSecurityRecords()
    {
        var before = new WalisSettings { SmtpPassword = "old-pass" };
        var after = new WalisSettings
        {
            SiteName = "changed",
            SmtpPassword = "new-pass",
            AuditLog = false,
            AuditRetentionDays = 40,
            LogRetentionDays = 10,
            RequireOtp = true,
            NotificationSigningSecret = "sign-secret-with-at-least-32-characters",
            AllowUnisolatedBuilds = false
        };

        var changed = SettingsChangeDescriber.ChangedFields(before, after);
        Assert.Equal(["siteName", "logRetentionDays", "requireOtp", "auditLog", "auditRetentionDays", "allowUnisolatedBuilds", "notificationSigningSecret", "smtpPassword"],
            changed.OrderBy(name => Array.IndexOf(new[]
            {
                "siteName", "logRetentionDays", "requireOtp", "auditLog", "auditRetentionDays", "allowUnisolatedBuilds",
                "notificationSigningSecret", "smtpPassword"
            }, name)).ToArray());

        var summary = SettingsChangeDescriber.Describe(changed).Summary;
        Assert.DoesNotContain("new-pass", summary);
        Assert.DoesNotContain("changed", summary);
        Assert.Contains("smtpPassword", summary);

        var dedicated = SettingsChangeDescriber.DescribeSecurityChanges(before, after).ToList();
        Assert.Contains(dedicated, item => item.EntityId == "auditLog" && item.Force && item.Summary == "關閉稽核紀錄");
        Assert.Contains(dedicated, item => item.EntityId == "auditRetentionDays" && item.Summary.Contains("365") && item.Summary.Contains("40"));
        Assert.Contains(dedicated, item => item.EntityId == "logRetentionDays");
        Assert.Contains(dedicated, item => item.EntityId == "requireOtp");
        Assert.Contains(dedicated, item => item.EntityId == "notificationSigningSecret" && !item.Summary.Contains("sign-secret"));
        Assert.Contains(dedicated, item => item.EntityId == "allowUnisolatedBuilds");
        Assert.Empty(SettingsChangeDescriber.DescribeSecurityChanges(after, after));
        Assert.Equal("更新系統設定（沒有欄位變更）", SettingsChangeDescriber.Describe([]).Summary);
    }

    [Fact]
    public async Task ThrottleReportsLockoutOnlyWhenTheFailureIsConfirmed()
    {
        var sink = new RecordingSink();
        var throttle = new SignInThrottle { LockoutSink = sink };

        for (var attempt = 0; attempt < SignInThrottle.AccountLimit - 1; attempt++)
        {
            var early = await throttle.BeginAttemptAsync("leo", "203.0.113.1");
            Assert.Null(early.RetryAfter);
            Assert.False(early.AccountLocked);
            await throttle.ConfirmFailureAsync(early, "leo", "203.0.113.1");
        }

        Assert.Empty(sink.Lockouts);

        var last = await throttle.BeginAttemptAsync("leo", "203.0.113.1");
        Assert.Null(last.RetryAfter);
        Assert.True(last.AccountLocked);
        await throttle.ConfirmFailureAsync(last, "leo", "203.0.113.1");

        var lockout = Assert.Single(sink.Lockouts);
        Assert.Equal("leo", lockout.Account);
        Assert.False(lockout.AddressLocked);
        Assert.NotNull((await throttle.BeginAttemptAsync("leo", "203.0.113.1")).RetryAfter);

        // A correct password on the attempt that reaches the limit hands the reservation back: no lockout is reported.
        var other = new SignInThrottle { LockoutSink = sink };
        for (var attempt = 0; attempt < SignInThrottle.AccountLimit - 1; attempt++)
            await other.ConfirmFailureAsync(await other.BeginAttemptAsync("amy", null), "amy", null);
        var success = await other.BeginAttemptAsync("amy", null);
        Assert.True(success.AccountLocked);
        await other.RecordSuccessAsync("amy", null);
        Assert.Single(sink.Lockouts);
        Assert.Null((await other.BeginAttemptAsync("amy", null)).RetryAfter);
    }

    [Fact]
    public async Task RecordNeverThrowsAndIgnoresTheCallersCancellation()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var failing = new AuditService(new ThrowingAuditRepository(), database.Settings, new HttpContextAccessor(),
            NullLogger<AuditService>.Instance);

        await failing.RecordAsync(AuditAction.Update, "job", "x", "must not throw");

        var service = new AuditService(database.Audit, database.Settings, new HttpContextAccessor(), NullLogger<AuditService>.Instance);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await service.RecordAsync(AuditAction.Update, "job", "x", "written despite a cancelled request", cancelled.Token);

        var entry = Assert.Single(await database.Audit.ListAsync(10));
        Assert.Equal("system", entry.Actor);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
    }

    [Fact]
    public async Task ActorFallsBackToSessionAndCaptureSurvivesTheRequest()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        accessor.HttpContext.Items[RequestAccess.SessionKey] = new AuthSession { Username = "leo", Role = UserRole.Admin };
        accessor.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        var service = new AuditService(database.Audit, database.Settings, accessor, NullLogger<AuditService>.Instance);

        var captured = service.Capture();
        accessor.HttpContext = null;

        await service.RecordAsync(new AuditEvent(AuditAction.Run, "backup", null, "後台") { Context = captured });

        var entry = Assert.Single(await database.Audit.ListAsync(10));
        Assert.Equal("leo", entry.Actor);
        Assert.Equal("203.0.113.9", entry.RemoteAddress);
    }

    [Fact]
    public async Task RepositoryTruncatesOverlongValuesAndPagesStably()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var index = 0; index < 5; index++)
            await database.Audit.AppendAsync(new AuditEntry
            {
                OccurredAt = stamp,
                Actor = index % 2 == 0 ? "even" : "odd",
                Action = AuditAction.Update,
                EntityType = "job",
                EntityId = new string('e', 500),
                Summary = $"row {index}\n" + new string('s', 3000)
            });

        var all = await database.Audit.ListAsync(10);
        Assert.All(all, entry =>
        {
            Assert.Equal(AuditText.MaximumEntityIdLength, entry.EntityId!.Length);
            Assert.Equal(AuditText.MaximumSummaryLength, entry.Summary.Length);
            Assert.DoesNotContain('\n', entry.Summary);
        });

        var first = await database.Audit.QueryAsync(new AuditQuery(Page: 1, PageSize: 2));
        var second = await database.Audit.QueryAsync(new AuditQuery(Page: 2, PageSize: 2));
        var third = await database.Audit.QueryAsync(new AuditQuery(Page: 3, PageSize: 2));
        Assert.Equal(5, first.Total);
        var ids = first.Items.Concat(second.Items).Concat(third.Items).Select(entry => entry.Id).ToList();
        Assert.Equal(5, ids.Distinct().Count());

        Assert.Equal(3, (await database.Audit.QueryAsync(new AuditQuery(Actor: "even"))).Total);
        Assert.Equal(0, (await database.Audit.QueryAsync(new AuditQuery(From: stamp.AddDays(1)))).Total);
        Assert.Equal(5, (await database.Audit.QueryAsync(new AuditQuery(From: stamp, To: stamp.AddSeconds(1)))).Total);
    }

    [Fact]
    public async Task RestoreKeepsExistingAuditRowsAndAddsTheArchivesMissingOnes()
    {
        await using var source = await TemporaryDatabase.CreateAsync();
        await using var target = await TemporaryDatabase.CreateAsync();
        var archived = Guid.NewGuid();
        await source.Audit.AppendAsync(new AuditEntry { Id = archived, Actor = "a", Action = AuditAction.Create, EntityType = "job", Summary = "備份裡的" });
        var kept = Guid.NewGuid();
        await target.Audit.AppendAsync(new AuditEntry { Id = kept, Actor = "b", Action = AuditAction.Delete, EntityType = "job", Summary = "備份之後的" });

        using var dump = new MemoryStream();
        await new DatabaseDump(source.Factory).ExportAsync(dump);
        dump.Position = 0;
        var result = await new DatabaseDump(target.Factory).ImportAsync(dump);

        Assert.Equal(1, result.AuditRowsKept);
        Assert.Equal(1, result.AuditRowsAdded);
        var rows = await target.Audit.ListAsync(10);
        Assert.Contains(rows, entry => entry.Id == kept);
        Assert.Contains(rows, entry => entry.Id == archived);
    }

    [Fact]
    public void SecurityAlertIsEnabledByDefaultAndSwitchable()
    {
        Assert.True(new WalisSettings().IsEventEnabled(NotificationEventKind.SecurityAlert));
        Assert.False(new WalisSettings { NotifyOnSecurityAlert = false }.IsEventEnabled(NotificationEventKind.SecurityAlert));
    }

    [Fact]
    public async Task SecurityAlertReachesChannelsEvenDuringQuietHours()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var settings = await database.Settings.GetAsync();
        settings.QuietHoursStart = "00:00";
        settings.QuietHoursEnd = "23:59";
        await database.Settings.SaveAsync(settings);
        await database.Notifications.CreateChannelAsync(new NotificationChannelDraft("webhook", "hook", "https://hooks.example.test/notify", true));
        var handler = new CapturingHandler();
        var dispatcher = new NotificationDispatcher(database.Notifications, database.Settings, new StubHttpClientFactory(handler),
            NullLogger<NotificationDispatcher>.Instance);

        await dispatcher.DispatchAsync(new NotificationMessage(NotificationEventKind.RunnerOffline, "quiet", "ignored", null), CancellationToken.None);
        await dispatcher.DispatchAsync(new NotificationMessage(NotificationEventKind.SecurityAlert, "安全告警：登入鎖定", "body", null), CancellationToken.None);

        var sent = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
        Assert.Equal(NotificationEventKind.SecurityAlert, sent.GetProperty("event").GetString());
        Assert.Contains("登入鎖定", sent.GetProperty("title").GetString());
    }

    [Fact]
    public async Task SecurityAlertsAreCoalescedPerKindAndUnknownAccountsAreNotAlerted()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await database.Security.CreateUserAsync(new UserDraft("realadmin", "Real", "walis-password-123", UserRole.Admin));
        await database.Notifications.CreateChannelAsync(new NotificationChannelDraft("webhook", "hook", "https://hooks.example.test/notify", true));
        var handler = new CapturingHandler();
        var dispatcher = new NotificationDispatcher(database.Notifications, database.Settings, new StubHttpClientFactory(handler),
            NullLogger<NotificationDispatcher>.Instance);
        var audit = new AuditService(database.Audit, database.Settings, new HttpContextAccessor(), NullLogger<AuditService>.Instance);
        var clock = new SteppingTime(DateTimeOffset.UtcNow);
        var events = new SecurityEventService(audit, dispatcher, new SecurityMetrics(), database.Security,
            NullLogger<SecurityEventService>.Instance) { Clock = clock };

        // A name that matches no account: audited, never sent to the on-call channel.
        await events.LockedOutAsync(new SignInLockout("nobody-here", "203.0.113.5", false, TimeSpan.FromMinutes(15)), CancellationToken.None);
        Assert.Empty(handler.Bodies);

        for (var attempt = 0; attempt < 3; attempt++)
            await events.LockedOutAsync(new SignInLockout("realadmin", "203.0.113.5", false, TimeSpan.FromMinutes(15)), CancellationToken.None);
        Assert.Single(handler.Bodies);

        clock.Advance(SecurityEventService.AlertCoalesceWindow);
        await events.LockedOutAsync(new SignInLockout("realadmin", "203.0.113.5", false, TimeSpan.FromMinutes(15)), CancellationToken.None);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("期間另有 2 次", JsonDocument.Parse(handler.Bodies[1]).RootElement.GetProperty("body").GetString());

        // Every lockout is still on record, including while auditing is off (security rows are forced).
        Assert.Equal(5, (await database.Audit.ListAsync(50)).Count(entry => entry.Action == AuditAction.LockedOut));
    }

    private sealed class SteppingTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class RecordingSink : ISignInLockoutSink
    {
        public List<SignInLockout> Lockouts { get; } = [];

        public Task LockedOutAsync(SignInLockout lockout, CancellationToken cancellationToken)
        {
            Lockouts.Add(lockout);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAuditRepository : IAuditRepository
    {
        public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("database down");
        }

        public Task<IReadOnlyList<AuditEntry>> ListAsync(int limit, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SecurityAuditSummary> GetSecuritySummaryAsync(
            DateTime sinceUtc,
            int limit = 100,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> PurgeAsync(int retentionDays, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
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
