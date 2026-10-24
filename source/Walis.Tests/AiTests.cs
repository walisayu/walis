using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Walis.Api.Services.Ai;
using Walis.Core.Models;

namespace Walis.Tests;

public class AiPromptTests
{
    [Fact]
    public void ShortLogIsNotTruncated()
    {
        var log = new string('a', 100);

        Assert.Equal(log, AiPrompts.TrimLog(log));
    }

    [Fact]
    public void LongLogKeepsHeadAndTailAndMarksOmission()
    {
        var log = new string('a', 5_000) + new string('b', 30_000) + new string('c', 5_000);

        var trimmed = AiPrompts.TrimLog(log);

        Assert.Contains("中間省略", trimmed);
        Assert.StartsWith("aaa", trimmed);
        Assert.EndsWith("ccc", trimmed);
        Assert.True(trimmed.Length < log.Length);
    }

    [Fact]
    public void AnalysisPromptContainsInformationNeededToDiagnoseFailure()
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Name = "管理端建置",
            Project = "Walis Admin",
            Branch = "main",
            Command = "npm run build",
            WorkingDirectory = "/workspace"
        };

        var build = new Build
        {
            Number = 42,
            Status = BuildStatus.Failed,
            ExitCode = 1,
            TriggeredBy = "push",
            RunnerName = "local"
        };

        var prompt = AiPrompts.BuildAnalysisPrompt(job, build, "npm ERR! missing script: build");

        Assert.Contains("管理端建置", prompt);
        Assert.Contains("#42", prompt);
        Assert.Contains("npm run build", prompt);
        Assert.Contains("npm ERR!", prompt);
        Assert.Contains("結束碼：1", prompt);
    }

    [Fact]
    public void ReviewPromptIncludesVariableNamesButNotValues()
    {
        var prompt = AiPrompts.BuildReviewPrompt("deploy.sh", "/workspace", "部署", ["DEPLOY_TOKEN", "NODE_ENV"]);

        Assert.Contains("DEPLOY_TOKEN", prompt);
        Assert.Contains("NODE_ENV", prompt);
        Assert.Contains("deploy.sh", prompt);
        Assert.Contains("/workspace", prompt);
    }
}

public class AiRepositoryTests
{
    [Fact]
    public async Task InitializationCreatesThreeProvidersDisabledByDefault()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var providers = await database.Ai.ListProvidersAsync();

        Assert.Equal(3, providers.Count);
        Assert.All(providers, provider =>
        {
            Assert.False(provider.IsEnabled);
            Assert.False(provider.HasApiKey);
            Assert.False(provider.IsUsable);
        });
        Assert.Contains(providers, provider => provider.Kind == AiProviderKind.Claude);
        Assert.Contains(providers, provider => provider.Kind == AiProviderKind.Gemini);
        Assert.Contains(providers, provider => provider.Kind == AiProviderKind.Codex);
    }

    [Fact]
    public async Task ClaudeDefaultModelIsClaudeOpus5()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var claude =
            (await database.Ai.ListProvidersAsync()).Single(provider => provider.Kind == AiProviderKind.Claude);

        Assert.Equal("claude-opus-5", claude.Model);
    }

    [Fact]
    public async Task NullApiKeyKeepsExistingValue()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var provider = (await database.Ai.ListProvidersAsync()).First();

        await database.Ai.UpdateProviderAsync(provider.Id,
            new AiProviderDraft("secret-key", provider.Model, null, true, false));
        var updated =
            await database.Ai.UpdateProviderAsync(provider.Id, new AiProviderDraft(null, "另一個模型", null, true, false));

        Assert.Equal("secret-key", updated!.ApiKey);
        Assert.Equal("另一個模型", updated.Model);
    }

    [Fact]
    public async Task EmptyApiKeyClearsValue()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var provider = (await database.Ai.ListProvidersAsync()).First();

        await database.Ai.UpdateProviderAsync(provider.Id,
            new AiProviderDraft("secret-key", provider.Model, null, true, false));
        var cleared =
            await database.Ai.UpdateProviderAsync(provider.Id,
                new AiProviderDraft("", provider.Model, null, true, false));

        Assert.False(cleared!.HasApiKey);
    }

    [Fact]
    public async Task OnlyOneDefaultProvider()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var providers = await database.Ai.ListProvidersAsync();

        await database.Ai.UpdateProviderAsync(providers[0].Id, new AiProviderDraft("key", "model", null, true, true));
        await database.Ai.UpdateProviderAsync(providers[1].Id, new AiProviderDraft("key", "model", null, true, true));

        var updated = await database.Ai.ListProvidersAsync();

        Assert.Single(updated, provider => provider.IsDefault);
        Assert.Equal(providers[1].Id, updated.Single(provider => provider.IsDefault).Id);
    }

    [Fact]
    public async Task ReturnsNullWhenNoProviderAvailable()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        Assert.Null(await database.Ai.ResolveProviderAsync(null));
    }

    [Fact]
    public async Task DefaultProviderPreferredWhenUnspecified()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var providers = await database.Ai.ListProvidersAsync();

        await database.Ai.UpdateProviderAsync(providers[0].Id, new AiProviderDraft("key", "model", null, true, false));
        await database.Ai.UpdateProviderAsync(providers[1].Id, new AiProviderDraft("key", "model", null, true, true));

        Assert.Equal(providers[1].Id, (await database.Ai.ResolveProviderAsync(null))!.Id);
    }

    [Fact]
    public async Task RequestedButUnavailableProviderDoesNotFallBack()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var providers = await database.Ai.ListProvidersAsync();

        await database.Ai.UpdateProviderAsync(providers[0].Id, new AiProviderDraft("key", "model", null, true, true));

        Assert.Null(await database.Ai.ResolveProviderAsync(providers[1].Id));
    }

    [Fact]
    public async Task RequestLogIsOrderedByTimeAndCanBeCleared()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.Ai.AppendRequestAsync(new AiRequestLogEntry
        {
            StartedAt = DateTime.UtcNow.AddDays(-40),
            ProviderKind = AiProviderKind.Claude,
            Model = "claude-opus-5",
            Purpose = AiPurpose.AnalyzeBuild,
            Actor = "leo",
            PromptChars = 100,
            DurationMs = 900,
            Succeeded = true
        });

        await database.Ai.AppendRequestAsync(new AiRequestLogEntry
        {
            ProviderKind = AiProviderKind.Gemini,
            Model = "gemini-2.5-pro",
            Purpose = AiPurpose.ReviewScript,
            Actor = "leo",
            PromptChars = 50,
            DurationMs = 400,
            Succeeded = false,
            Error = "HTTP 401"
        });

        var entries = await database.Ai.ListRequestsAsync(10);

        Assert.Equal(2, entries.Count);
        Assert.Equal(AiProviderKind.Gemini, entries[0].ProviderKind);
        Assert.Equal("HTTP 401", entries[0].Error);

        Assert.Equal(1, await database.Ai.PurgeRequestsAsync(30));
        Assert.Single(await database.Ai.ListRequestsAsync(10));
    }
}

public class AiApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ProviderListDoesNotReturnApiKeys()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();

        var providers = await client.GetFromJsonAsync<JsonElement>("/api/ai/providers");

        Assert.Equal(3, providers.GetArrayLength());
        Assert.All(providers.EnumerateArray().ToList(), provider =>
        {
            Assert.False(provider.TryGetProperty("apiKey", out _));
            Assert.False(provider.GetProperty("hasApiKey").GetBoolean());
        });
    }

    [Fact]
    public async Task EnablingWithoutModelIsRejected()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();

        var providers = await client.GetFromJsonAsync<JsonElement>("/api/ai/providers");
        var id = providers[0].GetProperty("id").GetString();

        var response = await client.PutAsJsonAsync($"/api/ai/providers/{id}", new
        {
            apiKey = "key",
            model = "",
            isEnabled = true,
            isDefault = false
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConnectionTestIsBlockedWithoutApiKey()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();

        var providers = await client.GetFromJsonAsync<JsonElement>("/api/ai/providers");
        var id = providers[0].GetProperty("id").GetString();

        var response = await client.PostAsync($"/api/ai/providers/{id}/test", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AnalysisReportsSetupRequiredWhenNoProviderAvailable()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"AI 測試 {Guid.NewGuid():N}",
            command = "echo hi",
            triggerKind = "manual",
            timeoutMinutes = 5
        });
        var job = await created.Content.ReadFromJsonAsync<Job>(Json);

        var run = await client.PostAsJsonAsync($"/api/jobs/{job!.Id}/builds", new { });
        var build = await run.Content.ReadFromJsonAsync<Build>(Json);

        var response = await client.PostAsJsonAsync($"/api/ai/analyze/build/{build!.Id}", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("AI",
            (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task ScriptReviewReportsValidationErrorWithoutCommand()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ai/review-script", new { command = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RequestLogEndpointCanBeQueried()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();

        var entries = await client.GetFromJsonAsync<JsonElement>("/api/ai/requests");

        Assert.Equal(JsonValueKind.Array, entries.ValueKind);
    }
}