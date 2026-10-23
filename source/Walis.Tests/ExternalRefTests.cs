using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Walis.Api.Services;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

/// <summary>Idempotent build triggers: externalRef in the body or the Idempotency-Key header.</summary>
public sealed class ExternalRefTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TriggerHashIgnoresKeyOrderAndTreatsNullAsEmpty()
    {
        var forward = BuildExternalRefs.ComputeTriggerHash(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" });
        var reverse = BuildExternalRefs.ComputeTriggerHash(new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" });

        Assert.Equal(forward, reverse);
        Assert.Equal(64, forward.Length);
        Assert.Equal(BuildExternalRefs.ComputeTriggerHash(null), BuildExternalRefs.ComputeTriggerHash(new Dictionary<string, string>()));
        Assert.NotEqual(forward,
            BuildExternalRefs.ComputeTriggerHash(new Dictionary<string, string> { ["A"] = "1", ["B"] = "3" }));
        Assert.NotEqual(forward, BuildExternalRefs.ComputeTriggerHash(new Dictionary<string, string> { ["A"] = "12" }));
    }

    [Theory]
    [InlineData("ref-1", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("tab\there", false)]
    public void ExternalRefValidationRejectsEmptyAndControlCharacters(string value, bool expected)
    {
        Assert.Equal(expected, BuildExternalRefs.IsValid(value));
    }

    [Fact]
    public void ExternalRefValidationEnforcesMaximumLength()
    {
        Assert.True(BuildExternalRefs.IsValid(new string('a', BuildExternalRefs.MaximumLength)));
        Assert.False(BuildExternalRefs.IsValid(new string('a', BuildExternalRefs.MaximumLength + 1)));
    }

    [Fact]
    public async Task RetryWithSameExternalRefReturnsOriginalBuildWithoutAuditingAgain()
    {
        var client = factory.CreateClient();
        var job = await CreateJobAsync(client);
        var externalRef = NewRef();

        var first = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { externalRef });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.False(first.Headers.Contains("Walis-Deduplicated"));
        var original = (await first.Content.ReadFromJsonAsync<Build>(Json))!;

        var retry = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { externalRef });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues("Walis-Deduplicated")));
        var returned = (await retry.Content.ReadFromJsonAsync<Build>(Json))!;

        Assert.Equal(original.Id, returned.Id);
        Assert.Equal(original.Number, returned.Number);
        Assert.Single(await Jobs().FindBuildsByExternalRefAsync(job.Id, externalRef));

        var audit = await factory.Services.GetRequiredService<IAuditRepository>().ListAsync(5_000);
        Assert.Single(audit, entry => entry.EntityType == "build" && entry.EntityId == original.Id.ToString());
    }

    [Fact]
    public async Task SameExternalRefWithDifferentParametersIsConflict()
    {
        var client = factory.CreateClient();
        var job = await CreateJobAsync(client, parametersJson: """[{"name":"ENV","type":"string","default":"stg"}]""");
        var externalRef = NewRef();

        var first = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds",
            new { externalRef, parameters = new Dictionary<string, string> { ["ENV"] = "stg" } });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var conflict = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds",
            new { externalRef, parameters = new Dictionary<string, string> { ["ENV"] = "prod" } });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("external_ref_conflict", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));

        // Filling in the default explicitly is still different content from what the caller sent the first time.
        var omitted = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { externalRef });
        Assert.Equal(HttpStatusCode.Conflict, omitted.StatusCode);

        Assert.Single(await Jobs().FindBuildsByExternalRefAsync(job.Id, externalRef));
    }

    [Fact]
    public async Task IdempotencyKeyHeaderActsAsExternalRef()
    {
        var client = factory.CreateClient();
        var job = await CreateJobAsync(client);
        var externalRef = NewRef();

        using var first = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{job.Id}/builds")
        {
            Content = JsonContent.Create(new { })
        };
        first.Headers.Add("Idempotency-Key", externalRef);
        var created = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var original = (await created.Content.ReadFromJsonAsync<Build>(Json))!;
        Assert.Equal(externalRef, original.ExternalRef);

        // The body form of the same reference finds the same build.
        var retry = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { externalRef });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(original.Id, (await retry.Content.ReadFromJsonAsync<Build>(Json))!.Id);

        // Header and body together are fine when they agree.
        using var both = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{job.Id}/builds")
        {
            Content = JsonContent.Create(new { externalRef })
        };
        both.Headers.Add("Idempotency-Key", externalRef);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(both)).StatusCode);
    }

    [Fact]
    public async Task MismatchedHeaderAndBodyOrInvalidExternalRefIsBadRequest()
    {
        var client = factory.CreateClient();
        var job = await CreateJobAsync(client);

        using var mismatched = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{job.Id}/builds")
        {
            Content = JsonContent.Create(new { externalRef = "body-ref" })
        };
        mismatched.Headers.Add("Idempotency-Key", "header-ref");
        var response = await client.SendAsync(mismatched);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("externalRef", await response.Content.ReadAsStringAsync());

        var tooLong = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds",
            new { externalRef = new string('x', BuildExternalRefs.MaximumLength + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var control = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { externalRef = "a\u0001b" });
        Assert.Equal(HttpStatusCode.BadRequest, control.StatusCode);

        var empty = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { externalRef = "" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        Assert.Equal(0, (await Jobs().GetJobAsync(job.Id))!.TotalBuilds);
    }

    [Fact]
    public async Task SameExternalRefOnDifferentJobsIsIndependent()
    {
        var client = factory.CreateClient();
        var first = await CreateJobAsync(client);
        var second = await CreateJobAsync(client);
        var externalRef = NewRef();

        var a = await client.PostAsJsonAsync($"/api/jobs/{first.Id}/builds", new { externalRef });
        var b = await client.PostAsJsonAsync($"/api/jobs/{second.Id}/builds", new { externalRef });

        Assert.Equal(HttpStatusCode.Accepted, a.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, b.StatusCode);
        Assert.NotEqual(
            (await a.Content.ReadFromJsonAsync<Build>(Json))!.Id,
            (await b.Content.ReadFromJsonAsync<Build>(Json))!.Id);
    }

    [Fact]
    public async Task MatrixRetryReturnsEveryBuildOfTheOriginalTrigger()
    {
        var client = factory.CreateClient();
        var job = await CreateJobAsync(client, matrixJson: """{"NODE":["20","22"],"OS":["mac"]}""");
        var externalRef = NewRef();

        var first = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { externalRef });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstBuild = (await first.Content.ReadFromJsonAsync<Build>(Json))!;

        var retry = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { externalRef });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(firstBuild.Id, (await retry.Content.ReadFromJsonAsync<Build>(Json))!.Id);

        var stored = await Jobs().FindBuildsByExternalRefAsync(job.Id, externalRef);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, build => Assert.Equal(externalRef, build.ExternalRef));

        // The launcher itself hands back the whole batch, which is what MCP lists.
        var launcher = factory.Services.GetRequiredService<BuildLauncher>();
        var relaunch = await launcher.LaunchAsync(job.Id, "test", CancellationToken.None,
            new BuildLaunchOptions { ExternalRef = externalRef });
        Assert.True(relaunch.Deduplicated);
        Assert.Equal(stored.Select(build => build.Id), relaunch.Builds.Select(build => build.Id));
        Assert.Equal(2, (await Jobs().GetJobAsync(job.Id))!.TotalBuilds);
    }

    [Fact]
    public async Task ConcurrentRequestsWithSameExternalRefCreateOneBuild()
    {
        var client = factory.CreateClient();
        var job = await CreateJobAsync(client);
        var externalRef = NewRef();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { externalRef })));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        Assert.All(responses, response =>
            Assert.True(response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK));

        var ids = new HashSet<Guid>();
        foreach (var response in responses) ids.Add((await response.Content.ReadFromJsonAsync<Build>(Json))!.Id);

        Assert.Single(ids);
        Assert.Single(await Jobs().FindBuildsByExternalRefAsync(job.Id, externalRef));
    }

    [Fact]
    public async Task RetryAgainstDisabledJobStillReturnsExistingBuild()
    {
        var client = factory.CreateClient();
        var job = await CreateJobAsync(client);
        var externalRef = NewRef();

        var first = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { externalRef });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var original = (await first.Content.ReadFromJsonAsync<Build>(Json))!;

        var disable = await client.PatchAsJsonAsync($"/api/jobs/{job.Id}/enabled", new { enabled = false });
        disable.EnsureSuccessStatusCode();

        var retry = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { externalRef });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(original.Id, (await retry.Content.ReadFromJsonAsync<Build>(Json))!.Id);

        // A new reference is an ordinary trigger and is refused for the disabled job.
        var fresh = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { externalRef = NewRef() });
        Assert.Equal(HttpStatusCode.Conflict, fresh.StatusCode);
    }

    [Fact]
    public async Task BuildResponsesCarryExternalRefButNotTriggerHash()
    {
        var client = factory.CreateClient();
        var job = await CreateJobAsync(client);
        var externalRef = NewRef();

        var run = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { externalRef });
        var created = await run.Content.ReadAsStringAsync();
        var build = JsonSerializer.Deserialize<Build>(created, Json)!;
        Assert.Equal(externalRef, build.ExternalRef);

        var detail = await client.GetStringAsync($"/api/builds/{build.Id}");
        var element = JsonDocument.Parse(detail).RootElement;
        Assert.Equal(externalRef, element.GetProperty("externalRef").GetString());

        Assert.DoesNotContain("triggerHash", created, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("triggerHash", detail, StringComparison.OrdinalIgnoreCase);

        // Without a reference the field is simply null.
        var plain = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { });
        Assert.Null((await plain.Content.ReadFromJsonAsync<Build>(Json))!.ExternalRef);
    }

    private IJobRepository Jobs()
    {
        return factory.Services.GetRequiredService<IJobRepository>();
    }

    private static string NewRef()
    {
        return $"agentgate:{Guid.NewGuid():N}";
    }

    private static async Task<Job> CreateJobAsync(
        HttpClient client,
        string? parametersJson = null,
        string? matrixJson = null)
    {
        var created = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"external-ref-{Guid.NewGuid():N}",
            command = "echo external-ref",
            triggerKind = "manual",
            timeoutMinutes = 5,
            parametersJson,
            matrixJson
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<Job>(Json))!;
    }

    [Fact]
    public void PasswordValuesNeverEnterTheTriggerHash()
    {
        var secret = new HashSet<string>(StringComparer.Ordinal) { "TOKEN" };

        var first = BuildExternalRefs.ComputeTriggerHash(
            new Dictionary<string, string> { ["TOKEN"] = "hunter2", ["ENV"] = "prod" }, secret);
        var second = BuildExternalRefs.ComputeTriggerHash(
            new Dictionary<string, string> { ["TOKEN"] = "another", ["ENV"] = "prod" }, secret);

        // The unsalted hash sits next to the encrypted value; it must not let anyone test password guesses offline.
        Assert.Equal(first, second);
        Assert.NotEqual(first, BuildExternalRefs.ComputeTriggerHash(
            new Dictionary<string, string> { ["TOKEN"] = "hunter2", ["ENV"] = "dev" }, secret));
    }

    [Fact]
    public void PasswordParametersShorterThanFourCharactersAreRefused()
    {
        var definitions = BuildParameters.Parse("""[{"name":"TOKEN","type":"password"}]""");

        Assert.False(BuildParameters.TryResolve(definitions, new Dictionary<string, string> { ["TOKEN"] = "ab" }, out _, out var error));
        Assert.Contains("至少", error);
        Assert.True(BuildParameters.TryResolve(definitions, new Dictionary<string, string> { ["TOKEN"] = "abcd" }, out _, out _));
        Assert.True(BuildParameters.TryResolve(definitions, null, out _, out _));
    }
}
