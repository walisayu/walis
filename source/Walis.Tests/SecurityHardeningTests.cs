using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Walis.Api.Endpoints;
using Walis.Api.Mcp;
using Walis.Api.Services;
using Walis.Core.Backup;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Tests;

/// <summary>
///     Regression tests added after the security review: each one matches a vulnerability that was actually exploitable at the time;
///     a separate database is used here because sign-in is turned on.
/// </summary>
public class SecurityHardeningApiTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    private const string Password = "walis-password";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static bool _ready;

    [Fact]
    public async Task OperatorCannotCreateKeysLetAloneAdminKeys()
    {
        var (client, tokens) = await PrepareAsync();

        var response = await Send(client, HttpMethod.Post, "/api/api-keys/", tokens.Operator,
            new { name = "escalate", role = "admin" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Not even the list is visible: the key list is reconnaissance material.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Get, "/api/api-keys/", tokens.Viewer)).StatusCode);
    }

    [Fact]
    public async Task SettingsResponseMasksSecretsAndNonAdminsGetNoConnectionString()
    {
        var (client, tokens) = await PrepareAsync();

        var asViewer = await (await Send(client, HttpMethod.Get, "/api/settings/", tokens.Viewer)).Content
            .ReadAsStringAsync();
        Assert.DoesNotContain("hook-secret-XYZ", asViewer);
        Assert.DoesNotContain("smtp-pass-XYZ", asViewer);

        var payload = JsonDocument.Parse(asViewer).RootElement;
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("databasePath").ValueKind);
        Assert.Equal(WalisVersion.Display, payload.GetProperty("version").GetString());
        Assert.StartsWith(WalisVersion.Number, WalisVersion.Display);
        var viewerSettings = payload.GetProperty("settings");
        Assert.False(viewerSettings.TryGetProperty("backupDirectory", out _));
        Assert.False(viewerSettings.TryGetProperty("backupTargets", out _));
        Assert.False(viewerSettings.TryGetProperty("smtpHost", out _));
        Assert.False(viewerSettings.TryGetProperty("webhookSecret", out _));

        // Even an admin sees only the mask string; sending it back unchanged means unmodified and the value must be kept.
        var asAdmin = await (await Send(client, HttpMethod.Get, "/api/settings/", tokens.Admin)).Content
            .ReadFromJsonAsync<JsonElement>(Json);
        var settings = asAdmin.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
        Assert.Equal(BackupTarget.MaskedValue, settings.WebhookSecret);
        Assert.Equal(BackupTarget.MaskedValue, settings.SmtpPassword);
        Assert.NotEqual(JsonValueKind.Null, asAdmin.GetProperty("databasePath").ValueKind);

        (await Send(client, HttpMethod.Put, "/api/settings/", tokens.Admin, settings)).EnsureSuccessStatusCode();
        var stored = await factory.Services.GetRequiredService<ISettingsRepository>().GetAsync();
        Assert.Equal("hook-secret-XYZ-with-at-least-32-characters", stored.WebhookSecret);
        Assert.Equal("smtp-pass-XYZ", stored.SmtpPassword);
    }

    [Fact]
    public void PasswordInConnectionStringIsRedacted()
    {
        var redacted = SettingsEndpoints.RedactConnectionString(
            "Host=db.internal;Database=walis;Username=walis;Password=s3cret;Pooling=true");

        Assert.DoesNotContain("s3cret", redacted);
        Assert.Contains("host=db.internal", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"password={BackupTarget.MaskedValue}", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Host=db.internal;Password=\"secret;ENDLEAK\";Database=walis")]
    [InlineData("Host=db.internal;Password={secret;ENDLEAK};Database=walis")]
    [InlineData("Host=db.internal;Password=\"secret;ENDLEAK")]
    public void ConnectionStringWithSemicolonValuesOrCorruptFormatDoesNotLeakPasswordTail(string connectionString)
    {
        var redacted = SettingsEndpoints.RedactConnectionString(connectionString);

        Assert.DoesNotContain("secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("ENDLEAK", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyAdminsCanRevealSecretValues()
    {
        var (client, tokens) = await PrepareAsync();

        var viewer = await Send(client, HttpMethod.Get, "/api/environment-variables/?reveal=true", tokens.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);

        var operatorMasked = await Send(client, HttpMethod.Get, "/api/environment-variables/", tokens.Operator);
        Assert.DoesNotContain("hunter2-XYZ", await operatorMasked.Content.ReadAsStringAsync());

        var admin = await Send(client, HttpMethod.Get, "/api/environment-variables/?reveal=true", tokens.Admin);
        Assert.Contains("hunter2-XYZ", await admin.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BuildLogsAndArtifactsOfInvisibleJobsAreInvisibleToo()
    {
        var (client, tokens) = await PrepareAsync();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();

        var restricted = await jobs.CreateJobAsync(Draft($"限管理員 {Guid.NewGuid():N}", "admin", "admin"));
        var build = await jobs.QueueBuildAsync(restricted.Id, "test", "local");

        Assert.Equal(HttpStatusCode.NotFound,
            (await Send(client, HttpMethod.Get, $"/api/builds/{build.Id}", tokens.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Send(client, HttpMethod.Get, $"/api/jobs/{restricted.Id}/builds", tokens.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Send(client, HttpMethod.Get, $"/api/builds/{build.Id}/artifacts", tokens.Operator)).StatusCode);

        var list = await (await Send(client, HttpMethod.Get, "/api/builds/?limit=200", tokens.Operator)).Content
            .ReadFromJsonAsync<JsonElement>(Json);
        Assert.DoesNotContain(list.EnumerateArray(), item => item.GetProperty("id").GetGuid() == build.Id);

        // Admins still see it as usual.
        Assert.Equal(HttpStatusCode.OK,
            (await Send(client, HttpMethod.Get, $"/api/builds/{build.Id}", tokens.Admin)).StatusCode);
    }

    [Fact]
    public async Task PagedListReturnsOnlyVisibleJobsAndStatisticsDoNotLeakHiddenOnes()
    {
        var (client, tokens) = await PrepareAsync();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var stamp = Guid.NewGuid().ToString("N");

        var open = await jobs.CreateJobAsync(Draft($"公開 {stamp}", "viewer", "operator"));
        var hidden = await jobs.CreateJobAsync(Draft($"隱藏 {stamp}", "admin", "admin"));

        var asViewer = await (await Send(client, HttpMethod.Get, $"/api/jobs/page?search={stamp}&pageSize=10",
                tokens.Viewer))
            .Content.ReadFromJsonAsync<JsonElement>(Json);
        var names = asViewer.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString())
            .ToArray();
        Assert.Contains(open.Name, names);
        Assert.DoesNotContain(hidden.Name, names);
        Assert.Equal(1, asViewer.GetProperty("total").GetInt32());

        var asAdmin =
            await (await Send(client, HttpMethod.Get, $"/api/jobs/page?search={stamp}&pageSize=10", tokens.Admin))
                .Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(2, asAdmin.GetProperty("total").GetInt32());

        // An unknown sort key falls back to name, and a pageSize over the cap is clamped rather than returning 500.
        var odd = await Send(client, HttpMethod.Get,
            "/api/jobs/page?sort=drop%20table&order=desc&page=0&pageSize=99999", tokens.Admin);
        Assert.Equal(HttpStatusCode.OK, odd.StatusCode);
        Assert.Equal(200, (await odd.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task OperatorCannotModifyJobsTheyCannotRun()
    {
        var (client, tokens) = await PrepareAsync();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();

        // Visible (viewer) but only admins may run: changing the permission would be a sneaky run and must be blocked.
        var guarded = await jobs.CreateJobAsync(Draft($"正式部署 {Guid.NewGuid():N}", "viewer", "admin"));

        var edit = await Send(client, HttpMethod.Put, $"/api/jobs/{guarded.Id}", tokens.Operator, new
        {
            name = guarded.Name, command = "echo pwned", triggerKind = "manual", timeoutMinutes = 5,
            branch = "main", viewRole = "operator", runRole = "operator"
        });
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Delete, $"/api/jobs/{guarded.Id}", tokens.Operator)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Patch, $"/api/jobs/{guarded.Id}/enabled", tokens.Operator,
                new { enabled = false })).StatusCode);

        // An invisible job returns 404 without revealing existence.
        var hidden = await jobs.CreateJobAsync(Draft($"隱藏 {Guid.NewGuid():N}", "admin", "admin"));
        Assert.Equal(HttpStatusCode.NotFound,
            (await Send(client, HttpMethod.Delete, $"/api/jobs/{hidden.Id}", tokens.Operator)).StatusCode);

        Assert.Equal("echo hi", (await jobs.GetJobAsync(guarded.Id))!.Command);
    }

    [Fact]
    public async Task DeletingRunnerRevokesItsKeysAndPausedRunnerRefusesReRegistration()
    {
        var (client, tokens) = await PrepareAsync();

        var name = $"gone-{Guid.NewGuid():N}"[..14];
        (await Send(client, HttpMethod.Post, "/api/runners/", tokens.Admin,
            new { name, totalSlots = 1, isPaused = false })).EnsureSuccessStatusCode();
        var key = await IssueKeyAsync(client, tokens.Admin, name);

        // Before deletion the key may act as the runner.
        Assert.Equal(HttpStatusCode.NoContent,
            (await Send(client, HttpMethod.Post, $"/api/runners/{name}/heartbeat", key)).StatusCode);

        var runners = await (await Send(client, HttpMethod.Get, "/api/runners/", tokens.Admin)).Content
            .ReadFromJsonAsync<JsonElement>(Json);
        var id = runners.EnumerateArray().Single(runner => runner.GetProperty("name").GetString() == name)
            .GetProperty("id").GetGuid();

        var deleted = await Send(client, HttpMethod.Delete, $"/api/runners/{id}", tokens.Admin);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(1, (await deleted.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("disabledKeyCount").GetInt32());

        // The key is disabled: it can neither re-register the name nor claim.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Send(client, HttpMethod.Post, "/api/runners/", key,
                new { name, totalSlots = 1, isPaused = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Send(client, HttpMethod.Post, $"/api/runners/{name}/claim", key)).StatusCode);

        var keys = await (await Send(client, HttpMethod.Get, "/api/api-keys/", tokens.Admin)).Content
            .ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(keys.EnumerateArray()
            .Single(item => item.GetProperty("runnerName").GetString() == name)
            .GetProperty("isDisabled").GetBoolean());

        var audit = await (await Send(client, HttpMethod.Get, "/api/audit/", tokens.Admin)).Content.ReadAsStringAsync();
        Assert.Contains($"刪除執行器 {name}，一併停用 1 把", audit);

        // A paused runner refuses re-registration under its name with an explicit reason.
        var paused = $"idle-{Guid.NewGuid():N}"[..14];
        (await Send(client, HttpMethod.Post, "/api/runners/", tokens.Admin,
            new { name = paused, totalSlots = 1, isPaused = true })).EnsureSuccessStatusCode();
        var pausedKey = await IssueKeyAsync(client, tokens.Admin, paused);
        var again = await Send(client, HttpMethod.Post, "/api/runners/", pausedKey,
            new { name = paused, totalSlots = 1, isPaused = false });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("已暫停", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConcurrentResultReportsCompleteTheBuildOnceAndTriggerDownstreamOnce()
    {
        var (client, tokens) = await PrepareAsync();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();

        var downstream = await jobs.CreateJobAsync(Draft($"下游 {Guid.NewGuid():N}", "viewer", "operator"));
        var upstream = await jobs.CreateJobAsync(Draft($"上游 {Guid.NewGuid():N}", "viewer", "operator") with
        {
            RunnerName = "reporter-x",
            DownstreamJson = $"[{{\"jobName\":\"{downstream.Name}\",\"when\":\"always\"}}]"
        });
        var build = await jobs.QueueBuildAsync(upstream.Id, "test", "reporter-x");
        Assert.True(await jobs.MarkBuildRunningAsync(build.Id, "reporter-x"));

        // Both reports see a Running build; the conditional UPDATE lets exactly one through and only that one wraps up.
        var gate = new TaskCompletionSource();
        var reports = Enumerable.Range(0, 2).Select(async index =>
        {
            await gate.Task;
            return await Send(client, HttpMethod.Post, $"/api/builds/{build.Id}/result", tokens.Admin,
                new { status = index == 0 ? "Succeeded" : "Failed", exitCode = index, log = $"回報 {index}" });
        }).ToArray();
        gate.SetResult();
        var responses = await Task.WhenAll(reports);

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));

        var stored = (await jobs.GetBuildAsync(build.Id))!;
        Assert.NotEqual(BuildStatus.Running, stored.Status);

        // Downstream triggering is a side effect of the wrap-up; a second trigger would mean the loser also ran it.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<Build> triggered;
        do
        {
            triggered = await jobs.ListBuildsAsync(new BuildQuery(null, null, downstream.Id, null, 10));
            if (triggered.Count != 0) await Task.Delay(300);
        } while (triggered.Count == 0 && DateTime.UtcNow < deadline);

        Assert.Single(triggered);
    }

    [Fact]
    public async Task CancellingARunningRemoteBuildIsSignalledToTheRunnerOnItsNextReport()
    {
        var (client, tokens) = await PrepareAsync();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();

        var name = $"stop-{Guid.NewGuid():N}"[..14];
        (await Send(client, HttpMethod.Post, "/api/runners/", tokens.Admin,
            new { name, totalSlots = 1, isPaused = false })).EnsureSuccessStatusCode();
        var key = await IssueKeyAsync(client, tokens.Admin, name);
        var job = await jobs.CreateJobAsync(Draft($"遠端取消 {Guid.NewGuid():N}", "viewer", "operator") with
        {
            RunnerName = name
        });

        var build = await jobs.QueueBuildAsync(job.Id, "test", name);
        Assert.True(await jobs.MarkBuildRunningAsync(build.Id, name));

        // Nothing requested yet: the runner's report comes back without the header.
        var quiet = await Send(client, HttpMethod.Post, $"/api/builds/{build.Id}/log", key, new { log = "跑著" });
        Assert.Equal(HttpStatusCode.NoContent, quiet.StatusCode);
        Assert.False(quiet.Headers.Contains(BuildEndpoints.CancelRequestedHeader));

        // A viewer cannot cancel; an operator can, and the build stays Running until the runner reports.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Post, $"/api/builds/{build.Id}/cancel", tokens.Viewer)).StatusCode);
        var cancelled = await Send(client, HttpMethod.Post, $"/api/builds/{build.Id}/cancel", tokens.Operator);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var flagged = await cancelled.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(BuildStatus.Running, flagged.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, flagged.GetProperty("cancelRequestedAt").ValueKind);

        var told = await Send(client, HttpMethod.Post, $"/api/builds/{build.Id}/log", key, new { log = "還在跑" });
        Assert.Equal(HttpStatusCode.NoContent, told.StatusCode);
        Assert.Equal("true", told.Headers.GetValues(BuildEndpoints.CancelRequestedHeader).Single());

        var reported = await Send(client, HttpMethod.Post, $"/api/builds/{build.Id}/result", key,
            new { status = BuildStatus.Canceled, exitCode = (int?)null, log = "已中止" });
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        Assert.Equal(BuildStatus.Canceled, (await jobs.GetBuildAsync(build.Id))!.Status);

        // Without a request, "Canceled" from a runner is just a failure.
        var other = await jobs.QueueBuildAsync(job.Id, "test", name);
        Assert.True(await jobs.MarkBuildRunningAsync(other.Id, name));
        (await Send(client, HttpMethod.Post, $"/api/builds/{other.Id}/result", key,
            new { status = BuildStatus.Canceled, exitCode = (int?)null, log = "假取消" })).EnsureSuccessStatusCode();
        Assert.Equal(BuildStatus.Failed, (await jobs.GetBuildAsync(other.Id))!.Status);
    }

    [Fact]
    public async Task RunnerKeyCannotBindToTheBuiltInRunnerName()
    {
        var (client, tokens) = await PrepareAsync();

        foreach (var reserved in new[] { Runner.LocalName, "Local", " LOCAL " })
        {
            var response = await Send(client, HttpMethod.Post, "/api/api-keys/", tokens.Admin,
                new { name = "steal-local", role = "operator", runnerName = reserved });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("保留名稱", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task ClaimAndReportAllowedOnlyForBoundRunnerKeyOrAdmin()
    {
        var (client, tokens) = await PrepareAsync();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var runners = factory.Services.GetRequiredService<IRunnerRepository>();
        var settings = factory.Services.GetRequiredService<ISettingsRepository>();

        var name = $"agent-{Guid.NewGuid():N}"[..14];
        await runners.CreateAsync(new RunnerDraft(name, null, null, null, 1, false));
        var configuration = await settings.GetAsync();
        configuration.ExternallyIsolatedRunners = name;
        await settings.SaveAsync(configuration);
        var job = await jobs.CreateJobAsync(Draft($"遠端 {Guid.NewGuid():N}", "viewer", "operator") with
        {
            RunnerName = name
        });
        var build = await jobs.QueueBuildAsync(job.Id, "test", name);

        // An ordinary operator claiming under a false name: no, this would hand over every secret.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Post, $"/api/runners/{name}/claim", tokens.Operator)).StatusCode);

        // A key bound to another runner: no.
        var other = await IssueKeyAsync(client, tokens.Admin, "other-runner");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Post, $"/api/runners/{name}/claim", other)).StatusCode);

        // A key bound to this runner: may claim, but can do nothing beyond the runner routes.
        var mine = await IssueKeyAsync(client, tokens.Admin, name);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Get, "/api/jobs/", mine)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Get, "/api/environment-variables/", mine)).StatusCode);
        var ownRunners = await Send(client, HttpMethod.Get, "/api/runners/", mine);
        Assert.Equal(HttpStatusCode.OK, ownRunners.StatusCode);
        var ownRunnerList = await ownRunners.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Single(ownRunnerList.EnumerateArray());
        Assert.Equal(name, ownRunnerList.EnumerateArray().Single().GetProperty("name").GetString());

        using var claimRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/runners/{name}/claim");
        claimRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mine);
        claimRequest.Headers.Remove("X-Walis-Reauth");
        claimRequest.Headers.Add("X-Walis-Reauth", "walis-password");
        claimRequest.Headers.Add("X-Walis-Runner-Protocol", "2");
        claimRequest.Headers.Add("X-Walis-Runner-Isolation", "external");
        var claimed = await client.SendAsync(claimRequest);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        var payload = await claimed.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(build.Id, payload.GetProperty("build").GetProperty("id").GetGuid());
        var stepId = payload.GetProperty("steps").EnumerateArray().Single().GetProperty("id").GetGuid();

        // Even a valid runner cannot exhaust connections and memory with tens of MiB of JSON per callback.
        var oversizedLog = await Send(
            client,
            HttpMethod.Post,
            $"/api/builds/{build.Id}/log",
            mine,
            new { log = new string('x', BuildEndpoints.MaximumRunnerCallbackBytes) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedLog.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await Send(client, HttpMethod.Post, $"/api/builds/{build.Id}/log", mine, new { log = "still running" }))
            .StatusCode);

        // Reports: another runner's key cannot, its own can; the step must really belong to this build.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Post, $"/api/builds/{build.Id}/log", other, new { log = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Send(client, HttpMethod.Post, $"/api/builds/{build.Id}/steps/{Guid.NewGuid()}/result", mine,
                new { status = "Succeeded", exitCode = 0, log = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await Send(client, HttpMethod.Post, $"/api/builds/{build.Id}/steps/{stepId}/result", mine,
                new { status = "Succeeded", exitCode = 0, log = "ok" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await Send(client, HttpMethod.Post, $"/api/builds/{build.Id}/result", mine,
                new { status = "Succeeded", exitCode = 0, log = "done" })).StatusCode);

        // A bound key may only register its own name.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Post, "/api/runners/", mine,
                new { name = "someone-else", totalSlots = 1, isPaused = false })).StatusCode);
    }

    [Fact]
    public async Task RepeatedSignInFailuresLockAccount()
    {
        var (client, _) = await PrepareAsync();
        var username = $"lock-{Guid.NewGuid():N}"[..12];
        var security = factory.Services.GetRequiredService<ISecurityRepository>();
        await security.CreateUserAsync(new UserDraft(username, username, Password, UserRole.Viewer));

        for (var attempt = 0; attempt < SignInThrottle.AccountLimit; attempt++)
        {
            var wrong = await client.PostAsJsonAsync("/api/auth/login?bearer=true", new { username, password = "wrong-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // Once locked, even the correct password is refused, and the message tells the user how long to wait.
        var locked = await client.PostAsJsonAsync("/api/auth/login?bearer=true", new { username, password = Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Contains("retryAfterSeconds", await locked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PasswordChangeRevokesSessionsOnOtherDevices()
    {
        var (client, _) = await PrepareAsync();
        var username = $"revoke-{Guid.NewGuid():N}"[..14];
        var security = factory.Services.GetRequiredService<ISecurityRepository>();
        var user = await security.CreateUserAsync(new UserDraft(username, username, Password, UserRole.Viewer));

        var phone = await LoginAsync(client, username, Password);
        var laptop = await LoginAsync(client, username, Password);

        (await Send(client, HttpMethod.Post, "/api/auth/password", laptop,
                new { currentPassword = Password, newPassword = "another-password" }))
            .EnsureSuccessStatusCode();

        // Every session goes, the one that changed the password included: its token may be what leaked. (A browser receives
        // a fresh cookie session with the response; a bearer client signs in again with the new password.)
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/jobs/", phone)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/jobs/", laptop)).StatusCode);
        Assert.Equal(0, await security.DeleteSessionsForUserAsync(user.Id));

        var again = await LoginAsync(client, username, "another-password");
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Get, "/api/jobs/", again)).StatusCode);
    }

    [Fact]
    public async Task LogoutAllRevokesEverySessionOfTheAccount()
    {
        var (client, _) = await PrepareAsync();
        var username = $"logoutall-{Guid.NewGuid():N}"[..16];
        var security = factory.Services.GetRequiredService<ISecurityRepository>();
        await security.CreateUserAsync(new UserDraft(username, username, Password, UserRole.Viewer));

        var phone = await LoginAsync(client, username, Password);
        var laptop = await LoginAsync(client, username, Password);

        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, "/api/auth/logout-all", laptop)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/jobs/", phone)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/jobs/", laptop)).StatusCode);
    }

    [Fact]
    public async Task OversizedPushPayloadIsRejected()
    {
        var (client, _) = await PrepareAsync();
        var body = new string('x', WebhookEndpoints.MaximumBodyBytes + 1);
        var response = await client.PostAsync("/api/webhooks/push",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task ResponsesCarryBasicSecurityHeaders()
    {
        var response = await factory.CreateAnonymousClient().GetAsync("/health");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(["status"], payload.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public async Task MetricsNotificationsAndBackupInfoAreAdminOnly()
    {
        var (client, tokens) = await PrepareAsync();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateAnonymousClient().GetAsync("/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Get, "/metrics", tokens.Operator)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await Send(client, HttpMethod.Get, "/metrics", tokens.Admin)).StatusCode);

        foreach (var path in new[]
                 {
                     "/api/notifications/channels",
                     "/api/notifications/log",
                     "/api/backup/",
                     "/api/backup/files"
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden,
                (await Send(client, HttpMethod.Get, path, tokens.Viewer)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await Send(client, HttpMethod.Get, path, tokens.Operator)).StatusCode);
        }
    }

    private static JobDraft Draft(string name, string viewRole, string runRole)
    {
        return new JobDraft(
            name, null, null, null, "main", null, null, "echo hi", 5, null,
            TriggerMode.Manual, null, false, false, ScriptModes.Source, null, null, null, viewRole, runRole);
    }

    private async Task<(HttpClient Client, Tokens Tokens)> PrepareAsync()
    {
        var client = factory.CreateClient();

        await SetupLock.WaitAsync();

        try
        {
            if (!_ready)
            {
                var setup = await client.PostAsJsonAsync("/api/auth/setup?bearer=true",
                    new { username = "root", password = Password, displayName = "root" });
                setup.EnsureSuccessStatusCode();
                var admin = (await setup.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token")
                    .GetString()!;

                var payload = await (await Send(client, HttpMethod.Get, "/api/settings/", admin)).Content
                    .ReadFromJsonAsync<JsonElement>(Json);
                var settings = payload.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
                settings.RequireSignIn = true;
                settings.WebhookSecret = "hook-secret-XYZ-with-at-least-32-characters";
                settings.SmtpUser = "mailer";
                settings.SmtpPassword = "smtp-pass-XYZ";
                (await Send(client, HttpMethod.Put, "/api/settings/", admin, settings)).EnsureSuccessStatusCode();

                (await Send(client, HttpMethod.Post, "/api/accounts/", admin,
                    new { username = "op", password = Password, role = "operator" })).EnsureSuccessStatusCode();
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

        return (client, new Tokens(
            await LoginAsync(client, "root", Password),
            await LoginAsync(client, "op", Password),
            await LoginAsync(client, "vw", Password)));
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string password)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login?bearer=true", new { username, password });
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
    }

    private static async Task<string> IssueKeyAsync(HttpClient client, string adminToken, string runnerName)
    {
        var response = await Send(client, HttpMethod.Post, "/api/api-keys/", adminToken,
            new { name = $"key-{runnerName}", role = "operator", runnerName });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string token,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove("X-Walis-Reauth");
        request.Headers.Add("X-Walis-Reauth", "walis-password");

        if (body is not null) request.Content = JsonContent.Create(body, options: Json);

        return await client.SendAsync(request);
    }

    private sealed record Tokens(string Admin, string Operator, string Viewer);
}

public class SecurityHardeningUnitTests
{
    [Fact]
    public void CsrfTokenMustMatchHeaderAndBeBoundToCurrentSession()
    {
        var firstSession = PasswordHasher.CreateToken();
        var secondSession = PasswordHasher.CreateToken();
        var csrf = SessionCookieService.CreateCsrfToken(firstSession);

        Assert.Equal(SessionCookieService.CsrfTokenLength, csrf.Length);
        Assert.True(SessionCookieService.IsCsrfTokenBoundToSession(firstSession, csrf));
        Assert.False(SessionCookieService.IsCsrfTokenBoundToSession(secondSession, csrf));

        var valid = CsrfRequest(firstSession, csrf, csrf);
        Assert.True(SessionCookieService.HasValidCsrf(valid));

        var tampered = csrf[..^1] + (csrf[^1] == '0' ? '1' : '0');
        Assert.False(SessionCookieService.HasValidCsrf(CsrfRequest(firstSession, tampered, tampered)));
        Assert.False(SessionCookieService.HasValidCsrf(CsrfRequest(firstSession, csrf, tampered)));
        Assert.False(SessionCookieService.HasValidCsrf(CsrfRequest(secondSession, csrf, csrf)));
    }

    [Fact]
    public void UnknownKeyScopeAndUnknownRoleAlwaysDenyWithLeastPrivilege()
    {
        var corrupted = new ApiKey { Scope = "future-or-corrupt", Role = "future-role" };

        Assert.False(AuthenticationMiddleware.IsApiKeyPathAllowed(corrupted, "GET", "/api/jobs"));
        Assert.False(AuthenticationMiddleware.IsApiKeyPathAllowed(corrupted, "POST", "/mcp"));
        Assert.False(AuthenticationMiddleware.IsAllowed("future-role", "GET", "/api/jobs"));
        Assert.False(AuthenticationMiddleware.IsAllowed("future-role", "POST", "/api/jobs"));

        var context = new DefaultHttpContext();
        context.Items[RequestAccess.SessionKey] = new AuthSession { Role = "future-role" };
        Assert.Equal(string.Empty, RequestAccess.Role(context));
        Assert.False(JobAccess.CanView(RequestAccess.Role(context), new Job { ViewRole = UserRole.Viewer }));
    }

    [Theory]
    [InlineData("GET", "/api/jobs")]
    [InlineData("GET", "/api/jobs/11111111-1111-1111-1111-111111111111")]
    [InlineData("GET", "/api/builds")]
    [InlineData("GET", "/api/builds/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/api/jobs/11111111-1111-1111-1111-111111111111/builds")]
    public void McpStdioBridgeAllowsOnlyListedRestMethodsAndPaths(string method, string path)
    {
        Assert.True(AuthenticationMiddleware.IsMcpBridgePath(method, path));
        Assert.False(AuthenticationMiddleware.IsMcpBridgePath("DELETE", path));
        Assert.False(AuthenticationMiddleware.IsMcpBridgePath(method, path + "/extra"));
        Assert.False(AuthenticationMiddleware.IsMcpBridgePath(method, "/api/settings"));
    }

    [Fact]
    public void McpBuildListUsesVisibleJobAllowlistExcludingHiddenAndOrphanRecords()
    {
        var visibleId = Guid.NewGuid();
        var hiddenId = Guid.NewGuid();
        var orphanId = Guid.NewGuid();
        var jobs = new[]
        {
            new Job { Id = visibleId, Name = "visible", ViewRole = UserRole.Viewer },
            new Job { Id = hiddenId, Name = "hidden", ViewRole = UserRole.Admin }
        };
        var builds = new[]
        {
            new Build { Id = Guid.NewGuid(), JobId = visibleId, JobName = "visible" },
            new Build { Id = Guid.NewGuid(), JobId = hiddenId, JobName = "hidden" },
            new Build { Id = Guid.NewGuid(), JobId = orphanId, JobName = "orphan" }
        };

        var viewer = WalisMcpTools.FilterVisibleBuilds(UserRole.Viewer, builds, jobs);
        Assert.Single(viewer);
        Assert.Equal(visibleId, viewer[0].JobId);
        Assert.Equal(3, WalisMcpTools.FilterVisibleBuilds(UserRole.Admin, builds, jobs).Count);
    }

    [Fact]
    public void ArtifactCollectionAndCacheDoNotFollowSymbolicLinks()
    {
        var root = Directory.CreateTempSubdirectory("walis-symlink").FullName;

        try
        {
            var outside = Path.Combine(root, "outside");
            var work = Path.Combine(root, "work");
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(work);
            File.WriteAllText(Path.Combine(outside, "walis.db"), "SECRET");

            // The whole artifact directory is a link: skipped.
            Directory.CreateSymbolicLink(Path.Combine(work, "out"), outside);
            var collected = ArtifactCollector.Collect(work, "out", Path.Combine(root, "artifacts-1"), out var report);
            Assert.Empty(collected);
            Assert.Contains("符號連結", report);

            // The directory is real but hides links inside: the links are not collected, normal files are.
            var real = Path.Combine(work, "dist");
            Directory.CreateDirectory(real);
            File.WriteAllText(Path.Combine(real, "app.js"), "ok");
            File.CreateSymbolicLink(Path.Combine(real, "db"), Path.Combine(outside, "walis.db"));
            Directory.CreateSymbolicLink(Path.Combine(real, "nested"), outside);

            collected = ArtifactCollector.Collect(work, "dist", Path.Combine(root, "artifacts-2"), out _);
            Assert.Equal(["app.js"], collected.Select(item => item.RelativePath).ToArray());

            // The cache is published atomically as a whole tree: any link/reparse/FIFO/device rejects the entire path,
            // so incomplete content left after skipping dangerous nodes is never mislabeled as a valid cache.
            var cache = Path.Combine(root, "cache");
            Assert.Equal(0, BuildCache.Save(cache, work, ["out"]));
            Assert.Equal(0, BuildCache.Save(cache, work, ["dist"]));
            Assert.Empty(Directory.GetFiles(cache, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RestoreIsRefusedWhenBackupChecksumMismatches()
    {
        var root = Directory.CreateTempSubdirectory("walis-integrity").FullName;

        try
        {
            var archive = Path.Combine(root, "walis-backup-x.tar.gz");
            await File.WriteAllTextAsync(archive, "original");

            Assert.Null(await BackupIntegrity.VerifyAsync(archive));

            await BackupIntegrity.WriteAsync(archive);
            Assert.True(await BackupIntegrity.VerifyAsync(archive));

            await File.WriteAllTextAsync(archive, "tampered");
            Assert.False(await BackupIntegrity.VerifyAsync(archive));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RunnerBoundKeyCanOnlyUseRunnerRoutes()
    {
        Assert.True(AuthenticationMiddleware.IsRunnerPath("builder-01", "POST", "/api/runners/builder-01/claim"));
        Assert.True(AuthenticationMiddleware.IsRunnerPath("builder-01", "POST", "/api/runners/builder-01/heartbeat"));
        Assert.True(AuthenticationMiddleware.IsRunnerPath("builder-01", "GET", "/api/runners/"));
        Assert.True(
            AuthenticationMiddleware.IsRunnerPath("builder-01", "POST", $"/api/builds/{Guid.NewGuid()}/result"));
        Assert.True(AuthenticationMiddleware.IsRunnerPath("builder-01", "POST",
            $"/api/builds/{Guid.NewGuid()}/steps/{Guid.NewGuid()}/log"));

        Assert.False(AuthenticationMiddleware.IsRunnerPath("builder-01", "POST", "/api/runners/builder-02/claim"));
        Assert.False(AuthenticationMiddleware.IsRunnerPath("builder-01", "GET", "/api/jobs/"));
        Assert.False(AuthenticationMiddleware.IsRunnerPath("builder-01", "GET", $"/api/builds/{Guid.NewGuid()}"));
        Assert.False(AuthenticationMiddleware.IsRunnerPath("builder-01", "POST", "/api/api-keys/"));
        Assert.False(AuthenticationMiddleware.IsRunnerPath("builder-01", "DELETE", "/api/runners/"));
    }

    [Fact]
    public void OperatorCannotTouchSettingsThatChangeSecretFlow()
    {
        Assert.False(AuthenticationMiddleware.IsAllowed(UserRole.Operator, "POST", "/api/api-keys/"));
        Assert.False(AuthenticationMiddleware.IsAllowed(UserRole.Operator, "GET", "/api/api-keys/"));
        Assert.False(AuthenticationMiddleware.IsAllowed(UserRole.Viewer, "GET", "/api/accounts/"));
        Assert.False(
            AuthenticationMiddleware.IsAllowed(UserRole.Operator, "PUT", $"/api/ai/providers/{Guid.NewGuid()}"));
        Assert.False(AuthenticationMiddleware.IsAllowed(UserRole.Operator, "POST", "/api/notifications/channels"));
        Assert.False(AuthenticationMiddleware.IsAllowed(UserRole.Operator, "GET", "/api/notifications/channels"));
        Assert.False(AuthenticationMiddleware.IsAllowed(UserRole.Operator, "POST", "/api/backup/test"));
        Assert.False(AuthenticationMiddleware.IsAllowed(UserRole.Operator, "GET", "/api/backup/files"));
        Assert.False(AuthenticationMiddleware.IsAllowed(UserRole.Operator, "GET", "/metrics"));
        Assert.False(AuthenticationMiddleware.IsAllowed(UserRole.Operator, "POST", "/api/runners/"));

        Assert.True(AuthenticationMiddleware.IsAllowed(UserRole.Operator, "GET", "/api/ai/providers"));
        Assert.True(AuthenticationMiddleware.IsAllowed(UserRole.Operator, "POST", "/api/ai/review-script"));
        Assert.True(AuthenticationMiddleware.IsAllowed(UserRole.Operator, "POST", "/api/jobs/"));
        Assert.True(AuthenticationMiddleware.IsAllowed(UserRole.Viewer, "GET", "/api/settings/"));
    }

    [Fact]
    public void SignInThrottleLocksAfterLimitAndUnlocksAfterSuccess()
    {
        var clock = new FakeTime(DateTimeOffset.UtcNow);
        var throttle = new SignInThrottle(clock);

        for (var attempt = 0; attempt < SignInThrottle.AccountLimit - 1; attempt++)
        {
            throttle.RecordFailure("leo", "10.0.0.1");
            Assert.Null(throttle.RetryAfter("leo", "10.0.0.1"));
        }

        throttle.RecordFailure("leo", "10.0.0.1");
        Assert.NotNull(throttle.RetryAfter("LEO", "10.0.0.2"));
        Assert.Null(throttle.RetryAfter("someone-else", "10.0.0.2"));

        clock.Advance(SignInThrottle.Lockout + TimeSpan.FromSeconds(1));
        Assert.Null(throttle.RetryAfter("leo", "10.0.0.1"));

        throttle.RecordFailure("leo", null);
        throttle.RecordSuccess("leo");
        throttle.RecordFailure("leo", null);
        Assert.Null(throttle.RetryAfter("leo", null));
    }

    private static HttpRequest CsrfRequest(string session, string cookie, string header)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie =
            $"{SessionCookieService.SessionCookieName}={session}; {SessionCookieService.CsrfCookieName}={cookie}";
        context.Request.Headers[SessionCookieService.CsrfHeaderName] = header;
        return context.Request;
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
        }
    }
}