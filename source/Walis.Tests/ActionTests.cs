using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Walis.Core.Actions;
using Walis.Core.Models;

namespace Walis.Tests;

public class ActionTemplateTests
{
    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] pairs)
    {
        return pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    [Fact]
    public void ParametersAreSingleQuotedByDefault()
    {
        Assert.Equal("echo 'hello world'",
            ActionTemplate.Render("echo {{message}}", Values(("message", "hello world"))));
    }

    [Fact]
    public void SingleQuotesInsideValuesAreEscaped()
    {
        var rendered = ActionTemplate.Render("echo {{message}}", Values(("message", "it's fine")));

        Assert.Equal("echo 'it'\\''s fine'", rendered);
    }

    [Fact]
    public void TripleBracesInsertVerbatim()
    {
        Assert.Equal("npm run build", ActionTemplate.Render("npm run {{{script}}}", Values(("script", "build"))));
    }

    [Fact]
    public void MissingParametersBecomeEmptyStrings()
    {
        Assert.Equal("echo ''", ActionTemplate.Render("echo {{message}}", Values()));
        Assert.Equal("run ", ActionTemplate.Render("run {{{flag}}}", Values()));
    }

    [Fact]
    public void ConditionalBlocksDependOnParameterPresence()
    {
        const string template = "git pull{{#if rebase}} --rebase{{/if}}";

        Assert.Equal("git pull --rebase", ActionTemplate.Render(template, Values(("rebase", "true"))));
        Assert.Equal("git pull", ActionTemplate.Render(template, Values(("rebase", ""))));
        Assert.Equal("git pull", ActionTemplate.Render(template, Values(("rebase", "false"))));
    }

    [Fact]
    public void UnlessBlockRendersWhenValueIsEmpty()
    {
        const string template = "{{#if useCi}}npm ci{{/if}}{{#unless useCi}}npm install{{/unless}}";

        Assert.Equal("npm ci", ActionTemplate.Render(template, Values(("useCi", "true"))));
        Assert.Equal("npm install", ActionTemplate.Render(template, Values(("useCi", ""))));
    }

    [Fact]
    public void ParametersAreStillSubstitutedInsideConditionalBlocks()
    {
        var rendered = ActionTemplate.Render("git clone{{#if branch}} --branch {{branch}}{{/if}}",
            Values(("branch", "main")));

        Assert.Equal("git clone --branch 'main'", rendered);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("something", true)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData(null, false)]
    public void RulesForHavingAValue(string? value, bool expected)
    {
        Assert.Equal(expected, ActionTemplate.IsTruthy(value));
    }
}

public class ActionTemplateNestingTests
{
    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] pairs)
    {
        return pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    [Fact]
    public void ConditionalBlocksMayNest()
    {
        const string template = "{{#if push}}git push{{#if force}} --force{{/if}} {{{name}}}{{/if}}";

        Assert.Equal("git push --force v1",
            ActionTemplate.Render(template, Values(("push", "true"), ("force", "true"), ("name", "v1"))));
        Assert.Equal("git push v1",
            ActionTemplate.Render(template, Values(("push", "true"), ("force", ""), ("name", "v1"))));
        Assert.Equal("",
            ActionTemplate.Render(template, Values(("push", ""), ("force", "true"), ("name", "v1"))));
    }

    [Fact]
    public void UnlessMayNestInsideIf()
    {
        const string template = "{{#if run}}npm {{#unless ci}}install{{/unless}}{{#if ci}}ci{{/if}}{{/if}}";

        Assert.Equal("npm ci", ActionTemplate.Render(template, Values(("run", "true"), ("ci", "true"))));
        Assert.Equal("npm install", ActionTemplate.Render(template, Values(("run", "true"), ("ci", ""))));
    }

    [Fact]
    public void UnterminatedConditionalTagIsKeptVerbatim()
    {
        Assert.Equal("git tag {{/if}}", ActionTemplate.Render("git tag {{/if}}", Values()));
    }
}

public class ActionScriptCompilerTests
{
    private static readonly IReadOnlyDictionary<string, ActionDefinition> Definitions =
        BuiltInActions.All.ToDictionary(action => action.Id, StringComparer.Ordinal);

    [Fact]
    public void PlanCarriesRunConditionAndRetries()
    {
        var steps = new List<ActionStep>
        {
            new()
            {
                Id = "a", ActionId = "shell.echo", Parameters = new Dictionary<string, string?> { ["message"] = "hi" }
            },
            new()
            {
                Id = "b", ActionId = "shell.echo", RunWhen = "always", Retries = 99,
                Parameters = new Dictionary<string, string?> { ["message"] = "bye" }
            }
        };

        var plan = ActionScriptCompiler.Plan(steps, Definitions);
        Assert.True(plan.IsValid);
        Assert.Equal(StepRunCondition.Success, plan.Steps[0].RunWhen);
        Assert.Equal(StepRunCondition.Always, plan.Steps[1].RunWhen);
        Assert.Equal(BuildStepPlan.MaximumRetries, plan.Steps[1].Retries);

        var compiled = ActionScriptCompiler.Compile(steps, Definitions);
        Assert.Contains("# 執行條件：always", compiled.Script);
        Assert.Contains("# 失敗後重試 10 次", compiled.Script);
    }

    private static ActionStep Step(string actionId, params (string Key, string? Value)[] parameters)
    {
        return new ActionStep
        {
            Id = Guid.NewGuid().ToString("N"),
            ActionId = actionId,
            Parameters = parameters.ToDictionary(pair => pair.Key, pair => pair.Value)
        };
    }

    [Fact]
    public void SourceActionFallsBackToTheJobRepositoryAndBranch()
    {
        var result = ActionScriptCompiler.Compile([Step("git.source")], Definitions);

        Assert.True(result.IsValid);
        Assert.Contains("WALIS_SOURCE_REPOSITORY=\"$WALIS_REPOSITORY\"", result.Script);
        Assert.Contains("WALIS_SOURCE_BRANCH=\"${WALIS_BRANCH:-main}\"", result.Script);
        Assert.Contains("WALIS_SOURCE_DIRECTORY=.", result.Script);
    }

    [Fact]
    public void SourceActionInitialisesOnceAndThenUpdatesInPlace()
    {
        var result = ActionScriptCompiler.Compile([Step("git.source", ("depth", "1"))], Definitions);

        Assert.Contains("git init -q", result.Script);
        Assert.Contains("fetch --prune --depth 1 origin", result.Script);
        Assert.Contains("checkout --force -B \"$WALIS_SOURCE_BRANCH\" FETCH_HEAD", result.Script);
        Assert.DoesNotContain("git clone", result.Script);
    }

    [Fact]
    public void GeneratedScriptHasHeaderAndFailFast()
    {
        var result = ActionScriptCompiler.Compile([Step("shell.echo", ("message", "hi"))], Definitions);

        Assert.True(result.IsValid);
        Assert.Contains(ActionScriptCompiler.GeneratedHeader, result.Script);
        Assert.Contains("set -e", result.Script);
        Assert.Contains("echo 'hi'", result.Script);
    }

    [Fact]
    public void EveryStepGetsANumberedComment()
    {
        var result = ActionScriptCompiler.Compile(
            [Step("shell.echo", ("message", "one")), Step("shell.echo", ("message", "two"))],
            Definitions);

        Assert.Contains("# 1. 輸出訊息", result.Script);
        Assert.Contains("# 2. 輸出訊息", result.Script);
    }

    [Fact]
    public void DisabledStepsDoNotAppearInScript()
    {
        var disabled = new ActionStep
        {
            Id = "1",
            ActionId = "shell.echo",
            Enabled = false,
            Parameters = new Dictionary<string, string?> { ["message"] = "skipped" }
        };

        var result = ActionScriptCompiler.Compile([disabled], Definitions);

        Assert.DoesNotContain("skipped", result.Script);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ContinueOnErrorStepsAreWrappedInSubshell()
    {
        var step = new ActionStep
        {
            Id = "1",
            ActionId = "shell.echo",
            ContinueOnError = true,
            Parameters = new Dictionary<string, string?> { ["message"] = "maybe" }
        };

        var result = ActionScriptCompiler.Compile([step], Definitions);

        Assert.Contains("(", result.Script);
        Assert.Contains(") || true", result.Script);
    }

    [Fact]
    public void MissingRequiredFieldReportsError()
    {
        var result = ActionScriptCompiler.Compile([Step("shell.echo")], Definitions);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("訊息"));
    }

    [Fact]
    public void UnknownActionReportsError()
    {
        var result = ActionScriptCompiler.Compile([Step("nobody.knows")], Definitions);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("nobody.knows"));
    }

    [Fact]
    public void UnsetParametersUseDefaults()
    {
        var result = ActionScriptCompiler.Compile([Step("dotnet.build")], Definitions);

        Assert.Contains("--configuration Release", result.Script);
    }

    [Fact]
    public void CustomNameAppearsInComment()
    {
        var step = new ActionStep
        {
            Id = "1",
            ActionId = "shell.echo",
            Name = "宣告開始",
            Parameters = new Dictionary<string, string?> { ["message"] = "go" }
        };

        Assert.Contains("# 1. 宣告開始", ActionScriptCompiler.Compile([step], Definitions).Script);
    }

    [Fact]
    public void SshActionIncludesPortAndPrivateKey()
    {
        var result = ActionScriptCompiler.Compile(
            [
                Step(
                    "deploy.ssh",
                    ("host", "deploy@stg-01"),
                    ("command", "./restart.sh"),
                    ("port", "2222"),
                    ("identity", "/opt/keys/deploy"))
            ],
            Definitions);

        Assert.True(result.IsValid);
        Assert.Contains(
            "ssh -o BatchMode=yes -p 2222 -i '/opt/keys/deploy' 'deploy@stg-01' './restart.sh'",
            result.Script);
    }

    [Fact]
    public void SshActionKeepsOnlyRequiredParametersWhenOptionalOnesUnset()
    {
        var result = ActionScriptCompiler.Compile(
            [Step("deploy.ssh", ("host", "deploy@stg-01"), ("command", "uptime"), ("batchMode", "false"))],
            Definitions);

        Assert.True(result.IsValid);
        Assert.Contains("ssh 'deploy@stg-01' 'uptime'", result.Script);
    }

    [Fact]
    public void SbomGenerateActionCompilesExpectedTemplate()
    {
        var result = ActionScriptCompiler.Compile(
            [Step("sbom.generate", ("path", "src"), ("format", "cyclonedx-json"), ("output", "dist/bom.cdx.json"), ("tool", "syft"))],
            Definitions);

        Assert.True(result.IsValid);
        Assert.Contains("TARGET='src'", result.Script);
        Assert.Contains("FMT='cyclonedx-json'", result.Script);
        Assert.Contains("OUT_FILE='dist/bom.cdx.json'", result.Script);
        Assert.Contains("TOOL='syft'", result.Script);
        Assert.Contains("syft \"$TARGET\" -o \"$FMT\" > \"$OUT_FILE\"", result.Script);
    }

    [Fact]
    public void SbomScanActionCompilesExpectedTemplate()
    {
        var result = ActionScriptCompiler.Compile(
            [Step("sbom.scan", ("file", "dist/bom.cdx.json"), ("tool", "grype"), ("failOn", "critical"))],
            Definitions);

        Assert.True(result.IsValid);
        Assert.Contains("FILE='dist/bom.cdx.json'", result.Script);
        Assert.Contains("TOOL='grype'", result.Script);
        Assert.Contains("FAIL_ON='critical'", result.Script);
        Assert.Contains("grype sbom:\"$FILE\"", result.Script);
    }

    [Fact]
    public void SbomGenerateDeclaresExpectedOutputs()
    {
        var action = BuiltInActions.All.FirstOrDefault(a => a.Id == "sbom.generate");
        Assert.NotNull(action);
        Assert.Equal(ActionCategory.Security, action.Category);
        Assert.Contains(action.Outputs, o => o.Name == "path");
        Assert.Contains(action.Outputs, o => o.Name == "format");
    }

    [Fact]
    public void BuiltInTemplatesUseOnlyDeclaredParameters()
    {
        foreach (var action in BuiltInActions.All)
        {
            var declared = action.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var placeholder in ActionManifest.Placeholders(action.Template))
                Assert.True(declared.Contains(placeholder), $"{action.Id} 的樣板用到未宣告的參數 {placeholder}");
        }
    }

    [Fact]
    public void BuiltInActionIdsAreUnique()
    {
        Assert.Equal(BuiltInActions.All.Count, BuiltInActions.All.Select(action => action.Id).Distinct().Count());
    }
}

public class ActionManifestTests
{
    private const string Valid = """
                                 {
                                   "id": "team.deploy",
                                   "name": "團隊部署",
                                   "description": "呼叫內部部署腳本。",
                                   "category": "deploy",
                                   "parameters": [
                                     { "name": "target", "label": "目標", "type": "text", "required": true }
                                   ],
                                   "template": "./deploy.sh {{target}}"
                                 }
                                 """;

    [Fact]
    public void ValidDefinitionParses()
    {
        Assert.True(ActionManifest.TryParse(Valid, out var definition, out var errors));
        Assert.Empty(errors);
        Assert.Equal("team.deploy", definition!.Id);
        Assert.False(definition.IsBuiltIn);
        Assert.Single(definition.Parameters);
    }

    [Fact]
    public void MalformedIdIsRejected()
    {
        var json = Valid.Replace("team.deploy", "Deploy");

        Assert.False(ActionManifest.TryParse(json, out _, out var errors));
        Assert.Contains(errors, error => error.Contains("id"));
    }

    [Fact]
    public void IdCollidingWithBuiltInIsRejected()
    {
        var json = Valid.Replace("team.deploy", "shell.run");

        Assert.False(ActionManifest.TryParse(json, out _, out var errors));
        Assert.Contains(errors, error => error.Contains("內建"));
    }

    [Fact]
    public void TemplateUsingUndeclaredParameterIsRejected()
    {
        var json = Valid.Replace("{{target}}", "{{missing}}");

        Assert.False(ActionManifest.TryParse(json, out _, out var errors));
        Assert.Contains(errors, error => error.Contains("missing"));
    }

    [Fact]
    public void SelectWithoutOptionsIsRejected()
    {
        var json = Valid.Replace("\"type\": \"text\"", "\"type\": \"select\"");

        Assert.False(ActionManifest.TryParse(json, out _, out var errors));
        Assert.Contains(errors, error => error.Contains("options"));
    }

    [Fact]
    public void NonJsonReportsParseError()
    {
        Assert.False(ActionManifest.TryParse("這不是 JSON", out _, out var errors));
        Assert.Contains(errors, error => error.Contains("解析"));
    }

    [Fact]
    public void DefinitionSizeParametersAndOptionsHaveLimits()
    {
        var huge = "{\"id\":\"team.huge\",\"name\":\"Huge\",\"template\":\"" +
                   new string('x', ActionManifest.MaximumManifestLength) + "\"}";
        Assert.False(ActionManifest.TryParse(huge, out _, out var sizeErrors));
        Assert.Contains(sizeErrors, error => error.Contains(ActionManifest.MaximumManifestLength.ToString("N0")));

        var parameters = Enumerable.Range(0, ActionManifest.MaximumParameters + 1)
            .Select(index => new { name = $"P{index}", label = "P", type = "text" })
            .ToArray();
        var manyParameters = JsonSerializer.Serialize(new
        {
            id = "team.many",
            name = "Many",
            template = "echo ok",
            parameters
        });
        Assert.False(ActionManifest.TryParse(manyParameters, out _, out var parameterErrors));
        Assert.Contains(parameterErrors, error => error.Contains(ActionManifest.MaximumParameters.ToString()));
    }
}

public class ActionApiTests
{
    private const string Manifest = """
                                    {
                                      "id": "team.notify",
                                      "name": "團隊通知",
                                      "category": "deploy",
                                      "parameters": [{ "name": "text", "label": "訊息", "type": "text", "required": true }],
                                      "template": "./notify.sh {{text}}"
                                    }
                                    """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ActionListIncludesBuiltIns()
    {
        await using var factory = new WalisApiFactory();
        var payload = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/actions");

        var ids = payload.GetProperty("definitions").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToList();

        Assert.Contains("shell.run", ids);
        Assert.Contains("dotnet.build", ids);
        Assert.True(payload.GetProperty("categories").GetArrayLength() > 0);
    }

    [Fact]
    public async Task CanPreviewCompiledFlowScript()
    {
        await using var factory = new WalisApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/actions/preview", new
        {
            actions = new[]
            {
                new { id = "1", actionId = "shell.echo", enabled = true, parameters = new { message = "hi" } }
            }
        });

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Contains("echo 'hi'", payload.GetProperty("script").GetString());
        Assert.Equal(0, payload.GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public async Task InstallAndRemoveCustomAction()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();

        var install = await client.PostAsJsonAsync("/api/actions/install", new { manifest = Manifest });
        install.EnsureSuccessStatusCode();

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/actions");
        Assert.Contains(
            listed.GetProperty("definitions").EnumerateArray().ToList(),
            item => item.GetProperty("id").GetString() == "team.notify");

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/actions/team.notify")).StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/actions");
        Assert.DoesNotContain(
            after.GetProperty("definitions").EnumerateArray().ToList(),
            item => item.GetProperty("id").GetString() == "team.notify");
    }

    [Fact]
    public async Task BuiltInActionsCannotBeRemoved()
    {
        await using var factory = new WalisApiFactory();

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await factory.CreateClient().DeleteAsync("/api/actions/shell.run")).StatusCode);
    }

    [Fact]
    public async Task InstallReportsErrorsForInvalidDefinition()
    {
        await using var factory = new WalisApiFactory();

        var response = await factory.CreateClient()
            .PostAsJsonAsync("/api/actions/install", new { manifest = Manifest.Replace("{{text}}", "{{unknown}}") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FlowModeJobCompilesActionsIntoCommand()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"流程測試 {Guid.NewGuid():N}",
            triggerKind = "manual",
            timeoutMinutes = 5,
            scriptMode = "actions",
            actions = new object[]
            {
                new { id = "1", actionId = "shell.echo", enabled = true, parameters = new { message = "開始" } },
                new { id = "2", actionId = "node.install", enabled = true, parameters = new { useCi = "true" } }
            }
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var job = await created.Content.ReadFromJsonAsync<Job>(Json);

        Assert.Equal("actions", job!.ScriptMode);
        Assert.Contains("echo '開始'", job.Command);
        Assert.Contains("npm ci", job.Command);
        Assert.NotNull(job.ActionsJson);
    }

    [Fact]
    public async Task FlowModeRejectsMissingRequiredField()
    {
        await using var factory = new WalisApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/jobs", new
        {
            name = "流程錯誤",
            triggerKind = "manual",
            timeoutMinutes = 5,
            scriptMode = "actions",
            actions = new object[]
            {
                new { id = "1", actionId = "shell.echo", enabled = true, parameters = new { } }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FlowModeRejectsEmptyFlow()
    {
        await using var factory = new WalisApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/jobs", new
        {
            name = "空流程",
            triggerKind = "manual",
            timeoutMinutes = 5,
            scriptMode = "actions",
            actions = Array.Empty<object>()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}