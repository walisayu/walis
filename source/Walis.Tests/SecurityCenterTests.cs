using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Walis.Api.Services;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Plugins;
using Walis.Core.Repositories;

namespace Walis.Tests;

public class SecurityCenterTests
{
    private const string Password = "security-center-password";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EndpointsRequireAdminRole()
    {
        await using var factory = new WalisApiFactory();
        var (client, tokens) = await SignInRequiredAsync(factory);

        var anonymousClient = factory.CreateAnonymousClient();
        var anonResponse = await anonymousClient.GetAsync("/api/security-center");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResponse.StatusCode);

        var viewerResponse = await Send(client, HttpMethod.Get, "/api/security-center", tokens.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, viewerResponse.StatusCode);

        var operatorResponse = await Send(client, HttpMethod.Get, "/api/security-center", tokens.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, operatorResponse.StatusCode);

        var adminResponse = await Send(client, HttpMethod.Get, "/api/security-center", tokens.Admin);
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    [Fact]
    public async Task GetSecurityCenterReturnsComprehensivePostureAndInventory()
    {
        await using var factory = new WalisApiFactory();
        var (client, tokens) = await SignInRequiredAsync(factory);

        var jobRepo = factory.Services.GetRequiredService<IJobRepository>();
        var runnerRepo = factory.Services.GetRequiredService<IRunnerRepository>();

        await runnerRepo.CreateAsync(new RunnerDraft("sec-runner-1", "Linux", "1.0", "sec", 1, false));

        await jobRepo.CreateJobAsync(new JobDraft(
            "privileged-build",
            "SecOps",
            "Security test job",
            null,
            "main",
            Runner.LocalName,
            null,
            "echo hi",
            30,
            null,
            TriggerMode.Manual,
            null,
            true,
            false,
            ScriptModes.Source,
            null,
            TrustedUnsandboxed: true,
            DenyNetwork: false));

        var response = await Send(client, HttpMethod.Get, "/api/security-center", tokens.Admin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.True(payload.TryGetProperty("posture", out var posture));
        Assert.True(posture.TryGetProperty("checks", out var checks));
        Assert.True(checks.GetArrayLength() > 0);
        Assert.True(posture.TryGetProperty("status", out _));

        Assert.True(payload.TryGetProperty("inventory", out var inventory));
        Assert.True(inventory.TryGetProperty("privilegedJobs", out var privilegedJobs));
        Assert.Contains(privilegedJobs.EnumerateArray(), j => j.GetProperty("name").GetString() == "privileged-build");

        Assert.True(inventory.TryGetProperty("runners", out var runners));
        Assert.Contains(runners.EnumerateArray(), r => r.GetProperty("name").GetString() == "sec-runner-1");

        Assert.True(payload.TryGetProperty("threats", out var threats));
        Assert.True(threats.TryGetProperty("activeLockouts", out _));
        Assert.True(threats.TryGetProperty("metrics24h", out _));
    }

    [Fact]
    public async Task LockoutManagement_CanListAndUnlock()
    {
        await using var factory = new WalisApiFactory();
        var (client, tokens) = await SignInRequiredAsync(factory);

        var targetUser = "lockout-victim";
        (await Send(client, HttpMethod.Post, "/api/accounts/", tokens.Admin,
            new { username = targetUser, password = Password, role = UserRole.Viewer })).EnsureSuccessStatusCode();

        // Fail 5 times to trigger an account lockout
        for (var i = 0; i < SignInThrottle.AccountLimit; i++)
        {
            var fail = await client.PostAsJsonAsync("/api/auth/login?bearer=true", new
            {
                username = targetUser,
                password = "wrong-password"
            });
            Assert.Equal(HttpStatusCode.Unauthorized, fail.StatusCode);
        }

        // Active lockouts should now contain the target user
        var centerResp = await Send(client, HttpMethod.Get, "/api/security-center", tokens.Admin);
        centerResp.EnsureSuccessStatusCode();
        var centerData = await centerResp.Content.ReadFromJsonAsync<JsonElement>(Json);
        var lockouts = centerData.GetProperty("threats").GetProperty("activeLockouts");
        Assert.Contains(lockouts.EnumerateArray(),
            l => l.GetProperty("kind").GetString() == "account" && l.GetProperty("target").GetString() == targetUser);

        var bucketKey = $"account:{targetUser.ToLowerInvariant()}";

        // Non-admin cannot unlock
        var viewerUnlock = await Send(client, HttpMethod.Delete, $"/api/security-center/lockouts?key={bucketKey}", tokens.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, viewerUnlock.StatusCode);

        // Admin unlocks
        var adminUnlock = await Send(client, HttpMethod.Delete, $"/api/security-center/lockouts?key={bucketKey}", tokens.Admin);
        Assert.Equal(HttpStatusCode.NoContent, adminUnlock.StatusCode);

        // Unlocking again yields 404
        var secondUnlock = await Send(client, HttpMethod.Delete, $"/api/security-center/lockouts?key={bucketKey}", tokens.Admin);
        Assert.Equal(HttpStatusCode.NotFound, secondUnlock.StatusCode);

        // Lockout list is now clear of this account
        var refreshedResp = await Send(client, HttpMethod.Get, "/api/security-center", tokens.Admin);
        var refreshedData = await refreshedResp.Content.ReadFromJsonAsync<JsonElement>(Json);
        var refreshedLockouts = refreshedData.GetProperty("threats").GetProperty("activeLockouts");
        Assert.DoesNotContain(refreshedLockouts.EnumerateArray(),
            l => l.GetProperty("target").GetString() == targetUser);
    }

    [Fact]
    public async Task PluginTrustManagement_CanToggleAndAudit()
    {
        await using var factory = new WalisApiFactory();
        var (client, tokens) = await SignInRequiredAsync(factory);

        var manifest = new PluginManifest
        {
            Id = "test.security.plugin",
            Name = "Security Center Test Plugin",
            Version = "1.0.0",
            Description = "Plugin for testing trust flag",
            Author = "Leo"
        };

        var installResp = await Send(client, HttpMethod.Post, "/api/plugins/", tokens.Admin, new
        {
            manifest,
            code = "console.log('sec');",
            isTrusted = false
        });
        installResp.EnsureSuccessStatusCode();

        // Non-admin cannot toggle trust
        var viewerToggle = await Send(client, HttpMethod.Post, $"/api/plugins/{manifest.Id}/trusted", tokens.Viewer, new { isTrusted = true });
        Assert.Equal(HttpStatusCode.Forbidden, viewerToggle.StatusCode);

        // Admin grants trust
        var adminToggle = await Send(client, HttpMethod.Post, $"/api/plugins/{manifest.Id}/trusted", tokens.Admin, new { isTrusted = true });
        Assert.Equal(HttpStatusCode.OK, adminToggle.StatusCode);
        var trustedPlugin = await adminToggle.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(trustedPlugin.GetProperty("isTrusted").GetBoolean());

        // Verify it appears in security center inventory
        var centerResp = await Send(client, HttpMethod.Get, "/api/security-center", tokens.Admin);
        centerResp.EnsureSuccessStatusCode();
        var centerData = await centerResp.Content.ReadFromJsonAsync<JsonElement>(Json);
        var trustedPlugins = centerData.GetProperty("inventory").GetProperty("trustedPlugins");
        Assert.Contains(trustedPlugins.EnumerateArray(), p => p.GetProperty("pluginId").GetString() == manifest.Id);

        // Admin revokes trust
        var revokeToggle = await Send(client, HttpMethod.Post, $"/api/plugins/{manifest.Id}/trusted", tokens.Admin, new { isTrusted = false });
        Assert.Equal(HttpStatusCode.OK, revokeToggle.StatusCode);
        var revokedPlugin = await revokeToggle.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(revokedPlugin.GetProperty("isTrusted").GetBoolean());

        // Audit summary includes plugin event
        var auditResp = await Send(client, HttpMethod.Get, "/api/audit/page?action=update&entityType=plugin", tokens.Admin);
        auditResp.EnsureSuccessStatusCode();
        var auditData = await auditResp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Contains(auditData.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("entityId").GetString() == manifest.Id);
    }

    [Fact]
    public async Task PostureChecks_ReflectSettingChanges()
    {
        await using var factory = new WalisApiFactory();
        var (client, tokens) = await SignInRequiredAsync(factory);

        var initialResp = await Send(client, HttpMethod.Get, "/api/security-center", tokens.Admin);
        var initialData = await initialResp.Content.ReadFromJsonAsync<JsonElement>(Json);
        var checks = initialData.GetProperty("posture").GetProperty("checks");

        var workdirCheck = checks.EnumerateArray().First(c => c.GetProperty("id").GetString() == "build-workdir");
        Assert.Equal("warn", workdirCheck.GetProperty("status").GetString());

        // Update settings to allow working directories
        var settingsPayload = await (await Send(client, HttpMethod.Get, "/api/settings/", tokens.Admin))
            .Content.ReadFromJsonAsync<JsonElement>(Json);
        var settings = settingsPayload.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
        settings.AllowedWorkingDirectories = "/var/walis/work";
        (await Send(client, HttpMethod.Put, "/api/settings/", tokens.Admin, settings)).EnsureSuccessStatusCode();

        var updatedResp = await Send(client, HttpMethod.Get, "/api/security-center", tokens.Admin);
        var updatedData = await updatedResp.Content.ReadFromJsonAsync<JsonElement>(Json);
        var updatedChecks = updatedData.GetProperty("posture").GetProperty("checks");

        var updatedWorkdirCheck = updatedChecks.EnumerateArray().First(c => c.GetProperty("id").GetString() == "build-workdir");
        Assert.Equal("pass", updatedWorkdirCheck.GetProperty("status").GetString());
    }

    private static async Task<(HttpClient Client, Tokens Tokens)> SignInRequiredAsync(
        WalisApiFactory factory,
        bool maskSecrets = true)
    {
        var client = factory.CreateClient();
        var setup = await client.PostAsJsonAsync("/api/auth/setup?bearer=true", new
        {
            username = "root",
            password = Password,
            displayName = "root"
        });
        setup.EnsureSuccessStatusCode();
        var admin = (await setup.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;

        var settingsPayload = await (await Send(client, HttpMethod.Get, "/api/settings/", admin))
            .Content.ReadFromJsonAsync<JsonElement>(Json);
        var settings = settingsPayload.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
        settings.RequireSignIn = true;
        settings.MaskSecrets = maskSecrets;
        (await Send(client, HttpMethod.Put, "/api/settings/", admin, settings)).EnsureSuccessStatusCode();

        (await Send(client, HttpMethod.Post, "/api/accounts/", admin,
            new { username = "operator", password = Password, role = UserRole.Operator })).EnsureSuccessStatusCode();
        (await Send(client, HttpMethod.Post, "/api/accounts/", admin,
            new { username = "viewer", password = Password, role = UserRole.Viewer })).EnsureSuccessStatusCode();

        return (client, new Tokens(
            admin,
            await LoginAsync(client, "operator"),
            await LoginAsync(client, "viewer")));
    }

    private static async Task<string> LoginAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login?bearer=true", new { username, password = Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove("X-Walis-Reauth");
        request.Headers.Add("X-Walis-Reauth", Password);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);

        return await client.SendAsync(request);
    }

    private sealed record Tokens(string Admin, string Operator, string Viewer);
}
