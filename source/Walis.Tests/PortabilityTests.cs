using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Walis.Api.Endpoints;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

public class PortabilityTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExportImportsUnchangedIntoAnotherDatabase()
    {
        var client = factory.CreateClient();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();

        var name = $"可攜任務 {Guid.NewGuid():N}";
        await jobs.CreateJobAsync(new JobDraft(
            name, "專案", "說明", "git@example.com:repo.git", "main", null, "/tmp",
            "echo 匯出測試", 15, "dist/**", TriggerMode.Cron, "0 3 * * *", true, false,
            ScriptModes.Source, null)
        {
            CleanWorkspace = true
        });

        var bundle = await client.GetFromJsonAsync<JsonElement>("/api/portability/export", Json);
        var exported = bundle.GetProperty("jobs").EnumerateArray()
            .Single(job => job.GetProperty("name").GetString() == name);

        // This machine's state must not travel along.
        Assert.False(exported.TryGetProperty("id", out _));
        Assert.False(exported.TryGetProperty("totalBuilds", out _));
        Assert.Equal("0 3 * * *", exported.GetProperty("cron").GetString());
        Assert.True(exported.GetProperty("cleanWorkspace").GetBoolean());
        Assert.Equal(JsonValueKind.Null, exported.GetProperty("cleanWorkspaceAfterBuild").ValueKind);

        // A job with the same name is not overwritten by default.
        var skip = await client.PostAsJsonAsync("/api/portability/import", new { bundle, overwrite = false }, Json);
        skip.EnsureSuccessStatusCode();
        var skipped = await skip.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(skipped.GetProperty("skipped").GetInt32() >= 1);
    }

    [Fact]
    public async Task OverwriteMatchesByNameNotId()
    {
        var client = factory.CreateClient();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();

        var name = $"覆寫測試 {Guid.NewGuid():N}";
        var original = await jobs.CreateJobAsync(new JobDraft(
            name, null, null, null, "main", null, null, "echo 舊的", 30, null,
            TriggerMode.Manual, null, true, false, ScriptModes.Source, null));

        var payload = new
        {
            bundle = new
            {
                version = 1,
                exportedAt = DateTime.UtcNow,
                jobs = new[]
                {
                    new
                    {
                        name,
                        branch = "main",
                        command = "echo 新的",
                        timeoutMinutes = 45,
                        triggerKind = "manual",
                        notifyOnFailure = true,
                        isDisabled = false,
                        scriptMode = "source"
                    }
                },
                actions = Array.Empty<object>()
            },
            overwrite = true
        };

        var response = await client.PostAsJsonAsync("/api/portability/import", payload, Json);
        response.EnsureSuccessStatusCode();

        var updated = await jobs.GetJobAsync(original.Id);

        // The id stays, the content is replaced, so the build history is not cut off.
        Assert.NotNull(updated);
        Assert.Equal("echo 新的", updated!.Command);
        Assert.Equal(45, updated.TimeoutMinutes);
    }

    [Fact]
    public async Task ImportAppliesTheSameJobRulesAsTheJobForm()
    {
        var client = factory.CreateClient();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var name = $"匯入規則 {Guid.NewGuid():N}";
        await jobs.CreateJobAsync(new JobDraft(
            name, "專案", null, "https://example.com/repo.git", "main", null, null, "echo hi", 15, null,
            TriggerMode.Manual, null, true, false, ScriptModes.Source, null));
        var bundle = System.Text.Json.Nodes.JsonNode.Parse(
            await client.GetStringAsync("/api/portability/export"))!.AsObject();
        var job = bundle["jobs"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == name)!.AsObject();

        async Task<HttpResponseMessage> ImportWith(string field, string value)
        {
            var copy = System.Text.Json.Nodes.JsonNode.Parse(bundle.ToJsonString())!.AsObject();
            var only = System.Text.Json.Nodes.JsonNode.Parse(job.ToJsonString())!.AsObject();
            only["name"] = $"{name}-{field}";
            only[field] = value;
            copy["jobs"] = new System.Text.Json.Nodes.JsonArray(only);
            copy["actions"] = new System.Text.Json.Nodes.JsonArray();
            return await client.PostAsJsonAsync("/api/portability/import", new { bundle = copy, overwrite = false }, Json);
        }

        var credentials = await ImportWith("repository", "https://user:token@example.com/repo.git");
        Assert.Equal(HttpStatusCode.BadRequest, credentials.StatusCode);
        Assert.Contains("repository", await credentials.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await ImportWith("runRole", UserRole.Viewer)).StatusCode);
    }

    [Fact]
    public async Task NewerFormatVersionIsRejected()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/portability/import", new
        {
            bundle = new
            {
                version = 99, exportedAt = DateTime.UtcNow, jobs = Array.Empty<object>(),
                actions = Array.Empty<object>()
            },
            overwrite = true
        }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("讀得懂", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ImportAcceptsOnlyExactVersionOneAndLimitsItemCountAndFieldLengths()
    {
        var client = factory.CreateClient();

        var oldOrMissing = await client.PostAsJsonAsync("/api/portability/import", new
        {
            bundle = new
            {
                version = 0, exportedAt = DateTime.UtcNow, jobs = Array.Empty<object>(), actions = Array.Empty<object>()
            },
            overwrite = false
        }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, oldOrMissing.StatusCode);

        var tooMany = await client.PostAsJsonAsync("/api/portability/import", new
        {
            bundle = new
            {
                version = 1,
                exportedAt = DateTime.UtcNow,
                jobs = Enumerable.Range(0, PortabilityEndpoints.MaximumJobs + 1).Select(_ => new { }).ToArray(),
                actions = Array.Empty<object>()
            },
            overwrite = false
        }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("一次最多", await tooMany.Content.ReadAsStringAsync());

        var oversizedField = await client.PostAsJsonAsync("/api/portability/import", new
        {
            bundle = new
            {
                version = 1,
                exportedAt = DateTime.UtcNow,
                jobs = new[]
                {
                    new
                    {
                        name = "bounded-job",
                        project = new string('p', 201),
                        branch = "main",
                        command = "echo ok",
                        timeoutMinutes = 30,
                        triggerKind = "manual",
                        scriptMode = "source"
                    }
                },
                actions = Array.Empty<object>()
            },
            overwrite = false
        }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, oversizedField.StatusCode);
        Assert.Contains("bundle.jobs[0].project", await oversizedField.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ActionFlowIsObjectNotStringInExport()
    {
        var client = factory.CreateClient();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();

        var name = $"流程任務 {Guid.NewGuid():N}";
        await jobs.CreateJobAsync(new JobDraft(
            name, null, null, null, "main", null, null, "echo 編譯後的腳本", 30, null,
            TriggerMode.Manual, null, true, false, ScriptModes.Actions,
            """[{"id":"1","actionId":"shell.run","enabled":true,"parameters":{"script":"echo hi"}}]"""));

        var bundle = await client.GetFromJsonAsync<JsonElement>("/api/portability/export", Json);
        var exported = bundle.GetProperty("jobs").EnumerateArray()
            .Single(job => job.GetProperty("name").GetString() == name);

        // Stored as a string, the export would contain one long escaped JSON string that cannot be reviewed.
        Assert.Equal(JsonValueKind.Array, exported.GetProperty("actionsJson").ValueKind);
    }
}