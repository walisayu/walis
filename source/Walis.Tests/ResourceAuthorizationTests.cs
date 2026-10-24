using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Walis.Api.Services;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

public class ResourceAuthorizationTests
{
    private const string Password = "resource-auth-password";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ActionAndPortabilityEndpointsAreAdminOnlyAndImportValidatesActionManifest()
    {
        await using var factory = new WalisApiFactory();
        var (client, tokens) = await SignInRequiredAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Get, "/api/actions/", tokens.Operator)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Post, "/api/actions/preview", tokens.Operator,
                new { actions = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Get, "/api/plugins/", tokens.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Get, "/api/plugins/sensitive.plugin", tokens.Operator)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Get, "/api/portability/export", tokens.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Post, "/api/portability/import", tokens.Operator, new { })).StatusCode);

        var invalid = await Send(client, HttpMethod.Post, "/api/portability/import", tokens.Admin, new
        {
            bundle = new
            {
                version = 1,
                exportedAt = DateTime.UtcNow,
                jobs = Array.Empty<object>(),
                actions = new[] { new { id = "INVALID", name = "bad", template = "echo bad" } }
            },
            overwrite = false
        });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("bundle.actions[0]", await invalid.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK,
            (await Send(client, HttpMethod.Get, "/api/portability/export", tokens.Admin)).StatusCode);
    }

    [Fact]
    public async Task DashboardReturnsOnlyBuildsAndStatisticsOfVisibleJobs()
    {
        await using var factory = new WalisApiFactory();
        var (client, tokens) = await SignInRequiredAsync(factory);
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var runners = factory.Services.GetRequiredService<IRunnerRepository>();
        var runnerName = $"hidden-runner-{Guid.NewGuid():N}"[..24];
        await runners.CreateAsync(new RunnerDraft(runnerName, "SecretOS", "9.9-secret", "prod-secret", 1, false));
        var visible =
            await jobs.CreateJobAsync(Draft($"visible-{Guid.NewGuid():N}", UserRole.Viewer, UserRole.Operator));
        var hidden = await jobs.CreateJobAsync(
            Draft($"hidden-{Guid.NewGuid():N}", UserRole.Admin, UserRole.Admin) with { RunnerName = runnerName });
        var visibleBuild = await jobs.QueueBuildAsync(visible.Id, "test", "runner-never");
        var hiddenBuild = await jobs.QueueBuildAsync(hidden.Id, "test", runnerName);
        Assert.True(await jobs.MarkBuildRunningAsync(hiddenBuild.Id, runnerName));

        var response = await Send(client, HttpMethod.Get, "/api/dashboard", tokens.Viewer);
        response.EnsureSuccessStatusCode();
        var dashboard = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var returnedJobs = dashboard.GetProperty("jobs").EnumerateArray().ToList();
        var recent = dashboard.GetProperty("recentBuilds").EnumerateArray().ToList();

        Assert.Contains(returnedJobs, item => item.GetProperty("id").GetGuid() == visible.Id);
        Assert.DoesNotContain(returnedJobs, item => item.GetProperty("id").GetGuid() == hidden.Id);
        Assert.Contains(recent, item => item.GetProperty("id").GetGuid() == visibleBuild.Id);
        Assert.DoesNotContain(recent, item => item.GetProperty("id").GetGuid() == hiddenBuild.Id);
        Assert.Equal(returnedJobs.Count, dashboard.GetProperty("totalJobs").GetInt64());
        Assert.Equal(1, dashboard.GetProperty("queuedBuilds").GetInt64());
        Assert.Equal(1, dashboard.GetProperty("buildsLast7Days").GetInt64());

        var dashboardRunner = dashboard.GetProperty("runners").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == runnerName);
        Assert.Equal(string.Empty, dashboardRunner.GetProperty("operatingSystem").GetString());
        Assert.Equal(JsonValueKind.Null, dashboardRunner.GetProperty("currentJobName").ValueKind);

        var runnerList = await (await Send(client, HttpMethod.Get, "/api/runners/", tokens.Viewer))
            .Content.ReadFromJsonAsync<JsonElement>(Json);
        var listRunner = runnerList.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == runnerName);
        Assert.Equal(string.Empty, listRunner.GetProperty("labels").GetString());
        Assert.Equal(JsonValueKind.Null, listRunner.GetProperty("currentJobName").ValueKind);
        Assert.DoesNotContain(hidden.Name, dashboard.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnvironmentVariablesFollowJobAclAndDisplayPreferenceDoesNotLeakToNonAdmins()
    {
        await using var factory = new WalisApiFactory();
        var (client, tokens) = await SignInRequiredAsync(factory, false);
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var variables = factory.Services.GetRequiredService<IEnvironmentVariableRepository>();
        var visible =
            await jobs.CreateJobAsync(Draft($"visible-env-{Guid.NewGuid():N}", UserRole.Viewer, UserRole.Operator));
        var guarded =
            await jobs.CreateJobAsync(Draft($"guarded-env-{Guid.NewGuid():N}", UserRole.Viewer, UserRole.Admin));
        var hidden = await jobs.CreateJobAsync(Draft($"hidden-env-{Guid.NewGuid():N}", UserRole.Admin, UserRole.Admin));
        var visibleSecret =
            await variables.CreateAsync(new EnvironmentVariableDraft("VISIBLE_SECRET", "visible-value", true,
                visible.Id));
        var guardedSecret =
            await variables.CreateAsync(new EnvironmentVariableDraft("GUARDED_SECRET", "guarded-value", true,
                guarded.Id));
        var hiddenSecret =
            await variables.CreateAsync(new EnvironmentVariableDraft("HIDDEN_SECRET", "hidden-value", true, hidden.Id));
        await variables.CreateAsync(new EnvironmentVariableDraft("GLOBAL_SECRET", "global-value", true, null));

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Get, "/api/environment-variables/", tokens.Viewer)).StatusCode);

        var list = await (await Send(client, HttpMethod.Get, "/api/environment-variables/", tokens.Operator))
            .Content.ReadFromJsonAsync<JsonElement>(Json);
        var body = list.ToString();
        Assert.Contains("VISIBLE_SECRET", body);
        Assert.Contains("GUARDED_SECRET", body);
        Assert.DoesNotContain("HIDDEN_SECRET", body);
        Assert.DoesNotContain("visible-value", body);
        Assert.DoesNotContain("guarded-value", body);
        Assert.DoesNotContain("global-value", body);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Put,
            $"/api/environment-variables/{hiddenSecret.Id}", tokens.Operator,
            new { name = "HIDDEN_SECRET", value = "poisoned", isSecret = true, jobId = hidden.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Put,
            $"/api/environment-variables/{guardedSecret.Id}", tokens.Operator,
            new { name = "GUARDED_SECRET", value = "poisoned", isSecret = true, jobId = guarded.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Post,
            "/api/environment-variables/", tokens.Operator,
            new { name = "GLOBAL_POISON", value = "poisoned", isSecret = true, jobId = (Guid?)null })).StatusCode);

        var allowed = await Send(client, HttpMethod.Put,
            $"/api/environment-variables/{visibleSecret.Id}", tokens.Operator,
            new { name = "VISIBLE_SECRET", value = "replacement", isSecret = true, jobId = visible.Id });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.DoesNotContain("replacement", await allowed.Content.ReadAsStringAsync());

        var revealed = await Send(client, HttpMethod.Get, "/api/environment-variables/?reveal=true", tokens.Admin);
        Assert.Contains("hidden-value", await revealed.Content.ReadAsStringAsync());

        var adminMasked = await Send(client, HttpMethod.Get, "/api/environment-variables/", tokens.Admin);
        Assert.DoesNotContain("hidden-value", await adminMasked.Content.ReadAsStringAsync());
        Assert.DoesNotContain("global-value", await adminMasked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AiAnalysisAndJobScopedScriptReviewDoNotBypassJobVisibility()
    {
        await using var factory = new WalisApiFactory();
        var (client, tokens) = await SignInRequiredAsync(factory);
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var ai = factory.Services.GetRequiredService<IAiRepository>();
        var visible =
            await jobs.CreateJobAsync(Draft($"visible-ai-{Guid.NewGuid():N}", UserRole.Viewer, UserRole.Operator));
        var hidden = await jobs.CreateJobAsync(Draft($"hidden-ai-{Guid.NewGuid():N}", UserRole.Admin, UserRole.Admin));
        var build = await jobs.QueueBuildAsync(hidden.Id, "test", "runner-never");

        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Post,
            $"/api/ai/analyze/build/{build.Id}", tokens.Operator, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Post,
            "/api/ai/review-script", tokens.Operator,
            new { command = "echo hi", jobId = hidden.Id })).StatusCode);

        var visibleRequest = new AiRequestLogEntry { Id = Guid.NewGuid(), JobId = visible.Id, Actor = "visible" };
        var hiddenRequest = new AiRequestLogEntry { Id = Guid.NewGuid(), JobId = hidden.Id, Actor = "hidden" };
        var globalRequest = new AiRequestLogEntry { Id = Guid.NewGuid(), Actor = "system" };
        await ai.AppendRequestAsync(visibleRequest);
        await ai.AppendRequestAsync(hiddenRequest);
        await ai.AppendRequestAsync(globalRequest);

        // Viewers do not see the AI request log at all; operators see requests about visible jobs, without the actor.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Get, "/api/ai/requests", tokens.Viewer)).StatusCode);

        var requests = await (await Send(client, HttpMethod.Get, "/api/ai/requests", tokens.Operator))
            .Content.ReadFromJsonAsync<JsonElement>(Json);
        var ids = requests.EnumerateArray().Select(entry => entry.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(visibleRequest.Id, ids);
        Assert.DoesNotContain(hiddenRequest.Id, ids);
        Assert.DoesNotContain(globalRequest.Id, ids);
        Assert.All(requests.EnumerateArray(), entry => Assert.False(entry.TryGetProperty("actor", out _)));
    }

    [Fact]
    public async Task JobPlacementRulesProtectReservedRunnersSharedDirectoriesAndNames()
    {
        await using var factory = new WalisApiFactory();
        var (client, tokens) = await SignInRequiredAsync(factory);
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var runners = factory.Services.GetRequiredService<IRunnerRepository>();
        await runners.CreateAsync(new RunnerDraft("deploy-host", null, null, null, 1, false, UserRole.Admin));
        var directory = Directory.CreateTempSubdirectory("walis-shared-");

        try
        {
            var adminJob = await jobs.CreateJobAsync(Draft($"admin-dir-{Guid.NewGuid():N}", UserRole.Viewer, UserRole.Admin)
                with { WorkingDirectory = directory.FullName });

            object Job(string name, string? runnerName = null, string? workingDirectory = null) => new
            {
                name, project = "Walis", command = "echo hi", branch = "main", triggerKind = "manual", timeoutMinutes = 5,
                notifyOnFailure = true, runnerName, workingDirectory
            };

            // A runner reserved for administrator jobs: an operator cannot send a job there.
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Post, "/api/jobs", tokens.Operator,
                Job($"to-reserved-{Guid.NewGuid():N}", runnerName: "deploy-host"))).StatusCode);

            // The administrator job's directory (or anything under it) is off limits to an operator's job.
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Post, "/api/jobs", tokens.Operator,
                Job($"same-dir-{Guid.NewGuid():N}", workingDirectory: Path.Combine(directory.FullName, "sub")))).StatusCode);

            // Names are unique, whatever the case.
            Assert.Equal(HttpStatusCode.Conflict, (await Send(client, HttpMethod.Post, "/api/jobs", tokens.Operator,
                Job(adminJob.Name.ToUpperInvariant()))).StatusCode);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task RunnerReportEndpointsDoNotRevealHiddenBuilds()
    {
        await using var factory = new WalisApiFactory();
        var (client, tokens) = await SignInRequiredAsync(factory);
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var hidden = await jobs.CreateJobAsync(Draft($"hidden-runner-{Guid.NewGuid():N}", UserRole.Admin, UserRole.Admin));
        var build = await jobs.QueueBuildAsync(hidden.Id, "test", "secret-runner-name");

        var response = await Send(client, HttpMethod.Post, $"/api/builds/{build.Id}/log", tokens.Operator, new { log = "x" });

        // Same answer as for a build that does not exist, and no runner name in it.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("secret-runner-name", await response.Content.ReadAsStringAsync());
    }

    private static JobDraft Draft(string name, string viewRole, string runRole)
    {
        return new JobDraft(
            name, null, null, null, "main", null, null, "echo hi", 5, null,
            TriggerMode.Manual, null, false, false, ScriptModes.Source, null, null, null, viewRole, runRole);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperatorCannotRaiseJobRunRoleToAdmin(bool update)
    {
        await using var factory = new WalisApiFactory();
        var (client, tokens) = await SignInRequiredAsync(factory);
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var job = await jobs.CreateJobAsync(Draft($"role-{Guid.NewGuid():N}", UserRole.Viewer, UserRole.Operator));

        var response = await Send(client, update ? HttpMethod.Put : HttpMethod.Post,
            update ? $"/api/jobs/{job.Id}" : "/api/jobs", tokens.Operator,
            new
            {
                name = job.Name + "-changed", command = "echo hi", timeoutMinutes = 5,
                triggerKind = "manual", runRole = UserRole.Admin
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(UserRole.Operator, (await jobs.GetJobAsync(job.Id))!.RunRole);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyAdminsCanSaveTrustedUnsandboxedJobs(bool update)
    {
        await using var factory = new WalisApiFactory();
        var (client, tokens) = await SignInRequiredAsync(factory);
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var job = await jobs.CreateJobAsync(Draft($"trusted-{Guid.NewGuid():N}", UserRole.Viewer, UserRole.Operator));
        var path = update ? $"/api/jobs/{job.Id}" : "/api/jobs";
        var method = update ? HttpMethod.Put : HttpMethod.Post;

        object Body(bool trusted, string? name = null) => new
        {
            name = name ?? job.Name, command = "echo hi", timeoutMinutes = 5, triggerKind = "manual",
            runRole = UserRole.Operator, trustedUnsandboxed = trusted, denyNetwork = true
        };

        // An operator may keep saving the job as long as the flag stays off; turning it on is refused outright.
        var refused = await Send(client, method, path, tokens.Operator, Body(true, $"{job.Name}-op"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("只有管理員", await refused.Content.ReadAsStringAsync());
        Assert.False((await jobs.GetJobAsync(job.Id))!.TrustedUnsandboxed);

        var plain = await Send(client, method, path, tokens.Operator, Body(false, $"{job.Name}-plain"));
        Assert.True(plain.IsSuccessStatusCode, await plain.Content.ReadAsStringAsync());

        var accepted = await Send(client, method, path, tokens.Admin, Body(true, $"{job.Name}-admin"));
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
        var saved = await accepted.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(saved.GetProperty("trustedUnsandboxed").GetBoolean());
        Assert.True(saved.GetProperty("denyNetwork").GetBoolean());

        // Trusted code and its environment stay administrator-controlled even when operators may trigger it.
        var savedId = saved.GetProperty("id").GetGuid();
        var keep = await Send(client, HttpMethod.Put, $"/api/jobs/{savedId}", tokens.Operator, Body(true));
        Assert.Equal(HttpStatusCode.Forbidden, keep.StatusCode);
        Assert.True((await jobs.GetJobAsync(savedId))!.TrustedUnsandboxed);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, HttpMethod.Put, $"/api/jobs/{savedId}", tokens.Operator, Body(false))).StatusCode);
        var variables = factory.Services.GetRequiredService<IEnvironmentVariableRepository>();
        var variable = await variables.CreateAsync(new EnvironmentVariableDraft("PATH", "/usr/bin:/bin", false, savedId));
        foreach (var verb in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var variablePath = verb == HttpMethod.Post ? "/api/environment-variables/" : $"/api/environment-variables/{variable.Id}";
            var body = verb == HttpMethod.Delete ? null : new { name = "PATH", value = "/tmp/untrusted", isSecret = false, jobId = savedId };
            Assert.Equal(HttpStatusCode.Forbidden,
                (await Send(client, verb, variablePath, tokens.Operator, body)).StatusCode);
        }
        Assert.Equal("/usr/bin:/bin", (await variables.GetAsync(variable.Id))!.Value);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Put, $"/api/environment-variables/{variable.Id}", tokens.Admin,
            new { name = "PATH", value = "/bin:/usr/bin", isSecret = false, jobId = savedId })).StatusCode);
    }

    [Theory]
    [InlineData(UserRole.Operator, UserRole.Admin, false)]
    [InlineData(UserRole.Operator, UserRole.Operator, true)]
    [InlineData(UserRole.Admin, UserRole.Admin, true)]
    [InlineData(UserRole.Admin, UserRole.Operator, true)]
    public async Task DownstreamTriggerCannotExceedUpstreamRunRole(string upstreamRole, string downstreamRole, bool allowed)
    {
        await using var factory = new WalisApiFactory();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        await factory.Services.GetRequiredService<IRunnerRepository>()
            .CreateAsync(new RunnerDraft("runner-never", "test", "1", "", 1, false));
        var downstream = await jobs.CreateJobAsync(Draft($"down-{Guid.NewGuid():N}", UserRole.Viewer, downstreamRole)
            with
            {
                RunnerName = "runner-never"
            });
        var upstream = await jobs.CreateJobAsync(Draft($"up-{Guid.NewGuid():N}", UserRole.Viewer, upstreamRole)
            with
            {
                DownstreamJson = JsonSerializer.Serialize(new[] { new { jobName = downstream.Name, when = "success" } })
            });
        var build = new Build { Id = Guid.NewGuid(), JobId = upstream.Id, Number = 1, Status = BuildStatus.Succeeded };

        await factory.Services.GetRequiredService<BuildCompletionService>()
            .HandleAsync(upstream, build, null, false, CancellationToken.None);

        var builds = await jobs.ListBuildsAsync(new BuildQuery(null, null, downstream.Id, null, 20));
        Assert.Equal(allowed ? 1 : 0, builds.Count);
    }

    [Theory]
    [InlineData("demoted")]
    [InlineData("promoted")]
    [InlineData("deleted")]
    public async Task UpstreamRoleChangeOrDeletionCannotLetExistingBuildTriggerDownstreamBeyondPermission(string change)
    {
        await using var factory = new WalisApiFactory();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        await factory.Services.GetRequiredService<IRunnerRepository>()
            .CreateAsync(new RunnerDraft("runner-never", "test", "1", "", 1, false));
        var downstream = await jobs.CreateJobAsync(Draft($"down-{Guid.NewGuid():N}", UserRole.Admin, UserRole.Admin)
            with
            {
                RunnerName = "runner-never"
            });
        var draft = Draft($"up-{Guid.NewGuid():N}", UserRole.Viewer,
                change == "promoted" ? UserRole.Operator : UserRole.Admin)
            with
            {
                DownstreamJson = JsonSerializer.Serialize(new[] { new { jobName = downstream.Name, when = "success" } })
            };
        var upstream = await jobs.CreateJobAsync(draft);
        if (change == "deleted")
            Assert.True(await jobs.DeleteJobAsync(upstream.Id));
        else
            await jobs.UpdateJobAsync(upstream.Id,
                draft with { RunRole = change == "demoted" ? UserRole.Operator : UserRole.Admin });

        await factory.Services.GetRequiredService<BuildCompletionService>().HandleAsync(upstream,
            new Build { Id = Guid.NewGuid(), JobId = upstream.Id, Number = 1, Status = BuildStatus.Succeeded },
            null, false, CancellationToken.None);

        Assert.Empty(await jobs.ListBuildsAsync(new BuildQuery(null, null, downstream.Id, null, 20)));
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
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);

        return await client.SendAsync(request);
    }

    private sealed record Tokens(string Admin, string Operator, string Viewer);
}
