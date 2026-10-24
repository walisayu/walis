using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Walis.Api.Services;
using Walis.Core.Actions;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

public sealed class WalisApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestAdministratorHeader = "X-Walis-Test-Administrator";
    private const string TestRemoteAddressHeader = "X-Walis-Test-Remote-Address";

    /// <summary>What the injected test administrator sends as its current password for step-up (X-Walis-Reauth).</summary>
    public const string TestAdministratorPassword = "test-administrator-password";
    // The database lives inside a private data directory of its own: the data directory (and the database's directory) are
    // protected paths, so a database directly in the temp root would make the whole temp directory off limits to builds.
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"walis-api-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(_dataDirectory, "walis.db");

    public string HostEnvironment { get; init; } = "Test";

    public bool ForceSecureCookies { get; init; }

    /// <summary>Turns on Security:RequireHttps, the opt-in strict transport rule.</summary>
    public bool RequireHttps { get; init; }

    /// <summary>Turns on Security:RequireSetupCode, so even this machine needs the code for the first administrator.</summary>
    public bool? RequireSetupCode { get; init; }

    /// <summary>Overrides Security:AuthRateLimitPerMinute; null keeps the fixtures effectively unlimited.</summary>
    public int? AuthRateLimitPerMinute { get; init; }

    /// <summary>The action pack root directory; when set it is written into Walis:ActionPacks.</summary>
    public string? ActionPackRoot { get; init; }

    /// <summary>The <c>urls</c> value the server would bind; decides the isolation default of a fresh database.</summary>
    public string? ListeningUrls { get; init; }

    public Task InitializeAsync()
    {
        _ = Services.GetRequiredService<IServiceProvider>();
        return Task.CompletedTask;
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();

        // Only this factory's pool: ClearAllPools would close connections that tests running in parallel still use.
        using (var connection = (SqliteConnection)new Walis.Core.Database.SqliteConnectionFactory($"Data Source={DatabasePath}").Create())
            SqliteConnection.ClearPool(connection);

        // Background work of the stopped host (a last audit row, a WAL checkpoint) can still create a file while the tree is
        // deleted; cleaning a temp directory must not fail the test that used it.
        for (var attempt = 0; attempt < 5 && Directory.Exists(_dataDirectory); attempt++)
        {
            try
            {
                Directory.Delete(_dataDirectory, true);
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(100);
            }
            catch (IOException)
            {
                // Left for the OS to clean up with the rest of the temp directory.
            }
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment(HostEnvironment);
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Walis"] = $"Data Source={DatabasePath}",
                [DataDirectory.ConfigurationKey] = _dataDirectory,
                // A fixed key keeps the API tests from writing a shared secret.key into the content root's data directory.
                ["Security:EncryptionKey"] = TemporaryDatabase.TestEncryptionKey,
                ["WALIS_SECURE_COOKIES"] = ForceSecureCookies ? "true" : null,
                ["Security:RequireHttps"] = RequireHttps ? "true" : null,
                ["Security:RequireSetupCode"] = RequireSetupCode switch { true => "true", false => "false", null => null },
                // The per-source sign-in ceiling is exercised by its own test; the fixtures sign in far more often than a person.
                ["Security:AuthRateLimitPerMinute"] = AuthRateLimitPerMinute?.ToString() ?? "100000",
                ["Walis:ActionPacks:0"] = ActionPackRoot,
                ["urls"] = ListeningUrls
            }));
        builder.ConfigureServices(services =>
        {
            services.AddTransient<IStartupFilter, TestAuthenticationStartupFilter>();

            // The injected test administrator has no stored password; it re-authenticates with a fixed test value. Every real
            // session still goes through the production check (and the negative tests use real accounts).
            services.AddSingleton<IReauthentication>(provider => new TestReauthentication(new Reauthentication(
                provider.GetRequiredService<ISecurityRepository>(),
                provider.GetRequiredService<SignInThrottle>())));
        });

        return base.CreateHost(builder);
    }

    /// <summary>
    ///     The old endpoint tests are not about sign-in; an admin identity that exists only in the Test host is injected by default.
    ///     Tests that verify fail-closed, cookies and bootstrap must explicitly use CreateAnonymousClient.
    /// </summary>
    public new HttpClient CreateClient()
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Add(TestAdministratorHeader, "true");
        client.DefaultRequestHeaders.Add(Reauthentication.HeaderName, TestAdministratorPassword);
        return client;
    }

    public HttpClient CreateAnonymousClient()
    {
        return base.CreateClient();
    }

    public HttpClient CreateRemoteAnonymousClient(string address = "203.0.113.10")
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteAddressHeader, address);
        return client;
    }

    /// <summary>The injected test administrator, but arriving from the given address instead of loopback.</summary>
    public HttpClient CreateRemoteClient(string address)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteAddressHeader, address);
        return client;
    }

    private sealed class TestAuthenticationStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, continuePipeline) =>
                {
                    // TestServer has no socket, so bootstrap cannot see RemoteIpAddress; explicitly simulate a local request.
                    context.Connection.RemoteIpAddress =
                        context.Request.Headers.TryGetValue(TestRemoteAddressHeader, out var remote)
                            ? IPAddress.TryParse(remote.ToString(), out var parsed) ? parsed : IPAddress.Parse("203.0.113.10")
                            : IPAddress.Loopback;

                    if (context.Request.Headers.ContainsKey(TestAdministratorHeader) &&
                        !context.Request.Headers.ContainsKey("Authorization") &&
                        !context.Request.Cookies.ContainsKey(SessionCookieService.SessionCookieName))
                        context.Items[RequestAccess.SessionKey] = new AuthSession
                        {
                            Token = "test-only-administrator",
                            Username = "test-admin",
                            DisplayName = "Test Administrator",
                            Role = UserRole.Admin,
                            HasOtp = true,
                            CreatedAt = DateTime.UtcNow,
                            ExpiresAt = DateTime.UtcNow.AddHours(1)
                        };

                    await continuePipeline();
                });

                next(app);
            };
        }
    }
}

public class ApiEndpointTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task HealthCheckResponds()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task JobListIncludesDefaultData()
    {
        var jobs = await factory.CreateClient().GetFromJsonAsync<List<Job>>("/api/jobs", Json);

        Assert.NotNull(jobs);
        Assert.NotEmpty(jobs);
    }

    [Fact]
    public async Task MissingRequiredFieldsReturnValidationError()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/jobs", new
        {
            name = "",
            command = "",
            triggerKind = "manual",
            timeoutMinutes = 30
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("command", out _));
    }

    [Fact]
    public async Task AmplifiableJobAndRunnerFieldsHaveCentralLimits()
    {
        var client = factory.CreateClient();
        var tooManyActions = Enumerable.Range(0, 101).Select(index => new
        {
            id = $"step-{index}",
            actionId = "shell.run",
            name = "step",
            parameters = new Dictionary<string, string> { ["command"] = "echo ok" }
        }).ToArray();

        var job = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"bounded-{Guid.NewGuid():N}",
            project = new string('p', 201),
            command = "",
            scriptMode = "actions",
            actions = tooManyActions,
            triggerKind = "manual",
            timeoutMinutes = 30
        });
        Assert.Equal(HttpStatusCode.BadRequest, job.StatusCode);
        var jobBody = await job.Content.ReadAsStringAsync();
        Assert.Contains("project", jobBody);
        Assert.Contains("actions", jobBody);

        var runner = await client.PostAsJsonAsync("/api/runners", new
        {
            name = $"runner-{Guid.NewGuid():N}"[..20],
            operatingSystem = new string('o', 301),
            version = new string('v', 61),
            labels = string.Join(',', Enumerable.Range(0, 33).Select(index => $"tag{index}")),
            totalSlots = 1,
            isPaused = false
        });
        Assert.Equal(HttpStatusCode.BadRequest, runner.StatusCode);
        var runnerBody = await runner.Content.ReadAsStringAsync();
        Assert.Contains("operatingSystem", runnerBody);
        Assert.Contains("version", runnerBody);
        Assert.Contains("labels", runnerBody);
    }

    [Fact]
    public async Task ScheduledJobCronIsValidated()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/jobs", new
        {
            name = "壞排程",
            command = "echo hi",
            triggerKind = "cron",
            cron = "99 * * *",
            timeoutMinutes = 30
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("cron", out _));
    }

    [Fact]
    public async Task JobCanRunImmediatelyAfterCreation()
    {
        var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"整合測試 {Guid.NewGuid():N}",
            project = "Walis",
            command = "echo integration",
            triggerKind = "manual",
            timeoutMinutes = 5,
            notifyOnFailure = true
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var job = await created.Content.ReadFromJsonAsync<Job>(Json);

        var run = await client.PostAsJsonAsync($"/api/jobs/{job!.Id}/builds", new { triggeredBy = "manual" });
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);

        var build = await run.Content.ReadFromJsonAsync<Build>(Json);
        Assert.Equal(1, build!.Number);
        Assert.Equal(Runner.LocalName, build.RunnerName);
    }

    [Fact]
    public async Task CleanWorkspaceAfterBuildEmptiesTheManagedWorkspaceOnceTheBuildEnds()
    {
        if (OperatingSystem.IsWindows()) return;

        var client = factory.CreateClient();
        var directory = Directory.CreateTempSubdirectory("walis-clean-after-api");

        try
        {
            // The script records where it ran and leaves a file behind; after the build that file must be gone.
            var where = Path.Combine(directory.FullName, "where");
            var created = await client.PostAsJsonAsync("/api/jobs", new
            {
                name = $"建置後清空 {Guid.NewGuid():N}",
                branch = "main",
                command = $"pwd > '{where}'; echo leftover > token.txt; exit 1",
                triggerKind = "manual",
                timeoutMinutes = 5,
                notifyOnFailure = false,
                cleanWorkspaceAfterBuild = true
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var job = await created.Content.ReadFromJsonAsync<Job>(Json);
            Assert.True(job!.CleanWorkspaceAfterBuild);

            var run = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds", new { triggeredBy = "manual" });
            Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
            var build = (await run.Content.ReadFromJsonAsync<Build>(Json))!;

            Build? finished = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                finished = await client.GetFromJsonAsync<Build>($"/api/builds/{build.Id}", Json);
                if (finished!.Status is not (BuildStatus.Queued or BuildStatus.Running)) break;
                await Task.Delay(200);
            }

            // Failed on purpose: the clean must happen for failed builds too, since that is where leftovers are most likely.
            Assert.Equal(BuildStatus.Failed, finished!.Status);
            Assert.Contains("建置後已清空工作目錄", finished.Log);

            var workspace = (await File.ReadAllTextAsync(where)).Trim();
            Assert.True(Directory.Exists(workspace));
            Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task RunningLocalBuildCanBeCancelledAndEndsAsCanceled()
    {
        if (OperatingSystem.IsWindows()) return;

        var client = factory.CreateClient();
        var directory = Directory.CreateTempSubdirectory("walis-cancel");

        try
        {
            var started = Path.Combine(directory.FullName, "started");
            var finishedMarker = Path.Combine(directory.FullName, "finished");
            var created = await client.PostAsJsonAsync("/api/jobs", new
            {
                name = $"可取消 {Guid.NewGuid():N}",
                branch = "main",

                // No workingDirectory: the test database lives in the temp directory, which is a protected path, so the
                // job uses the managed workspace and only the marker file lives in the temp directory.
                command = $"touch '{started}'; i=0; while [ $i -lt 600 ]; do sleep 0.1; i=$((i+1)); done; touch '{finishedMarker}'",
                triggerKind = "manual",
                timeoutMinutes = 5,
                notifyOnFailure = false
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var job = await created.Content.ReadFromJsonAsync<Job>(Json);

            var run = await client.PostAsJsonAsync($"/api/jobs/{job!.Id}/builds", new { triggeredBy = "manual" });
            Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
            var build = (await run.Content.ReadFromJsonAsync<Build>(Json))!;

            // Wait until the shell is really running, then ask for the cancel.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(started) && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (!File.Exists(started))
            {
                var stuck = await client.GetFromJsonAsync<Build>($"/api/builds/{build.Id}", Json);
                Assert.Fail($"建置沒有在時限內開始執行：{stuck!.Status}\n{stuck.Log}");
            }

            var cancelled = await client.PostAsync($"/api/builds/{build.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

            // The worker polls every two seconds and the step's process group is killed; the build must end as Canceled.
            Build? finished = null;
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                finished = await client.GetFromJsonAsync<Build>($"/api/builds/{build.Id}", Json);
                if (finished!.Status != BuildStatus.Running) break;
                await Task.Delay(200);
            }

            Assert.NotNull(finished);
            Assert.Equal(BuildStatus.Canceled, finished.Status);
            Assert.Contains("建置已依要求取消", finished.Log);
            Assert.False(File.Exists(finishedMarker), "被取消的腳本仍跑到了結尾。");

            // A finished build cannot be cancelled again.
            Assert.Equal(HttpStatusCode.Conflict,
                (await client.PostAsync($"/api/builds/{build.Id}/cancel", null)).StatusCode);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task QueuedBuildStartsAutomaticallyAfterMaintenanceLeaseReleased()
    {
        var client = factory.CreateClient();
        var leases = factory.Services.GetRequiredService<ILeaseRepository>();
        var owner = $"restore-test-{Guid.NewGuid():N}";
        Assert.True(await leases.TryAcquireAsync(LeaseNames.Maintenance, owner, TimeSpan.FromMinutes(5)));

        try
        {
            var created = await client.PostAsJsonAsync("/api/jobs", new
            {
                name = $"維護中排隊 {Guid.NewGuid():N}",
                command = "echo hi",
                triggerKind = "manual",
                timeoutMinutes = 5
            });
            var job = await created.Content.ReadFromJsonAsync<Job>(Json);
            var run = await client.PostAsJsonAsync($"/api/jobs/{job!.Id}/builds", new { });
            var build = await run.Content.ReadFromJsonAsync<Build>(Json);

            // Under maintenance: the claim is refused and the build stays in the queue instead of disappearing or failing.
            await Task.Delay(TimeSpan.FromSeconds(2));
            var queued = await client.GetFromJsonAsync<Build>($"/api/builds/{build!.Id}", Json);
            Assert.Equal(BuildStatus.Queued, queued!.Status);

            await leases.ReleaseAsync(LeaseNames.Maintenance, owner);

            // A single node has no database polling and relies on the executor re-queueing; without it this would stay Queued forever.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            Build? latest = null;
            while (DateTime.UtcNow < deadline)
            {
                latest = await client.GetFromJsonAsync<Build>($"/api/builds/{build.Id}", Json);
                if (latest!.Status != BuildStatus.Queued) break;

                await Task.Delay(500);
            }

            Assert.NotEqual(BuildStatus.Queued, latest!.Status);
        }
        finally
        {
            await leases.ReleaseAsync(LeaseNames.Maintenance, owner);
        }
    }

    [Fact]
    public async Task BuildParametersGetDefaultsAreInjectedAndPasswordsAreNotReturned()
    {
        var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"參數任務 {Guid.NewGuid():N}",
            command = "echo $ENV",
            triggerKind = "manual",
            timeoutMinutes = 5,
            parametersJson =
                """[{"name":"ENV","type":"choice","options":["stg","prod"]},{"name":"TOKEN","type":"password"}]"""
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var job = await created.Content.ReadFromJsonAsync<Job>(Json);

        var run = await client.PostAsJsonAsync($"/api/jobs/{job!.Id}/builds", new
        {
            triggeredBy = "manual",
            parameters = new Dictionary<string, string> { ["ENV"] = "prod", ["TOKEN"] = "hunter2" }
        });
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        var raw = await run.Content.ReadAsStringAsync();
        var build = JsonSerializer.Deserialize<Build>(raw, Json)!;
        Assert.Equal("prod", BuildParameters.ParseValues(build.ParametersJson)["ENV"]);
        Assert.DoesNotContain("hunter2", raw);
        Assert.DoesNotContain("secretParametersJson", raw, StringComparison.OrdinalIgnoreCase);

        // Undeclared parameters and values outside the choices are both rejected.
        var unknown = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds",
            new { parameters = new Dictionary<string, string> { ["NOPE"] = "1" } });
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        var bad = await client.PostAsJsonAsync($"/api/jobs/{job.Id}/builds",
            new { parameters = new Dictionary<string, string> { ["ENV"] = "dev" } });
        Assert.Equal(HttpStatusCode.Conflict, bad.StatusCode);

        // An invalid declaration itself cannot be saved.
        var invalid = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"壞參數 {Guid.NewGuid():N}",
            command = "echo hi",
            triggerKind = "manual",
            timeoutMinutes = 5,
            parametersJson = """[{"name":"bad name","type":"string"}]"""
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task UpstreamSuccessTriggersDownstreamWithUpstreamInfo()
    {
        if (OperatingSystem.IsWindows()) return;

        var client = factory.CreateClient();
        var stamp = Guid.NewGuid().ToString("N");

        var downstream = await (await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"下游 {stamp}",
            command = "echo $WALIS_UPSTREAM_JOB",
            triggerKind = "manual",
            timeoutMinutes = 5
        })).Content.ReadFromJsonAsync<Job>(Json);

        var upstream = await (await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"上游 {stamp}",
            command = "echo hi",
            triggerKind = "manual",
            timeoutMinutes = 5,
            downstreamJson = $$"""[{"jobName":"下游 {{stamp}}","when":"success"}]"""
        })).Content.ReadFromJsonAsync<Job>(Json);

        // Setting itself as downstream must be rejected.
        var self = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"自環 {stamp}",
            command = "echo hi",
            triggerKind = "manual",
            timeoutMinutes = 5,
            downstreamJson = $$"""[{"jobName":"自環 {{stamp}}","when":"always"}]"""
        });
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        var run = await client.PostAsJsonAsync($"/api/jobs/{upstream!.Id}/builds", new { });
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);

        Build? triggered = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && triggered is null)
        {
            var builds = await client.GetFromJsonAsync<List<Build>>($"/api/jobs/{downstream!.Id}/builds", Json);
            triggered = builds?.FirstOrDefault();
            if (triggered is null) await Task.Delay(500);
        }

        var upstreamBuild =
            (await client.GetFromJsonAsync<List<Build>>($"/api/jobs/{upstream.Id}/builds", Json))!.First();
        Assert.True(triggered is not null, $"下游沒有被觸發；上游建置狀態 {upstreamBuild.Status}，日誌：{upstreamBuild.Log}");
        Assert.StartsWith("downstream:", triggered.TriggeredBy);
        var values = BuildParameters.ParseValues(triggered.ParametersJson);
        Assert.Equal(upstream.Name, values[DownstreamJobs.UpstreamJobVariable]);
        Assert.Equal("1", values[DownstreamJobs.UpstreamBuildVariable]);
        Assert.Equal(BuildStatus.Succeeded, values[DownstreamJobs.UpstreamStatusVariable]);
        Assert.Equal("1", values[BuildLauncher.ChainDepthVariable]);
    }

    [Fact]
    public async Task ImportingJenkinsfileReturnsJobDraftAndWarnings()
    {
        var client = factory.CreateClient();

        var preview = await client.PostAsJsonAsync("/api/jobs/import/jenkins", new
        {
            name = "匯入測試",
            content = """
                      pipeline {
                          agent any
                          triggers { cron('H 4 * * *') }
                          stages {
                              stage('Build') { steps { sh 'make' } }
                              stage('Notify') { steps { slackSend message: 'done' } }
                          }
                      }
                      """
        });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var payload = await preview.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("declarative", payload.GetProperty("format").GetString());
        var job = payload.GetProperty("job");
        Assert.Equal("匯入測試", job.GetProperty("name").GetString());
        Assert.Equal("cron", job.GetProperty("triggerKind").GetString());
        var actions = job.GetProperty("actions").EnumerateArray().ToList();
        Assert.Single(actions);
        Assert.Equal("shell.run", actions[0].GetProperty("actionId").GetString());
        Assert.Equal("make", actions[0].GetProperty("parameters").GetProperty("command").GetString());
        Assert.Contains(payload.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetProperty("message").GetString()!.Contains("slackSend"));

        // The previewed draft can be used directly to create a job.
        var created = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"匯入建立 {Guid.NewGuid():N}",
            triggerKind = job.GetProperty("triggerKind").GetString(),
            cron = job.GetProperty("cron").GetString(),
            timeoutMinutes = job.GetProperty("timeoutMinutes").GetInt32(),
            scriptMode = "actions",
            actions = actions.Select(action => JsonSerializer.Deserialize<ActionStep>(action.GetRawText(), Json))
                .ToList(),
            allowConcurrentBuilds = job.GetProperty("allowConcurrentBuilds").GetBoolean()
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var empty = await client.PostAsJsonAsync("/api/jobs/import/jenkins", new { content = "" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task ScheduleTimelineReturnsTriggerTimesAndAverageDurationsInWindow()
    {
        var client = factory.CreateClient();
        var stamp = Guid.NewGuid().ToString("N");

        var job = await (await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"時間表 {stamp}",
            command = "echo hi",
            triggerKind = "cron",
            cron = "*/15 * * * *",
            timeoutMinutes = 5
        })).Content.ReadFromJsonAsync<Job>(Json);

        var payload = await client.GetFromJsonAsync<JsonElement>("/api/schedules/timeline?date=2030-01-01&days=1", Json);
        var from = payload.GetProperty("from").GetDateTime().ToUniversalTime();
        var to = payload.GetProperty("to").GetDateTime().ToUniversalTime();
        Assert.Equal(TimeSpan.FromDays(1), to - from);
        Assert.Equal("2030-01-01", payload.GetProperty("date").GetString());

        var row = payload.GetProperty("jobs").EnumerateArray().Single(item => item.GetProperty("id").GetString() == job!.Id.ToString());
        var starts = row.GetProperty("starts").EnumerateArray().Select(item => item.GetDateTime().ToUniversalTime()).ToList();
        Assert.Equal(96, starts.Count);
        Assert.Equal(from, starts[0]);
        Assert.Equal(from.AddMinutes(15), starts[1]);
        Assert.All(starts, start => Assert.True(start >= from && start < to));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("averageSeconds").ValueKind);
        Assert.Empty(row.GetProperty("builds").EnumerateArray());

        var badDate = await client.GetAsync("/api/schedules/timeline?date=tomorrow");
        Assert.Equal(HttpStatusCode.BadRequest, badDate.StatusCode);

        // A manual job without builds is not listed; one that ran today appears in today's window (without estimated times).
        var manual = await (await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"手動 {stamp}",
            command = "echo hi",
            triggerKind = "manual",
            timeoutMinutes = 5
        })).Content.ReadFromJsonAsync<Job>(Json);
        var future = await client.GetFromJsonAsync<JsonElement>("/api/schedules/timeline?date=2030-01-01&days=1", Json);
        Assert.DoesNotContain(future.GetProperty("jobs").EnumerateArray(), item => item.GetProperty("id").GetString() == manual!.Id.ToString());

        await client.PostAsJsonAsync($"/api/jobs/{manual!.Id}/builds", new { });
        var today = await client.GetFromJsonAsync<JsonElement>("/api/schedules/timeline?days=1", Json);
        var manualRow = today.GetProperty("jobs").EnumerateArray().Single(item => item.GetProperty("id").GetString() == manual.Id.ToString());
        Assert.Equal("manual", manualRow.GetProperty("triggerKind").GetString());
        Assert.Empty(manualRow.GetProperty("starts").EnumerateArray());
        Assert.NotEmpty(manualRow.GetProperty("builds").EnumerateArray());
    }

    [Fact]
    public async Task DisabledJobCannotRun()
    {
        var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"停用測試 {Guid.NewGuid():N}",
            command = "echo hi",
            triggerKind = "manual",
            timeoutMinutes = 5,
            isDisabled = true
        });

        var job = await created.Content.ReadFromJsonAsync<Job>(Json);
        var run = await client.PostAsJsonAsync($"/api/jobs/{job!.Id}/builds", new { });

        Assert.Equal(HttpStatusCode.Conflict, run.StatusCode);
    }

    [Fact]
    public async Task JobKeepsItsCleanWorkspaceChoiceThroughTheApi()
    {
        var client = factory.CreateClient();
        var name = $"清空選項 {Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync("/api/jobs", new
        {
            name,
            command = "echo hi",
            triggerKind = "manual",
            timeoutMinutes = 5,
            cleanWorkspace = false
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var job = await created.Content.ReadFromJsonAsync<Job>(Json);
        Assert.False(job!.CleanWorkspace);

        var updated = await client.PutAsJsonAsync($"/api/jobs/{job.Id}", new
        {
            name,
            command = "echo hi",
            triggerKind = "manual",
            timeoutMinutes = 5,
            cleanWorkspace = true
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.True((await updated.Content.ReadFromJsonAsync<Job>(Json))!.CleanWorkspace);

        // Leaving the field out is how a client asks for the system default again.
        var cleared = await client.PutAsJsonAsync($"/api/jobs/{job.Id}", new
        {
            name,
            command = "echo hi",
            triggerKind = "manual",
            timeoutMinutes = 5
        });

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await client.GetFromJsonAsync<Job>($"/api/jobs/{job.Id}", Json))!.CleanWorkspace);
    }

    [Fact]
    public async Task EnvironmentVariableNamesCannotUseTheReservedPrefix()
    {
        var client = factory.CreateClient();

        foreach (var name in new[] { "WALIS_BUILD_NUMBER", "walis_step_x_y", "WALIS_" })
        {
            var response = await client.PostAsJsonAsync("/api/environment-variables", new
            {
                name,
                value = "shadow",
                isSecret = false,
                jobId = (Guid?)null
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("WALIS_", await response.Content.ReadAsStringAsync());
        }

        // A name that merely contains the prefix is fine.
        var allowed = await client.PostAsJsonAsync("/api/environment-variables", new
        {
            name = $"MY_WALIS_{Guid.NewGuid():N}"[..20],
            value = "ok",
            isSecret = false,
            jobId = (Guid?)null
        });
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task SecretEnvironmentVariablesAreMaskedByDefault()
    {
        var client = factory.CreateClient();
        var name = $"SECRET_{Guid.NewGuid():N}"[..20];

        await client.PostAsJsonAsync("/api/environment-variables", new
        {
            name,
            value = "非常機密",
            isSecret = true,
            jobId = (Guid?)null
        });

        var masked = await client.GetFromJsonAsync<JsonElement>("/api/environment-variables");
        var revealed = await client.GetFromJsonAsync<JsonElement>("/api/environment-variables?reveal=true");

        Assert.Equal(
            EnvironmentVariable.Mask,
            masked.EnumerateArray().First(item => item.GetProperty("name").GetString() == name)
                .GetProperty("value").GetString());

        Assert.Equal(
            "非常機密",
            revealed.EnumerateArray().First(item => item.GetProperty("name").GetString() == name)
                .GetProperty("value").GetString());
    }

    [Fact]
    public async Task MaskedSecretSentBackKeepsStoredValueWhenScopeChanges()
    {
        var client = factory.CreateClient();
        var name = $"SECRET_{Guid.NewGuid():N}"[..20];

        var job = await (await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"環境變數範圍測試 {Guid.NewGuid():N}",
            project = "Walis",
            command = "echo scope",
            triggerKind = "manual",
            timeoutMinutes = 5
        })).Content.ReadFromJsonAsync<Job>(Json);

        var created = await (await client.PostAsJsonAsync("/api/environment-variables", new
        {
            name,
            value = "原本的機密",
            isSecret = true,
            jobId = job!.Id
        })).Content.ReadFromJsonAsync<JsonElement>(Json);

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/environment-variables");

        // The editor is filled from this list, so a secret it opens always holds the mask, never the stored value.
        Assert.Equal(
            EnvironmentVariable.Mask,
            listed.EnumerateArray().First(item => item.GetProperty("name").GetString() == name)
                .GetProperty("value").GetString());

        // Which makes this exactly what "change the scope to global and save" sends.
        var moved = await client.PutAsJsonAsync(
            $"/api/environment-variables/{created.GetProperty("id").GetGuid()}",
            new { name, value = EnvironmentVariable.Mask, isSecret = true, jobId = (Guid?)null });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var revealed = await client.GetFromJsonAsync<JsonElement>("/api/environment-variables?reveal=true");
        var stored = revealed.EnumerateArray().First(item => item.GetProperty("name").GetString() == name);

        Assert.Equal("原本的機密", stored.GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("jobId").ValueKind);
    }

    [Fact]
    public async Task ClearingSecretFlagWithMaskedValueIsRejected()
    {
        var client = factory.CreateClient();
        var name = $"SECRET_{Guid.NewGuid():N}"[..20];

        var created = await (await client.PostAsJsonAsync("/api/environment-variables", new
        {
            name,
            value = "原本的機密",
            isSecret = true,
            jobId = (Guid?)null
        })).Content.ReadFromJsonAsync<JsonElement>(Json);

        // Copying the stored secret into a plaintext variable would reveal it to everyone who can read the list.
        var response = await client.PutAsJsonAsync(
            $"/api/environment-variables/{created.GetProperty("id").GetGuid()}",
            new { name, value = EnvironmentVariable.Mask, isSecret = false, jobId = (Guid?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(problem.GetProperty("errors").TryGetProperty("value", out _));

        var revealed = await client.GetFromJsonAsync<JsonElement>("/api/environment-variables?reveal=true");
        var stored = revealed.EnumerateArray().First(item => item.GetProperty("name").GetString() == name);

        Assert.Equal("原本的機密", stored.GetProperty("value").GetString());
        Assert.True(stored.GetProperty("isSecret").GetBoolean());
    }

    [Fact]
    public async Task SettingsCanBeReadAndSaved()
    {
        var client = factory.CreateClient();

        var current = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.False(string.IsNullOrWhiteSpace(current.GetProperty("databasePath").GetString()));

        var settings = current.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
        settings.SiteName = "改過的名稱";
        settings.LogRetentionDays = 30;

        var saved = await client.PutAsJsonAsync("/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var updated = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("改過的名稱", updated.GetProperty("settings").GetProperty("siteName").GetString());
        Assert.Equal(30, updated.GetProperty("settings").GetProperty("logRetentionDays").GetInt32());
    }

    [Fact]
    public async Task MalformedQuietHoursAreRejected()
    {
        var client = factory.CreateClient();
        var current = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        var settings = current.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
        settings.QuietHoursStart = "不是時間";

        var response = await client.PutAsJsonAsync("/api/settings", settings);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SecuritySettingsRejectWeakSecretsAndInsecureUrls()
    {
        var client = factory.CreateClient();
        var current = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        var settings = current.GetProperty("settings").Deserialize<WalisSettings>(Json)!;

        settings.WebhookSecret = "too-short";
        settings.CorsOrigins = "https://user:password@example.test";
        var weakWebhook = await client.PutAsJsonAsync("/api/settings", settings);
        Assert.Equal(HttpStatusCode.BadRequest, weakWebhook.StatusCode);
        var weakWebhookBody = await weakWebhook.Content.ReadAsStringAsync();
        Assert.Contains("webhookSecret", weakWebhookBody);
        Assert.Contains("corsOrigins", weakWebhookBody);

        settings.WebhookSecret = null;
        settings.CorsOrigins = "http://127.0.0.1:11124";
        settings.BackupEnabled = true;
        settings.BackupPassphrase = "too-short";
        var weakBackup = await client.PutAsJsonAsync("/api/settings", settings);
        Assert.Equal(HttpStatusCode.BadRequest, weakBackup.StatusCode);
        Assert.Contains("backupPassphrase", await weakBackup.Content.ReadAsStringAsync());

        settings.BackupEnabled = false;
        settings.BackupPassphrase = null;
        settings.BaseUrl = "https://walis.example.test?token=misleading";
        settings.SmtpHost = "smtp.example.test\r\nattacker";
        settings.SmtpPort = 70_000;
        settings.SmtpUser = "mailer";
        settings.SmtpPassword = null;
        settings.SmtpFrom = "Walis <two@example.test>";
        var unsafeExternal = await client.PutAsJsonAsync("/api/settings", settings);
        Assert.Equal(HttpStatusCode.BadRequest, unsafeExternal.StatusCode);
        var unsafeExternalBody = await unsafeExternal.Content.ReadAsStringAsync();
        Assert.Contains("baseUrl", unsafeExternalBody);
        Assert.Contains("smtpHost", unsafeExternalBody);
        Assert.Contains("smtpPort", unsafeExternalBody);
        Assert.Contains("smtpCredentials", unsafeExternalBody);
        Assert.Contains("smtpFrom", unsafeExternalBody);
    }

    [Fact]
    public async Task ExternalServiceUrlsAndKeysRejectAmbiguousOrHeaderInjectingFormats()
    {
        var client = factory.CreateClient();

        var notification = await client.PostAsJsonAsync("/api/notifications/channels", new
        {
            kind = "webhook",
            name = "bad fragment",
            target = "https://hooks.example.test/send#https://internal.example",
            isEnabled = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, notification.StatusCode);
        Assert.Contains("target", await notification.Content.ReadAsStringAsync());

        var providers = await client.GetFromJsonAsync<JsonElement>("/api/ai/providers");
        var providerId = providers.EnumerateArray().First().GetProperty("id").GetGuid();
        var ai = await client.PutAsJsonAsync($"/api/ai/providers/{providerId}", new
        {
            apiKey = "secret\r\ninjected: value",
            model = "model",
            baseUrl = "https://ai.example.test/api?redirect=internal",
            isEnabled = true,
            isDefault = false
        });
        Assert.Equal(HttpStatusCode.BadRequest, ai.StatusCode);
        var aiBody = await ai.Content.ReadAsStringAsync();
        Assert.Contains("apiKey", aiBody);
        Assert.Contains("baseUrl", aiBody);
    }

    [Fact]
    public async Task TranslationOverridesAppearInLocaleData()
    {
        var client = factory.CreateClient();

        var saved = await client.PutAsJsonAsync("/api/i18n/translations", new
        {
            locale = "en",
            source = "總覽",
            value = "Dashboard"
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var payload = await client.GetFromJsonAsync<JsonElement>("/api/i18n");
        Assert.Equal(
            "Dashboard",
            payload.GetProperty("overrides").GetProperty("en").GetProperty("總覽").GetString());

        var cleared = await client.PutAsJsonAsync("/api/i18n/translations", new
        {
            locale = "en",
            source = "總覽",
            value = ""
        });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/i18n");
        Assert.False(after.GetProperty("overrides").TryGetProperty("en", out var remaining) &&
                     remaining.TryGetProperty("總覽", out _));
    }

    [Fact]
    public async Task SourceLocaleDoesNotAcceptTranslations()
    {
        var response = await factory.CreateClient().PutAsJsonAsync("/api/i18n/translations", new
        {
            locale = "zh-Hant",
            source = "總覽",
            value = "總覽"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CallerCannotLabelAManualTriggerAsAnotherSource()
    {
        var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"觸發來源 {Guid.NewGuid():N}", project = "Walis", command = "echo hi", branch = "main",
            triggerKind = "manual", timeoutMinutes = 5, notifyOnFailure = true
        });
        created.EnsureSuccessStatusCode();
        var job = await created.Content.ReadFromJsonAsync<JsonElement>();

        var triggered = await client.PostAsJsonAsync($"/api/jobs/{job.GetProperty("id").GetGuid()}/builds",
            new { triggeredBy = "downstream:正式部署#42" });
        triggered.EnsureSuccessStatusCode();
        var build = await triggered.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("manual:downstream:正式部署#42", build.GetProperty("triggeredBy").GetString());
    }
}

internal sealed class TestReauthentication(Reauthentication real) : IReauthentication
{
    public Task<Microsoft.AspNetCore.Http.IResult?> RequireAsync(Microsoft.AspNetCore.Http.HttpContext context, CancellationToken cancellationToken)
    {
        if (RequestAccess.Session(context)?.Token != "test-only-administrator") return real.RequireAsync(context, cancellationToken);

        return Task.FromResult<Microsoft.AspNetCore.Http.IResult?>(
            context.Request.Headers[Reauthentication.HeaderName] == WalisApiFactory.TestAdministratorPassword
                ? null
                : Reauthentication.Refuse("這個操作需要再次輸入你的密碼。"));
    }
}
