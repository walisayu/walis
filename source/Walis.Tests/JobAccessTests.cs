using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Walis.Core.Database;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

public class JobAccessTests
{
    [Fact]
    public void HigherRoleCanViewAndRun()
    {
        var job = new Job { ViewRole = UserRole.Operator, RunRole = UserRole.Admin };

        Assert.True(JobAccess.CanView(UserRole.Admin, job));
        Assert.True(JobAccess.CanRun(UserRole.Admin, job));

        Assert.True(JobAccess.CanView(UserRole.Operator, job));
        Assert.False(JobAccess.CanRun(UserRole.Operator, job));

        Assert.False(JobAccess.CanView(UserRole.Viewer, job));
        Assert.False(JobAccess.CanRun(UserRole.Viewer, job));
    }

    [Fact]
    public void DefaultsLetEveryoneViewAndOperatorsRun()
    {
        var job = new Job();

        Assert.True(JobAccess.CanView(UserRole.Viewer, job));
        Assert.False(JobAccess.CanRun(UserRole.Viewer, job));
        Assert.True(JobAccess.CanRun(UserRole.Operator, job));
    }

    [Fact]
    public void InvalidRolesFoldToDefaults()
    {
        Assert.Equal(UserRole.Viewer, JobAccess.NormalizeViewRole("不存在的角色"));
        Assert.Equal(UserRole.Operator, JobAccess.NormalizeRunRole(null));
        Assert.Equal(UserRole.Admin, JobAccess.NormalizeRunRole(UserRole.Admin));
    }

    [Fact]
    public void RunRoleHasOperatorFloor()
    {
        // A row saved before the floor existed: viewer is read as operator, never as "anyone may run".
        var job = new Job { RunRole = UserRole.Viewer };

        Assert.Equal(UserRole.Operator, JobAccess.NormalizeRunRole(UserRole.Viewer));
        Assert.False(JobAccess.IsValidRunRole(UserRole.Viewer));
        Assert.True(JobAccess.IsValidRunRole(UserRole.Operator));
        Assert.False(JobAccess.CanRun(UserRole.Viewer, job));
        Assert.True(JobAccess.CanRun(UserRole.Operator, job));
        Assert.True(JobAccess.CanView(UserRole.Viewer, job));
    }

    [Fact]
    public void TrustedJobsCanBeRunButOnlyModifiedByAdmins()
    {
        var job = new Job { TrustedUnsandboxed = true, RunRole = UserRole.Operator };
        Assert.True(JobAccess.CanRun(UserRole.Operator, job));
        Assert.False(JobAccess.CanModify(UserRole.Operator, job));
        Assert.False(JobAccess.CanModify(UserRole.Viewer, job));
        Assert.True(JobAccess.CanModify(UserRole.Admin, job));
        Assert.True(JobAccess.CanModify(UserRole.Operator, new Job()));
    }

    [Fact]
    public void UnknownRoleIsNotTreatedAsViewer()
    {
        Assert.False(JobAccess.CanView("future-role", new Job()));
        Assert.False(JobAccess.CanRun("future-role", new Job()));
        Assert.False(JobAccess.CanView(UserRole.Admin, new Job { ViewRole = "corrupt" }));
        Assert.False(JobAccess.CanRun(UserRole.Admin, new Job { RunRole = "corrupt" }));
    }
}

/// <summary>Uses a separate database: sign-in is enabled here.</summary>
public class JobAccessApiTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AdminOnlyJobIsInvisibleToOperator()
    {
        var client = factory.CreateClient();
        var admin = await SignInAsync(client);
        var jobs = factory.Services.GetRequiredService<IJobRepository>();

        var restricted = await jobs.CreateJobAsync(new JobDraft(
            $"正式環境部署 {Guid.NewGuid():N}", null, null, null, "main", null, null,
            "echo 部署", 30, null, TriggerMode.Manual, null, true, false,
            ScriptModes.Source, null, null, null, UserRole.Admin, UserRole.Admin));

        var open = await jobs.CreateJobAsync(new JobDraft(
            $"一般任務 {Guid.NewGuid():N}", null, null, null, "main", null, null,
            "echo hi", 30, null, TriggerMode.Manual, null, true, false, ScriptModes.Source, null));

        await EnableSignInAsync(client, admin);
        var operatorToken = await CreateKeyAsync(client, admin, UserRole.Operator);

        var list = await GetAsync<JsonElement>(client, operatorToken, "/api/jobs/");
        var names = list.EnumerateArray().Select(job => job.GetProperty("id").GetString()).ToList();

        Assert.DoesNotContain(restricted.Id.ToString(), names);
        Assert.Contains(open.Id.ToString(), names);

        // Requesting the id directly does not work either, and it returns 404 without revealing existence.
        var detail = await SendAsync(client, operatorToken, HttpMethod.Get, $"/api/jobs/{restricted.Id}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task InsufficientRunRoleBlocksTrigger()
    {
        var client = factory.CreateClient();
        var admin = await SignInAsync(client);
        var jobs = factory.Services.GetRequiredService<IJobRepository>();

        var job = await jobs.CreateJobAsync(new JobDraft(
            $"要管理員才能跑 {Guid.NewGuid():N}", null, null, null, "main", null, null,
            "echo 部署", 30, null, TriggerMode.Manual, null, true, false,
            ScriptModes.Source, null, null, null, UserRole.Viewer, UserRole.Admin));

        await EnableSignInAsync(client, admin);
        var operatorToken = await CreateKeyAsync(client, admin, UserRole.Operator);

        // Visible (ViewRole is viewer) but cannot run.
        var detail = await SendAsync(client, operatorToken, HttpMethod.Get, $"/api/jobs/{job.Id}");
        detail.EnsureSuccessStatusCode();

        var trigger = await SendAsync(client, operatorToken, HttpMethod.Post, $"/api/jobs/{job.Id}/builds");
        Assert.Equal(HttpStatusCode.Forbidden, trigger.StatusCode);
        Assert.Contains("admin", await trigger.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RunRoleViewerIsRejectedOnSaveAndViewerKeyCannotTriggerThroughMcp()
    {
        var client = factory.CreateClient();
        var admin = await SignInAsync(client);
        var jobs = factory.Services.GetRequiredService<IJobRepository>();

        var payload = new
        {
            name = $"門檻太低 {Guid.NewGuid():N}",
            branch = "main",
            command = "echo hi",
            timeoutMinutes = 5,
            triggerKind = "manual",
            notifyOnFailure = false,
            isDisabled = false,
            scriptMode = "source",
            viewRole = "viewer",
            runRole = "viewer"
        };

        var created = await SendAsync(client, admin, HttpMethod.Post, "/api/jobs/", payload);
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Contains("runRole", await created.Content.ReadAsStringAsync());

        var existing = await jobs.CreateJobAsync(new JobDraft(
            $"既有任務 {Guid.NewGuid():N}", null, null, null, "main", null, null,
            "echo hi", 30, null, TriggerMode.Manual, null, true, false, ScriptModes.Source, null));
        var updated = await SendAsync(client, admin, HttpMethod.Put, $"/api/jobs/{existing.Id}",
            payload with { name = existing.Name });
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);

        // A row that still says viewer (written before the floor) is treated as operator when read.
        var legacy = await jobs.CreateJobAsync(new JobDraft(
            $"舊資料 {Guid.NewGuid():N}", null, null, null, "main", null, null,
            "echo hi", 30, null, TriggerMode.Manual, null, true, false,
            ScriptModes.Source, null, null, null, UserRole.Viewer, UserRole.Viewer));
        using (var connection = factory.Services.GetRequiredService<IDbConnectionFactory>().Create())
        {
            await connection.ExecuteAsync(
                "UPDATE Jobs SET RunRole = @Role WHERE Id = @Id",
                new { Role = UserRole.Viewer, Id = legacy.Id });
        }

        Assert.Equal(UserRole.Operator, (await jobs.GetJobAsync(legacy.Id))!.RunRole);

        await EnableSignInAsync(client, admin);
        var viewerToken = await CreateKeyAsync(client, admin, UserRole.Viewer);

        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new { name = "trigger_build", arguments = new { job = legacy.Id.ToString() } }
            }, options: Json)
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        request.Headers.Remove("X-Walis-Reauth");
        request.Headers.Add("X-Walis-Reauth", "walis-password");

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        // The SSE body escapes non-ASCII; the role name in "需要 operator 以上的角色才能執行" is enough to pin the refusal.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("operator", body);
        Assert.Empty(await jobs.ListBuildsAsync(new BuildQuery(null, null, legacy.Id, null, 10)));
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string token,
        HttpMethod method,
        string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove("X-Walis-Reauth");
        request.Headers.Add("X-Walis-Reauth", "walis-password");

        if (body is not null) request.Content = JsonContent.Create(body, options: Json);

        return await client.SendAsync(request);
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string token, string path)
    {
        var response = await SendAsync(client, token, HttpMethod.Get, path);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static async Task<string> CreateKeyAsync(HttpClient client, string adminToken, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/api-keys")
        {
            Content = JsonContent.Create(new
            {
                name = $"{role}-{Guid.NewGuid():N}"[..20],
                role,
                scope = ApiKeyScope.Api
            }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        request.Headers.Remove("X-Walis-Reauth");
        request.Headers.Add("X-Walis-Reauth", "walis-password");

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
    }

    private static async Task<string> SignInAsync(HttpClient client)
    {
        var setup = await client.PostAsJsonAsync("/api/auth/setup?bearer=true", new
        {
            username = "leo",
            displayName = "Leo",
            password = "walis-password"
        });

        if (setup.StatusCode == HttpStatusCode.OK)
            return (await setup.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;

        var login = await client.PostAsJsonAsync("/api/auth/login?bearer=true",
            new { username = "leo", password = "walis-password" });
        login.EnsureSuccessStatusCode();

        return (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
    }

    private static async Task EnableSignInAsync(HttpClient client, string token)
    {
        var payload = await GetAsync<JsonElement>(client, token, "/api/settings/");
        var settings = payload.GetProperty("settings").Deserialize<WalisSettings>(Json)!;

        if (settings.RequireSignIn) return;

        settings.RequireSignIn = true;

        var update = new HttpRequestMessage(HttpMethod.Put, "/api/settings/")
        {
            Content = JsonContent.Create(settings, options: Json)
        };
        update.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        update.Headers.Remove("X-Walis-Reauth");
        update.Headers.Add("X-Walis-Reauth", "walis-password");

        (await client.SendAsync(update)).EnsureSuccessStatusCode();
    }
}
