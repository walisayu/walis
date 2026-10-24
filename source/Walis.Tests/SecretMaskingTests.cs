using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Walis.Core.Actions;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Tests;

/// <summary>Build-log masking beyond the exact string: encodings of a secret, credentials inside repository URLs, and the "masking off" notice.</summary>
public class SecretMaskingTests
{
    private static Job Job(string command, string? repository = null)
    {
        return new Job
        {
            Id = Guid.NewGuid(),
            Name = "遮蔽",
            Command = command,
            Repository = repository,
            TimeoutMinutes = 1,
            WorkingDirectory = Path.GetTempPath()
        };
    }

    private static BuildExecutionOptions Options(IReadOnlyDictionary<string, string> variables, IReadOnlyList<string> secrets, bool mask = true)
    {
        return new BuildExecutionOptions(Path.GetTempPath(), null, variables, secrets, mask);
    }

    [Fact]
    public async Task EncodedFormsOfASecretAreMaskedToo()
    {
        if (OperatingSystem.IsWindows()) return;

        const string secret = "s3cr3t/token+value=";
        var job = Job("printf '%s' \"$TOKEN\" | base64; echo; printf '%s' \"$TOKEN\" | base64 | tr -d '=' | tr '+/' '-_'; echo; " +
                      "printf '%s' \"$TOKEN\" | sed 's#/#%2F#g; s#+#%2B#g; s#=#%3D#g'; echo");
        var reports = new List<string>();

        var result = await new ShellCommandExecutor().ExecuteAsync(
            job,
            Options(new Dictionary<string, string> { ["TOKEN"] = secret }, [secret]),
            new SynchronousProgress(log => reports.Add(log)),
            CancellationToken.None);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.DoesNotContain(secret, result.Completion.Log);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret)), result.Completion.Log);
        Assert.DoesNotContain(Uri.EscapeDataString(secret), result.Completion.Log);
        // Three encoded echoes, three masks (the command line itself only shows "$TOKEN").
        Assert.True(result.Completion.Log.Split("***").Length - 1 >= 3, result.Completion.Log);
        Assert.All(reports, report => Assert.DoesNotContain(secret, report));
    }

    private sealed class SynchronousProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value)
        {
            handler(value);
        }
    }

    [Fact]
    public void SanitizeLogMasksEncodedVariantsOfRemoteOutput()
    {
        const string secret = "hunter2-XYZ";
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret));
        var text = $"plain {secret}; base64 {base64}; json {JsonSerializer.Serialize(secret)[1..^1]}; html {WebUtilityEncode(secret)}";

        var masked = ShellCommandExecutor.SanitizeLog(text, [secret], true);

        Assert.DoesNotContain(secret, masked);
        Assert.DoesNotContain(base64, masked);
        Assert.Equal(text, ShellCommandExecutor.SanitizeLog(text, [secret], false));
    }

    private static string WebUtilityEncode(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value);
    }

    [Fact]
    public async Task DisabledMaskingIsAnnouncedOnTheFirstLineOfTheLog()
    {
        if (OperatingSystem.IsWindows()) return;

        const string secret = "visible-secret-value";
        var job = Job("printf '%s' \"$TOKEN\"");

        var masked = await new ShellCommandExecutor().ExecuteAsync(
            job, Options(new Dictionary<string, string> { ["TOKEN"] = secret }, [secret]), CancellationToken.None);
        var unmasked = await new ShellCommandExecutor().ExecuteAsync(
            job, Options(new Dictionary<string, string> { ["TOKEN"] = secret }, [secret], false), CancellationToken.None);

        Assert.DoesNotContain(ShellCommandExecutor.MaskingDisabledNotice, masked.Completion.Log);
        Assert.StartsWith(ShellCommandExecutor.MaskingDisabledNotice, unmasked.Completion.Log);
        Assert.Contains(secret, unmasked.Completion.Log);
    }

    [Fact]
    public async Task CredentialsInsideTheRepositoryUrlAreMaskedEvenWithoutASecretVariable()
    {
        if (OperatingSystem.IsWindows()) return;

        var job = Job(
            "echo \"$WALIS_REPOSITORY\"; echo 'https://deploy:second-token-value@git.example.com/x.git'",
            "https://deploy:first-token-value@git.example.com/org/repo.git");

        var result = await new ShellCommandExecutor().ExecuteAsync(
            job, Options(new Dictionary<string, string>(), []), CancellationToken.None);

        Assert.Equal(BuildStatus.Succeeded, result.Completion.Status);
        Assert.DoesNotContain("first-token-value", result.Completion.Log);
        Assert.DoesNotContain("second-token-value", result.Completion.Log);
        Assert.Contains("https://***@git.example.com/org/repo.git", result.Completion.Log);
        Assert.Contains("https://***@git.example.com/x.git", result.Completion.Log);
    }

    [Theory]
    [InlineData("https://user:token@github.com/org/repo.git", true)]
    [InlineData("https://ghp_token@github.com/org/repo.git", true)]
    [InlineData("http://user@host/repo", true)]
    [InlineData("ssh://git:password@host/repo.git", true)]
    [InlineData("ssh://git@host/repo.git", false)]
    [InlineData("git@github.com:org/repo.git", false)]
    [InlineData("https://github.com/org/repo.git", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RepositoryUserInfoIsDetected(string? repository, bool expected)
    {
        Assert.Equal(expected, RepositoryCredentials.HasUserInfo(repository));
    }

    [Fact]
    public void RepositoryCredentialsAreExtractedAndStripped()
    {
        var secrets = RepositoryCredentials.ExtractSecrets(
            "https://deploy:p%40ss-word@host/a.git",
            "git clone 'https://tokenonly@host/b.git' && ssh://git@host/c.git",
            null);

        Assert.Contains("deploy:p%40ss-word", secrets);
        Assert.Contains("p%40ss-word", secrets);
        Assert.Contains("p@ss-word", secrets);
        Assert.Contains("tokenonly", secrets);
        Assert.DoesNotContain("git", secrets);

        Assert.Equal("https://host/a.git", RepositoryCredentials.StripUserInfo("https://deploy:p%40ss-word@host/a.git"));
        Assert.Equal("ssh://host/c.git", RepositoryCredentials.StripUserInfo("ssh://git@host/c.git"));
        Assert.Equal("git@github.com:org/repo.git", RepositoryCredentials.StripUserInfo("git@github.com:org/repo.git"));
    }

    [Fact]
    public void SourceActionPrintsTheRepositoryWithoutCredentials()
    {
        var definitions = BuiltInActions.All.ToDictionary(action => action.Id, StringComparer.Ordinal);
        var step = new ActionStep { Id = "1", ActionId = "git.source", Parameters = new Dictionary<string, string?>() };

        var result = ActionScriptCompiler.Compile([step], definitions);

        Assert.True(result.IsValid);
        Assert.Contains("WALIS_SOURCE_DISPLAY=$(printf '%s' \"$WALIS_SOURCE_REPOSITORY\" | sed -E", result.Script);
        Assert.Contains("echo \"已取得 $WALIS_SOURCE_DISPLAY 的 $WALIS_SOURCE_BRANCH\"", result.Script);
        Assert.DoesNotContain("echo \"已取得 $WALIS_SOURCE_REPOSITORY", result.Script);
    }
}

public class SecretMaskingApiTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task JobFormRejectsRepositoryWithEmbeddedCredentials()
    {
        var client = factory.CreateClient();

        var rejected = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"帳密網址 {Guid.NewGuid():N}",
            repository = "https://deploy:ghp_token@github.com/org/repo.git",
            triggerKind = "manual",
            timeoutMinutes = 5,
            command = "echo hi"
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Contains("credential.helper", problem.GetProperty("errors").GetProperty("repository")[0].GetString());

        var sshLogin = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"SSH 登入名 {Guid.NewGuid():N}",
            repository = "ssh://git@github.com/org/repo.git",
            triggerKind = "manual",
            timeoutMinutes = 5,
            command = "echo hi"
        });
        Assert.Equal(HttpStatusCode.Created, sshLogin.StatusCode);
    }

    [Fact]
    public async Task PasswordParameterValuesReportedByARemoteRunnerAreMaskedBeforeStorage()
    {
        const string password = "deploy-p@ssw0rd-value";
        var client = factory.CreateClient();
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var runners = factory.Services.GetRequiredService<IRunnerRepository>();
        var keys = factory.Services.GetRequiredService<IApiKeyRepository>();

        var name = $"masker-{Guid.NewGuid():N}"[..16];
        await runners.CreateAsync(new RunnerDraft(name, null, null, null, 1, false));
        var job = await jobs.CreateJobAsync(new JobDraft($"遠端密碼 {Guid.NewGuid():N}", "Walis", null, null, "main", name, null,
            "echo \"$DEPLOY_PASSWORD\"", 5, null, TriggerMode.Manual, null, true, false, ScriptModes.Source, null,
            ParametersJson: """[{"name":"DEPLOY_PASSWORD","type":"password"}]"""));
        var build = (await jobs.TryQueueBuildsAsync(job.Id, "test", name,
            [new BuildQueueItem(SecretParametersJson: BuildParameters.SerializeValues(
                new Dictionary<string, string> { ["DEPLOY_PASSWORD"] = password }))]))![0];
        var issued = await keys.CreateAsync(new ApiKeyDraft($"key-{name}", UserRole.Operator, null, name), "test");

        using var claimRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/runners/{name}/claim");
        claimRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token);
        claimRequest.Headers.Add("X-Walis-Runner-Protocol", "2");
        claimRequest.Headers.Add("X-Walis-Runner-Isolation", "none");
        var claimed = await client.SendAsync(claimRequest);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        var payload = await claimed.Content.ReadFromJsonAsync<JsonElement>(Json);
        var stepId = payload.GetProperty("steps").EnumerateArray().Single().GetProperty("id").GetGuid();
        Assert.Contains(password, payload.GetProperty("secrets").EnumerateArray().Select(item => item.GetString()));

        // A runner that does not mask (old version, or masking disabled on its side) reports the raw value everywhere.
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password));
        Assert.Equal(HttpStatusCode.NoContent,
            (await Send(client, issued.Token, $"/api/builds/{build.Id}/log", new { log = $"running {password}" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await Send(client, issued.Token, $"/api/builds/{build.Id}/steps/{stepId}/log", new { log = $"step {password}" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await Send(client, issued.Token, $"/api/builds/{build.Id}/steps/{stepId}/result",
                new { status = "Succeeded", exitCode = 0, log = $"step done {password} {base64}", outputs = new { echoed = password } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await Send(client, issued.Token, $"/api/builds/{build.Id}/result",
                new { status = "Succeeded", exitCode = 0, log = $"done {password}" })).StatusCode);

        var stored = await client.GetFromJsonAsync<JsonElement>($"/api/builds/{build.Id}", Json);
        var text = stored.GetRawText();
        Assert.DoesNotContain(password, text);
        Assert.DoesNotContain(base64, text);
        Assert.Contains("done ***", stored.GetProperty("log").GetString());
        var step = stored.GetProperty("steps").EnumerateArray().Single();
        Assert.Equal("step done *** ***", step.GetProperty("log").GetString());
        Assert.Equal("***", step.GetProperty("outputs").GetProperty("echoed").GetString());
        Assert.False(stored.TryGetProperty("secretParametersJson", out _));
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string token, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body, options: Json);
        return await client.SendAsync(request);
    }
}
