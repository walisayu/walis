using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Walis.Api.Endpoints;
using Walis.Api.Services.Ai;
using Walis.Core.Models;

namespace Walis.Tests;

/// <summary>What leaves for the AI provider and who may send it: prompts are redacted with the job's secrets, and operators can be kept out.</summary>
public class AiAccessTests
{
    private const string Password = "walis-password";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PromptsAreRedactedWithTheJobSecretsAndTheirEncodings()
    {
        const string secret = "deploy-token-XYZ123";
        var job = new Job { Id = Guid.NewGuid(), Name = "部署", Branch = "main", Command = $"curl -H 'Authorization: Bearer {secret}' https://x" };
        var build = new Build { Id = Guid.NewGuid(), JobId = job.Id, Number = 7, Status = BuildStatus.Failed, Log = $"token={secret}\nb64={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret))}" };

        var analysis = AiEndpoints.RedactPrompt(AiPrompts.BuildAnalysisPrompt(job, build, build.Log), [secret]);
        var review = AiEndpoints.RedactPrompt(AiPrompts.BuildReviewPrompt(job.Command, null, job.Name, ["TOKEN"]), [secret]);

        Assert.DoesNotContain(secret, analysis);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret)), analysis);
        Assert.Contains("Bearer ***", analysis);
        Assert.DoesNotContain(secret, review);
        Assert.Contains("TOKEN", review);
    }

    [Fact]
    public async Task OperatorsLoseTheAiFeaturesWhenTheSettingIsOff()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateAnonymousClient();
        var setup = await client.PostAsJsonAsync("/api/auth/setup?bearer=true", new { username = "root", displayName = "root", password = Password });
        setup.EnsureSuccessStatusCode();
        var admin = (await setup.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;

        (await Send(client, HttpMethod.Post, "/api/accounts/", admin,
            new { username = "op", password = Password, role = UserRole.Operator })).EnsureSuccessStatusCode();
        var login = await client.PostAsJsonAsync("/api/auth/login?bearer=true", new { username = "op", password = Password });
        login.EnsureSuccessStatusCode();
        var op = (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;

        var review = new { command = "echo hi" };

        // Default: operators may use it; without a provider the answer is "set one up first", not "forbidden".
        Assert.Equal(HttpStatusCode.Conflict,
            (await Send(client, HttpMethod.Post, "/api/ai/review-script", op, review)).StatusCode);

        var settingsPayload = await (await Send(client, HttpMethod.Get, "/api/settings/", admin))
            .Content.ReadFromJsonAsync<JsonElement>(Json);
        var settings = settingsPayload.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
        Assert.True(settings.AllowOperatorAi);
        settings.AllowOperatorAi = false;
        (await Send(client, HttpMethod.Put, "/api/settings/", admin, settings)).EnsureSuccessStatusCode();

        var forbidden = await Send(client, HttpMethod.Post, "/api/ai/review-script", op, review);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Post, $"/api/ai/analyze/build/{Guid.NewGuid()}", op, new { })).StatusCode);

        // Administrators are unaffected.
        Assert.Equal(HttpStatusCode.Conflict,
            (await Send(client, HttpMethod.Post, "/api/ai/review-script", admin, review)).StatusCode);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return await client.SendAsync(request);
    }
}
