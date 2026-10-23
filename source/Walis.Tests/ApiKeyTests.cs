using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

public class ApiKeyRepositoryTests
{
    [Fact]
    public async Task CreatedKeyAuthenticatesWithPlaintextToken()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var repository = new ApiKeyRepository(database.Factory);

        var issued = await repository.CreateAsync(new ApiKeyDraft("部署機器人", UserRole.Operator, null), "leo");

        Assert.StartsWith("walis_", issued.Token);
        Assert.Equal(UserRole.Operator, issued.Key.Role);

        var authenticated = await repository.AuthenticateAsync(issued.Token);

        Assert.NotNull(authenticated);
        Assert.Equal(issued.Key.Id, authenticated!.Id);
        Assert.NotNull(authenticated.LastUsedAt);
    }

    [Fact]
    public async Task PlaintextIsNotStored()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var repository = new ApiKeyRepository(database.Factory);

        var issued = await repository.CreateAsync(new ApiKeyDraft("金鑰", UserRole.Viewer, null), null);
        var listed = await repository.ListAsync();
        using var connection = database.Factory.Create();
        var storedHash = await connection.ExecuteScalarAsync<string>("SELECT Hash FROM ApiKeys;");

        // The list only shows the prefix; nothing anywhere can recover the plaintext.
        Assert.Single(listed);
        Assert.Equal(issued.Key.Prefix, listed[0].Prefix);
        Assert.DoesNotContain(issued.Token, JsonSerializer.Serialize(listed));
        Assert.StartsWith("sha256$", storedHash);
    }

    [Fact]
    public async Task DisabledOrExpiredKeysAreRejected()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var repository = new ApiKeyRepository(database.Factory);

        var disabled = await repository.CreateAsync(new ApiKeyDraft("停用", UserRole.Operator, null), null);
        await repository.SetDisabledAsync(disabled.Key.Id, true);
        Assert.Null(await repository.AuthenticateAsync(disabled.Token));

        var expired = await repository.CreateAsync(
            new ApiKeyDraft("過期", UserRole.Operator, DateTime.UtcNow.AddSeconds(-1)),
            null);
        Assert.Null(await repository.AuthenticateAsync(expired.Token));
    }

    [Fact]
    public async Task GuessedTokenFailsAuthentication()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var repository = new ApiKeyRepository(database.Factory);

        var issued = await repository.CreateAsync(new ApiKeyDraft("金鑰", UserRole.Operator, null), null);

        Assert.Null(await repository.AuthenticateAsync("walis_zzzzzz_不存在"));
        Assert.Null(await repository.AuthenticateAsync($"walis_{issued.Key.Prefix}_錯的祕密"));
        Assert.Null(await repository.AuthenticateAsync("不是金鑰格式"));
    }
}

/// <summary>Uses a separate database: sign-in is enabled here, which would affect the other API tests.</summary>
public class ApiKeyEndpointTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task KeyCanReadAndWriteApiWithoutOtp()
    {
        var client = factory.CreateClient();
        var session = await SignInAsync(client);

        var created = await Post(client, session, "/api/api-keys", new
        {
            name = "自動化 API",
            role = UserRole.Operator,
            scope = ApiKeyScope.Api
        });
        created.EnsureSuccessStatusCode();

        var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;

        // Once sign-in is enabled, requests without credentials are blocked.
        await EnableSignInAsync(client, session);
        var anonymous = await factory.CreateAnonymousClient().GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var withKey = new HttpRequestMessage(HttpMethod.Get, "/api/jobs");
        withKey.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        withKey.Headers.Remove("X-Walis-Reauth");
        withKey.Headers.Add("X-Walis-Reauth", "walis-password");

        var response = await client.SendAsync(withKey);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task KeyCannotCreateNewKeys()
    {
        var client = factory.CreateClient();
        var session = await SignInAsync(client);

        var created = await Post(client, session, "/api/api-keys", new { name = "管理金鑰", role = UserRole.Admin });
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;

        var response = await Post(client, token, "/api/api-keys", new { name = "再生一把", role = UserRole.Admin });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyKeyCannotTriggerBuilds()
    {
        var client = factory.CreateClient();
        var session = await SignInAsync(client);
        await EnableSignInAsync(client, session);

        var created = await Post(client, session, "/api/api-keys", new
        {
            name = "唯讀",
            role = UserRole.Viewer,
            scope = ApiKeyScope.Api
        });
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;

        var read = new HttpRequestMessage(HttpMethod.Get, "/api/jobs");
        read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        read.Headers.Remove("X-Walis-Reauth");
        read.Headers.Add("X-Walis-Reauth", "walis-password");
        (await client.SendAsync(read)).EnsureSuccessStatusCode();

        var write = await Post(client, token, "/api/jobs", new
        {
            name = "不該建立",
            command = "echo hi",
            timeoutMinutes = 30,
            triggerKind = "manual"
        });

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task DefaultMcpKeyAllowsOnlyExactRestPathsNeededByBridge()
    {
        var client = factory.CreateClient();
        var session = await SignInAsync(client);
        var created = await Post(client, session, "/api/api-keys", new
        {
            name = "AI 代理",
            role = UserRole.Operator
        });
        created.EnsureSuccessStatusCode();

        var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
        var rest = new HttpRequestMessage(HttpMethod.Get, "/api/jobs");
        rest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        rest.Headers.Remove("X-Walis-Reauth");
        rest.Headers.Add("X-Walis-Reauth", "walis-password");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(rest)).StatusCode);

        var builds = new HttpRequestMessage(HttpMethod.Get, "/api/builds?limit=20");
        builds.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        builds.Headers.Remove("X-Walis-Reauth");
        builds.Headers.Add("X-Walis-Reauth", "walis-password");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(builds)).StatusCode);

        var forbidden = new HttpRequestMessage(HttpMethod.Get, "/api/settings");
        forbidden.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        forbidden.Headers.Remove("X-Walis-Reauth");
        forbidden.Headers.Add("X-Walis-Reauth", "walis-password");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(forbidden)).StatusCode);

        var createJob = await Post(client, token, "/api/jobs", new
        {
            name = "不該建立",
            command = "echo no",
            triggerKind = "manual",
            timeoutMinutes = 30
        });
        Assert.Equal(HttpStatusCode.Forbidden, createJob.StatusCode);

        var mcp = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }, options: Json)
        };
        mcp.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        mcp.Headers.Remove("X-Walis-Reauth");
        mcp.Headers.Add("X-Walis-Reauth", "walis-password");
        mcp.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(mcp)).StatusCode);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string token, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove("X-Walis-Reauth");
        request.Headers.Add("X-Walis-Reauth", "walis-password");

        return await client.SendAsync(request);
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
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/settings/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove("X-Walis-Reauth");
        request.Headers.Add("X-Walis-Reauth", "walis-password");

        var payload = await (await client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>(Json);
        var settings = payload.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
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

/// <summary>MCP is mounted directly on the API and must authenticate like /api.</summary>
public class McpEndpointTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ListsTools()
    {
        var client = factory.CreateClient();
        var response = await CallAsync(client, null, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list"
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        foreach (var tool in new[] { "list_jobs", "trigger_build", "wait_for_build", "get_build_log" })
            Assert.Contains(tool, body);
    }

    [Fact]
    public async Task CallingToolReturnsTextResult()
    {
        var client = factory.CreateClient();
        var response = await CallAsync(client, null, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new { name = "list_jobs", arguments = new { } }
        });

        response.EnsureSuccessStatusCode();
        Assert.Contains("content", await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> CallAsync(HttpClient client, string? token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(body, options: Json)
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }
}