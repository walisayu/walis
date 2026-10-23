using System.Text.Json;
using System.Text.RegularExpressions;
using Walis.Core.Actions;
using Walis.Core.Models;

namespace Walis.Core.Jenkins;

/// <summary>
///     Converts a Jenkinsfile (declarative or scripted) into a Walis job draft: stage -> step, parallel -> parallel steps,
///     post -> run condition, parameters -> build parameters, environment/credentials -> environment variables,
///     triggers -> trigger kind, options -> timeout/retry/no concurrency, build job -> downstream jobs.
///     Anything without a mapping is left as a comment with a warning; a piece of Groovy logic never makes the whole file give up.
/// </summary>
public sealed partial class JenkinsfileConverter
{
    private const string ShellAction = "shell.run";
    private const string ContainerAction = "container.run";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly List<string> _artifacts = [];
    private readonly List<DownstreamTarget> _downstream = [];
    private readonly List<JenkinsEnvironmentVariable> _environment = [];
    private readonly JenkinsJobProposal _job = new();
    private readonly List<BuildParameterDefinition> _parameters = [];
    private readonly HashSet<string> _seenWarnings = new(StringComparer.Ordinal);

    private readonly List<string> _stageExportsPrefix = [];
    private readonly List<ImportedStep> _steps = [];

    private readonly List<JenkinsWarning> _warnings = [];
    private int _defaultRetries;

    private DockerAgent? _pipelineDocker;
    private bool _skipDefaultCheckout;

    private JenkinsfileConverter(string? preferredName)
    {
        _job.Name = preferredName?.Trim() ?? string.Empty;
    }

    public static JenkinsImportResult Convert(string source, string? preferredName)
    {
        var converter = new JenkinsfileConverter(preferredName);
        var nodes = GroovyParser.Parse(source);
        var format = converter.Run(nodes);
        return converter.Finish(format);
    }

    private string Run(IReadOnlyList<GroovyNode> nodes)
    {
        var pipeline = nodes.OfType<GroovyCall>()
            .FirstOrDefault(call => call.Name == "pipeline" && call.Body is not null);
        if (pipeline is not null)
        {
            foreach (var stray in nodes.Where(node => !ReferenceEquals(node, pipeline)))
                if (stray is GroovyRaw raw)
                    Warn(JenkinsWarningSeverity.Unsupported, "pipeline 區塊外的 Groovy 程式碼已略過。", raw.Line, raw.Text);

            ConvertDeclarative(pipeline);
            return "declarative";
        }

        ConvertScripted(nodes);
        return "scripted";
    }

    private JenkinsImportResult Finish(string format)
    {
        foreach (var step in _steps) _job.Steps.Add(step.ToActionStep());

        if (_job.Steps.Count == 0) Warn(JenkinsWarningSeverity.Warning, "沒有找到任何可以執行的步驟，請檢查貼上的內容是不是 Jenkinsfile。");

        if (_artifacts.Count != 0) _job.ArtifactPath = string.Join('\n', _artifacts.Distinct(StringComparer.Ordinal));

        if (_parameters.Count != 0) _job.ParametersJson = BuildParameters.Serialize(_parameters);

        if (_downstream.Count != 0) _job.DownstreamJson = JsonSerializer.Serialize(_downstream, JsonOptions);

        return new JenkinsImportResult(format, _job, _environment, _warnings);
    }

    // ---------------------------------------------------------------- declarative

    private void ConvertDeclarative(GroovyCall pipeline)
    {
        var body = pipeline.Body!;

        // Read the configuration blocks first, stages last, post at the end.
        foreach (var node in body)
        {
            if (node is not GroovyCall call)
            {
                if (node is GroovyRaw raw)
                    Warn(JenkinsWarningSeverity.Unsupported, "pipeline 裡有看不懂的語句，已略過。", raw.Line, raw.Text);

                continue;
            }

            switch (call.Name)
            {
                case "agent":
                    _pipelineDocker = ConvertAgent(call, true);
                    break;
                case "environment":
                    ConvertEnvironmentBlock(call, _environment, null);
                    break;
                case "options":
                    ConvertOptions(call, true, out _, out _);
                    break;
                case "parameters":
                    ConvertParameters(call.Body ?? []);
                    break;
                case "triggers":
                    ConvertTriggers(call.Body ?? []);
                    break;
                case "tools":
                    ConvertTools(call);
                    break;
                case "stages":
                case "post":
                    break;
                case "libraries":
                    Warn(JenkinsWarningSeverity.Unsupported, "共用函式庫（libraries）不支援；用到的自訂步驟會變成註解。", call.Line);
                    break;
                default:
                    Warn(JenkinsWarningSeverity.Unsupported, $"pipeline 的「{call.Name}」區塊不支援，已略過。", call.Line);
                    break;
            }
        }

        var stages = body.OfType<GroovyCall>().FirstOrDefault(call => call.Name == "stages");
        if (stages?.Body is not null)
            ConvertStageList(stages.Body, null, _pipelineDocker);
        else
            Warn(JenkinsWarningSeverity.Warning, "pipeline 裡沒有 stages 區塊。", pipeline.Line);

        var post = body.OfType<GroovyCall>().FirstOrDefault(call => call.Name == "post");
        if (post?.Body is not null) ConvertPost(post.Body, "post", _pipelineDocker);
    }

    private DockerAgent? ConvertAgent(GroovyCall agent, bool pipelineLevel)
    {
        if (agent.First is GroovyIdentifier { Name: "any" or "none" }) return null;

        if (agent.Body is null)
        {
            Warn(JenkinsWarningSeverity.Info, "agent 設定已略過。", agent.Line);
            return null;
        }

        foreach (var node in agent.Body.OfType<GroovyCall>())
            switch (node.Name)
            {
                case "label":
                case "node":
                {
                    var label = node.Name == "label"
                        ? StringOf(node.First)
                        : StringOf(
                            node.Body?.OfType<GroovyCall>().FirstOrDefault(inner => inner.Name == "label")?.First);
                    if (label is null) break;

                    if (pipelineLevel)
                    {
                        _job.RunnerName = label;
                        Warn(JenkinsWarningSeverity.Warning, $"agent label「{label}」已對應成執行器名稱；Walis 沒有這個執行器的話請改成 local。",
                            node.Line);
                    }
                    else
                    {
                        Warn(JenkinsWarningSeverity.Info, $"stage 層級的 agent label「{label}」沒有對應，整個任務用同一個執行器。",
                            node.Line);
                    }

                    break;
                }

                case "docker":
                {
                    var image = StringOf(node.First) ??
                                StringOf(node.Body?.OfType<GroovyCall>().FirstOrDefault(inner => inner.Name == "image")
                                    ?.First);
                    var args = StringOf(node.Body?.OfType<GroovyCall>().FirstOrDefault(inner => inner.Name == "args")
                        ?.First);
                    if (image is null)
                    {
                        Warn(JenkinsWarningSeverity.Unsupported, "docker agent 沒有指定映像，已略過。", node.Line);
                        break;
                    }

                    Warn(JenkinsWarningSeverity.Info, $"docker agent「{image}」對應成「在容器中執行」動作，工作目錄會掛進容器。", node.Line);
                    return new DockerAgent(image, args);
                }

                case "dockerfile":
                    Warn(JenkinsWarningSeverity.Unsupported, "agent dockerfile 不支援；請先建好映像再用「在容器中執行」。", node.Line);
                    break;
                case "kubernetes":
                    Warn(JenkinsWarningSeverity.Unsupported, "agent kubernetes 不支援；步驟會直接在執行器上跑。", node.Line);
                    break;
                default:
                    Warn(JenkinsWarningSeverity.Unsupported, $"agent 的「{node.Name}」不支援。", node.Line);
                    break;
            }

        return null;
    }

    private void ConvertEnvironmentBlock(GroovyCall environment, List<JenkinsEnvironmentVariable> target,
        List<string>? exportsForStage)
    {
        foreach (var node in environment.Body ?? [])
        {
            if (node is not GroovyAssignment assignment)
            {
                if (node is GroovyRaw raw)
                    Warn(JenkinsWarningSeverity.Unsupported, "environment 裡有看不懂的設定，已略過。", raw.Line, raw.Text);

                continue;
            }

            var name = assignment.Target.StartsWith("env.", StringComparison.Ordinal)
                ? assignment.Target[4..]
                : assignment.Target;

            if (assignment.Value is GroovyCallValue { Call.Name: "credentials" } credential)
            {
                var id = StringOf(credential.Call.First) ?? "?";
                if (exportsForStage is null)
                    target.Add(new JenkinsEnvironmentVariable
                        { Name = name, Value = string.Empty, IsSecret = true, Source = $"Jenkins 憑證 {id}" });
                else
                    _environment.Add(new JenkinsEnvironmentVariable
                        { Name = name, Value = string.Empty, IsSecret = true, Source = $"Jenkins 憑證 {id}" });

                Warn(JenkinsWarningSeverity.Warning, $"環境變數 {name} 來自 Jenkins 憑證「{id}」，匯入後請到環境變數頁填入機密值。",
                    assignment.Line);
                continue;
            }

            var value = ValueToShellLiteral(assignment.Value, assignment.Line, out var literal);

            if (exportsForStage is not null)
            {
                exportsForStage.Add(literal ? $"export {name}={ShellQuote(value)}" : $"export {name}=\"{value}\"");
            }
            else if (literal)
            {
                target.Add(new JenkinsEnvironmentVariable
                    { Name = name, Value = value, IsSecret = false, Source = "environment 區塊" });
            }
            else
            {
                // Values referencing other variables ("${env.X}-suffix"): the environment page does not expand them, so emit an export before the step instead.
                Warn(JenkinsWarningSeverity.Info, $"環境變數 {name} 的值參照了其他變數，改成每個步驟開頭的 export。", assignment.Line);
                _stageExportsPrefix.Add($"export {name}=\"{value}\"");
            }
        }
    }

    private void ConvertOptions(GroovyCall options, bool pipelineLevel, out int retries, out bool hasTimeout)
    {
        retries = 0;
        hasTimeout = false;

        foreach (var node in options.Body ?? [])
        {
            if (node is not GroovyCall call) continue;

            switch (call.Name)
            {
                case "timeout":
                {
                    var minutes = TimeoutMinutes(call);
                    if (minutes is null) break;

                    if (pipelineLevel)
                    {
                        _job.TimeoutMinutes = minutes.Value;
                    }
                    else
                    {
                        hasTimeout = true;
                        Warn(JenkinsWarningSeverity.Info, $"stage 層級的 timeout（{minutes} 分鐘）沒有對應，Walis 只有整場建置的逾時。",
                            call.Line);
                    }

                    break;
                }

                case "retry":
                {
                    var count = IntOf(call.First) ?? 0;
                    if (pipelineLevel)
                    {
                        _defaultRetries = Math.Clamp(count, 0, BuildStepPlan.MaximumRetries);
                        Warn(JenkinsWarningSeverity.Info, $"pipeline 層級的 retry({count}) 套用到每個步驟。", call.Line);
                    }
                    else
                    {
                        retries = Math.Clamp(count, 0, BuildStepPlan.MaximumRetries);
                    }

                    break;
                }

                case "disableConcurrentBuilds":
                    _job.AllowConcurrentBuilds = false;
                    break;
                case "skipDefaultCheckout":
                    _skipDefaultCheckout = true;
                    break;
                case "buildDiscarder":
                    Warn(JenkinsWarningSeverity.Info, "buildDiscarder 沒有對應；建置紀錄的保留天數在系統設定。", call.Line);
                    break;
                case "timestamps":
                case "ansiColor":
                case "disableResume":
                case "quietPeriod":
                case "parallelsAlwaysFailFast":
                case "skipStagesAfterUnstable":
                case "durabilityHint":
                case "checkoutToSubdirectory":
                case "newContainerPerStage":
                    break;
                case "lock":
                    Warn(JenkinsWarningSeverity.Info, "lock 沒有對應；需要互斥的話可以關閉「同一時間只跑一筆」以外的任務併發。", call.Line);
                    break;
                default:
                    Warn(JenkinsWarningSeverity.Unsupported, $"options 的「{call.Name}」不支援，已略過。", call.Line);
                    break;
            }
        }
    }

    private static int? TimeoutMinutes(GroovyCall timeout)
    {
        var amount = IntOf(timeout.Named("time")) ?? IntOf(timeout.First);
        if (amount is null) return null;

        var unit = (StringOf(timeout.Named("unit")) ?? "MINUTES").ToUpperInvariant();
        var minutes = unit switch
        {
            "SECONDS" => (int)Math.Ceiling(amount.Value / 60.0),
            "HOURS" => amount.Value * 60,
            "DAYS" => amount.Value * 1440,
            "MILLISECONDS" => (int)Math.Ceiling(amount.Value / 60000.0),
            _ => amount.Value
        };

        return Math.Clamp(minutes, 1, 1440);
    }

    private void ConvertParameters(IReadOnlyList<GroovyNode> nodes)
    {
        foreach (var node in nodes)
            if (node is GroovyCall call)
                ConvertParameter(call);
            else if (node is GroovyRaw raw)
                Warn(JenkinsWarningSeverity.Unsupported, "parameters 裡有看不懂的宣告，已略過。", raw.Line, raw.Text);
    }

    private void ConvertParameter(GroovyCall call)
    {
        var name = StringOf(call.Named("name"));
        if (name is null)
        {
            Warn(JenkinsWarningSeverity.Unsupported, $"參數「{call.Name}」沒有名稱，已略過。", call.Line);
            return;
        }

        if (!ParameterName().IsMatch(name) || name.StartsWith("WALIS_", StringComparison.OrdinalIgnoreCase))
        {
            Warn(JenkinsWarningSeverity.Unsupported, $"參數名稱「{name}」不符合 Walis 的規則（英數與底線、不可 WALIS_ 開頭），已略過。", call.Line);
            return;
        }

        var description = StringOf(call.Named("description"));
        var defaultValue = StringOf(call.Named("defaultValue"));

        switch (call.Name)
        {
            case "string":
                AddParameter(name, BuildParameterTypes.String, defaultValue, description, null);
                break;
            case "text":
                AddParameter(name, BuildParameterTypes.Text, defaultValue, description, null);
                break;
            case "booleanParam":
                AddParameter(name, BuildParameterTypes.Boolean,
                    string.Equals(defaultValue, "true", StringComparison.OrdinalIgnoreCase) ||
                    call.Named("defaultValue") is GroovyIdentifier { Name: "true" }
                        ? "true"
                        : "false", description, null);
                break;
            case "password":
                AddParameter(name, BuildParameterTypes.Password, null, description, null);
                if (!string.IsNullOrEmpty(defaultValue))
                    Warn(JenkinsWarningSeverity.Warning, $"密碼參數 {name} 的預設值不會被匯入，請在觸發時輸入。", call.Line);

                break;
            case "choice":
            {
                var choices = call.Named("choices") switch
                {
                    GroovyList list => list.Items.Select(item => StringOf(item) ?? string.Empty).ToList(),
                    GroovyString text => text.Text.Split('\n').Select(line => line.Trim())
                        .Where(line => line.Length > 0).ToList(),
                    _ => []
                };

                if (choices.Count == 0)
                {
                    Warn(JenkinsWarningSeverity.Unsupported, $"選項參數 {name} 沒有選項，已略過。", call.Line);
                    break;
                }

                AddParameter(name, BuildParameterTypes.Choice, choices[0], description, choices);
                break;
            }

            case "credentials":
                Warn(JenkinsWarningSeverity.Unsupported, $"憑證參數 {name} 不支援；請改用機密環境變數。", call.Line);
                break;
            case "file":
                Warn(JenkinsWarningSeverity.Unsupported, $"檔案參數 {name} 不支援。", call.Line);
                break;
            default:
                Warn(JenkinsWarningSeverity.Unsupported, $"參數型別「{call.Name}」不支援（{name}），已略過。", call.Line);
                break;
        }
    }

    private void AddParameter(string name, string type, string? defaultValue, string? description,
        List<string>? options)
    {
        if (_parameters.Any(parameter => parameter.Name == name)) return;

        _parameters.Add(new BuildParameterDefinition
        {
            Name = name,
            Label = name,
            Type = type,
            Default = defaultValue,
            Description = description,
            Options = options ?? []
        });
    }

    private void ConvertTriggers(IReadOnlyList<GroovyNode> nodes)
    {
        foreach (var node in nodes.OfType<GroovyCall>())
            switch (node.Name)
            {
                case "cron":
                {
                    var spec = StringOf(node.First) ?? StringOf(node.Named("spec"));
                    if (spec is null) break;

                    var cron = JenkinsCron.Convert(spec, _job.Name.Length == 0 ? spec : _job.Name, _warnings);
                    if (cron is not null)
                    {
                        _job.TriggerKind = TriggerMode.Cron;
                        _job.Cron = cron;
                    }

                    break;
                }

                case "pollSCM":
                    _job.TriggerKind = TriggerMode.Push;
                    Warn(JenkinsWarningSeverity.Warning,
                        "pollSCM 對應成「推送後自動建置」，請到系統設定拿 webhook 網址設到 Git 伺服器，任務也要填儲存庫網址。", node.Line);
                    break;
                case "githubPush":
                case "bitbucketPush":
                case "gitlab":
                    _job.TriggerKind = TriggerMode.Push;
                    Warn(JenkinsWarningSeverity.Info, $"{node.Name} 對應成「推送後自動建置」，請設定 webhook 並填儲存庫網址。", node.Line);
                    break;
                case "upstream":
                {
                    var projects = StringOf(node.Named("upstreamProjects")) ?? StringOf(node.First) ?? string.Empty;
                    Warn(JenkinsWarningSeverity.Warning, $"upstream 觸發要反過來設定：請到任務「{projects}」的「完成後觸發下游任務」加上這個任務。",
                        node.Line);
                    break;
                }

                default:
                    Warn(JenkinsWarningSeverity.Unsupported, $"觸發方式「{node.Name}」不支援。", node.Line);
                    break;
            }
    }

    private void ConvertTools(GroovyCall tools)
    {
        foreach (var node in (tools.Body ?? []).OfType<GroovyCall>())
            Warn(JenkinsWarningSeverity.Warning,
                $"tools 的 {node.Name}「{StringOf(node.First)}」沒有對應；請確認執行器的 PATH 已有這個工具。", node.Line);
    }

    // ---------------------------------------------------------------- stages

    private void ConvertStageList(IReadOnlyList<GroovyNode> nodes, string? prefix, DockerAgent? docker,
        IReadOnlyList<string>? inherited = null, bool firstParallel = false)
    {
        // When a parallel branch is itself nested stages, the branch's first step must inherit "run concurrently with the previous step".
        var parallel = firstParallel;
        foreach (var node in nodes)
            if (node is GroovyCall { Name: "stage" } stage)
            {
                var before = _steps.Count;
                ConvertStage(stage, prefix, docker, parallel, inherited);
                if (_steps.Count > before) parallel = false;
            }
            else if (node is GroovyRaw raw)
            {
                Warn(JenkinsWarningSeverity.Unsupported, "stages 裡有看不懂的語句，已略過。", raw.Line, raw.Text);
            }
    }

    /// <param name="inherited">The parent stage's environment exports and when guards, carried into child stages (parallel/nested/matrix).</param>
    private void ConvertStage(GroovyCall stage, string? prefix, DockerAgent? docker, bool parallel,
        IReadOnlyList<string>? inherited = null)
    {
        var name = StringOf(stage.First) ?? StringOf(stage.Named("name")) ?? "stage";
        var fullName = prefix is null ? name : $"{prefix} / {name}";
        var body = stage.Body ?? [];

        var stageDocker = docker;
        var exports = new List<string>(inherited ?? []);
        var guards = new List<string>();
        var retries = _defaultRetries;

        foreach (var node in body.OfType<GroovyCall>())
            switch (node.Name)
            {
                case "agent":
                    stageDocker = ConvertAgent(node, false) ?? docker;
                    break;
                case "environment":
                    ConvertEnvironmentBlock(node, _environment, exports);
                    break;
                case "when":
                    guards.AddRange(ConvertWhen(node, fullName));
                    break;
                case "options":
                    ConvertOptions(node, false, out var stageRetries, out _);
                    if (stageRetries > 0) retries = stageRetries;

                    break;
                case "tools":
                    ConvertTools(node);
                    break;
                case "input":
                    Warn(JenkinsWarningSeverity.Unsupported,
                        $"stage「{fullName}」的 input（人工核准）不支援；Walis 沒有暫停等人的機制，可以拆成兩個任務由人手觸發第二個。", node.Line);
                    break;
                case "steps":
                case "parallel":
                case "stages":
                case "matrix":
                case "post":
                case "failFast":
                    break;
                default:
                    Warn(JenkinsWarningSeverity.Unsupported, $"stage「{fullName}」的「{node.Name}」不支援。", node.Line);
                    break;
            }

        var matrix = body.OfType<GroovyCall>().FirstOrDefault(call => call.Name == "matrix");
        if (matrix?.Body is not null)
        {
            ConvertMatrix(matrix, fullName);
            var matrixStages = matrix.Body.OfType<GroovyCall>().FirstOrDefault(call => call.Name == "stages");
            if (matrixStages?.Body is not null)
                ConvertStageList(matrixStages.Body, fullName, stageDocker, [.. exports, .. guards], parallel);

            return;
        }

        var nested = body.OfType<GroovyCall>().FirstOrDefault(call => call.Name == "stages");
        if (nested?.Body is not null)
        {
            ConvertStageList(nested.Body, fullName, stageDocker, [.. exports, .. guards], parallel);
            ConvertStagePost(body, fullName, stageDocker);
            return;
        }

        var parallelBlock = body.OfType<GroovyCall>().FirstOrDefault(call => call.Name == "parallel");
        if (parallelBlock?.Body is not null)
        {
            var first = true;
            foreach (var child in parallelBlock.Body.OfType<GroovyCall>().Where(call => call.Name == "stage"))
            {
                var before = _steps.Count;
                ConvertStage(child, fullName, stageDocker, !first, [.. exports, .. guards]);
                if (_steps.Count > before) first = false;
            }

            ConvertStagePost(body, fullName, stageDocker);
            return;
        }

        var steps = body.OfType<GroovyCall>().FirstOrDefault(call => call.Name == "steps");
        if (steps?.Body is not null)
        {
            var script = new ScriptBuilder();
            var context = new StepContext(fullName, script, retries);
            ConvertSteps(steps.Body, context, true);

            var lines = new List<string>();
            lines.AddRange(_stageExportsPrefix);
            lines.AddRange(exports);
            lines.AddRange(guards);
            lines.AddRange(script.Lines);

            if (lines.Count != 0 || context.Retries != retries)
                _steps.Add(new ImportedStep(fullName, lines, StepRunCondition.Success, parallel, context.Retries,
                    stageDocker));
        }
        else if (parallelBlock is null && nested is null)
        {
            Warn(JenkinsWarningSeverity.Warning, $"stage「{fullName}」沒有 steps，已略過。", stage.Line);
        }

        ConvertStagePost(body, fullName, stageDocker);
    }

    private void ConvertStagePost(IReadOnlyList<GroovyNode> body, string fullName, DockerAgent? docker)
    {
        var post = body.OfType<GroovyCall>().FirstOrDefault(call => call.Name == "post");
        if (post?.Body is not null) ConvertPost(post.Body, fullName, docker);
    }

    private void ConvertPost(IReadOnlyList<GroovyNode> nodes, string prefix, DockerAgent? docker)
    {
        foreach (var section in nodes.OfType<GroovyCall>())
        {
            var condition = section.Name switch
            {
                "always" or "cleanup" => StepRunCondition.Always,
                "success" => StepRunCondition.Success,
                "failure" or "unstable" or "aborted" or "regression" => StepRunCondition.Failure,
                "fixed" => StepRunCondition.Success,
                "changed" => StepRunCondition.Always,
                _ => null
            };

            if (condition is null)
            {
                Warn(JenkinsWarningSeverity.Unsupported, $"post 的「{section.Name}」不支援，已略過。", section.Line);
                continue;
            }

            if (section.Name is "unstable" or "aborted" or "regression" or "fixed" or "changed")
                Warn(JenkinsWarningSeverity.Info, $"post 的「{section.Name}」沒有完全對應，改成「{ConditionLabel(condition)}」。",
                    section.Line);

            var script = new ScriptBuilder();
            var context = new StepContext($"{prefix} / {section.Name}", script, 0);
            ConvertSteps(section.Body ?? [], context, true);

            if (script.Lines.Count != 0)
                _steps.Add(new ImportedStep($"{prefix} / {section.Name}", script.Lines, condition, false,
                    context.Retries, docker));
        }
    }

    private static string ConditionLabel(string condition)
    {
        return condition switch
        {
            StepRunCondition.Always => "總是執行",
            StepRunCondition.Failure => "只在前面失敗時執行",
            _ => "前面都成功才執行"
        };
    }

    private void ConvertMatrix(GroovyCall matrix, string stageName)
    {
        var axes = matrix.Body!.OfType<GroovyCall>().FirstOrDefault(call => call.Name == "axes");
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var axis in (axes?.Body ?? []).OfType<GroovyCall>().Where(call => call.Name == "axis"))
        {
            var name = StringOf(axis.Body?.OfType<GroovyCall>().FirstOrDefault(call => call.Name == "name")?.First);
            var values = axis.Body?.OfType<GroovyCall>().FirstOrDefault(call => call.Name == "values")?.Arguments
                .Select(argument => StringOf(argument.Value) ?? string.Empty).Where(value => value.Length > 0).ToList();
            if (name is not null && values is { Count: > 0 }) map[name] = values;
        }

        if (map.Count == 0)
        {
            Warn(JenkinsWarningSeverity.Unsupported, $"stage「{stageName}」的 matrix 沒有可用的 axes，已略過。", matrix.Line);
            return;
        }

        if (_job.MatrixJson is not null)
        {
            Warn(JenkinsWarningSeverity.Warning, $"stage「{stageName}」的 matrix 沒有匯入：Walis 一個任務只有一組矩陣，已被前面的 stage 用掉。",
                matrix.Line);
            return;
        }

        _job.MatrixJson = JsonSerializer.Serialize(map, JsonOptions);
        Warn(JenkinsWarningSeverity.Info, $"stage「{stageName}」的 matrix 對應成任務的矩陣，整個任務會為每個組合各跑一次。", matrix.Line);

        if (matrix.Body!.OfType<GroovyCall>().Any(call => call.Name == "excludes"))
            Warn(JenkinsWarningSeverity.Unsupported, "matrix 的 excludes 不支援，所有組合都會跑。", matrix.Line);
    }

    private List<string> ConvertWhen(GroovyCall when, string stageName)
    {
        var conditions = new List<string>();

        foreach (var node in (when.Body ?? []).OfType<GroovyCall>())
        {
            var condition = WhenCondition(node, stageName);
            if (condition is not null) conditions.Add(condition);
        }

        if (conditions.Count == 0) return [];

        return
        [
            $"if ! {{ {string.Join(" && ", conditions)}; }}; then",
            $"  echo {ShellQuote($"略過 {stageName}：when 條件不成立")}",
            "  exit 0",
            "fi"
        ];
    }

    private string? WhenCondition(GroovyCall node, string stageName)
    {
        switch (node.Name)
        {
            case "branch":
            {
                var pattern = StringOf(node.First) ?? StringOf(node.Named("pattern")) ?? "*";
                return pattern.Contains('*') || pattern.Contains('?')
                    ? $"[[ \"$WALIS_BRANCH\" == {pattern} ]]"
                    : $"[ \"$WALIS_BRANCH\" = {ShellQuote(pattern)} ]";
            }

            case "environment":
            {
                var name = StringOf(node.Named("name")) ?? "X";
                var value = StringOf(node.Named("value")) ?? string.Empty;
                return $"[ \"${name}\" = {ShellQuote(value)} ]";
            }

            case "equals":
            {
                var expected = StringOf(node.Named("expected")) ?? string.Empty;
                var actual = ValueToShellLiteral(node.Named("actual") ?? new GroovyString(string.Empty, false),
                    node.Line, out var literal);
                return $"[ \"{(literal ? actual : actual)}\" = {ShellQuote(expected)} ]";
            }

            case "not":
            {
                var inner = (node.Body ?? []).OfType<GroovyCall>().Select(child => WhenCondition(child, stageName))
                    .Where(child => child is not null).ToList();
                return inner.Count == 0 ? null : $"! {{ {string.Join(" && ", inner)}; }}";
            }

            case "allOf":
            {
                var inner = (node.Body ?? []).OfType<GroovyCall>().Select(child => WhenCondition(child, stageName))
                    .Where(child => child is not null).ToList();
                return inner.Count == 0 ? null : $"{{ {string.Join(" && ", inner)}; }}";
            }

            case "anyOf":
            {
                var inner = (node.Body ?? []).OfType<GroovyCall>().Select(child => WhenCondition(child, stageName))
                    .Where(child => child is not null).ToList();
                return inner.Count == 0 ? null : $"{{ {string.Join(" || ", inner)}; }}";
            }

            case "expression":
                Warn(JenkinsWarningSeverity.Unsupported,
                    $"stage「{stageName}」的 when expression 是 Groovy 邏輯，需要手動改成 shell 判斷。", node.Line, RawText(node));
                return null;
            case "beforeAgent":
            case "beforeInput":
            case "beforeOptions":
                return null;
            case "changeset":
            case "changelog":
            case "changeRequest":
            case "tag":
            case "buildingTag":
            case "triggeredBy":
                Warn(JenkinsWarningSeverity.Unsupported, $"stage「{stageName}」的 when {node.Name} 不支援，這個 stage 會一律執行。",
                    node.Line);
                return null;
            default:
                Warn(JenkinsWarningSeverity.Unsupported, $"stage「{stageName}」的 when {node.Name} 不支援。", node.Line);
                return null;
        }
    }

    private void ConvertSteps(IReadOnlyList<GroovyNode> nodes, StepContext context, bool topLevel)
    {
        // retry(n) { whole steps } -> the step's retry count; retry anywhere else can only be inlined.
        if (topLevel && nodes.Count == 1 && nodes[0] is GroovyCall { Name: "retry", Body: not null } retry)
        {
            context.Retries = Math.Clamp(IntOf(retry.First) ?? 0, 0, BuildStepPlan.MaximumRetries);
            ConvertSteps(retry.Body, context, false);
            return;
        }

        foreach (var node in nodes)
            switch (node)
            {
                case GroovyCall call:
                    ConvertStep(call, context);
                    break;
                case GroovyAssignment assignment:
                    ConvertAssignment(assignment, context);
                    break;
                case GroovyRaw raw:
                    Warn(JenkinsWarningSeverity.Unsupported, $"stage「{context.StageName}」有一段 Groovy 邏輯，已留成註解，請手動改寫。",
                        raw.Line, raw.Text);
                    context.Script.Comment("TODO Groovy：", raw.Text);
                    break;
            }
    }

    private void ConvertAssignment(GroovyAssignment assignment, StepContext context)
    {
        if (assignment.Target.StartsWith("env.", StringComparison.Ordinal))
        {
            var name = assignment.Target[4..];
            var value = ValueToShellLiteral(assignment.Value, assignment.Line, out var literal);
            context.Script.Line(literal ? $"export {name}={ShellQuote(value)}" : $"export {name}=\"{value}\"");
            context.Script.Line($"printf '%s=%s\\n' {ShellQuote(name)} \"${name}\" >> \"$WALIS_ENV_FILE\"");
            return;
        }

        if (assignment.Target.StartsWith("currentBuild.", StringComparison.Ordinal))
        {
            Warn(JenkinsWarningSeverity.Info, $"對 {assignment.Target} 的指派已略過；建置結果由步驟的結束碼決定。", assignment.Line);
            return;
        }

        Warn(JenkinsWarningSeverity.Unsupported,
            $"stage「{context.StageName}」的 Groovy 變數「{assignment.Target}」需要手動改成 shell 變數。", assignment.Line,
            RawText(assignment));
        context.Script.Comment("TODO Groovy 變數：", $"{assignment.Target} = {RawValueText(assignment.Value)}");
    }

    private void ConvertStep(GroovyCall call, StepContext context)
    {
        var script = context.Script;

        switch (call.ShortName)
        {
            case "sh":
            {
                var command = StringOf(call.First) ?? StringOf(call.Named("script"));
                if (command is null)
                {
                    Warn(JenkinsWarningSeverity.Unsupported, "sh 沒有腳本內容，已略過。", call.Line);
                    break;
                }

                if (call.Named("returnStdout") is not null || call.Named("returnStatus") is not null)
                    Warn(JenkinsWarningSeverity.Warning,
                        $"stage「{context.StageName}」的 sh 使用 returnStdout／returnStatus，回傳值無法交給 Groovy；請改用 shell 變數。",
                        call.Line);

                var interpolated = call.First is GroovyString { Interpolated: true } ||
                                   call.Named("script") is GroovyString { Interpolated: true };
                foreach (var line in Dedent(RewriteShell(command, interpolated, call.Line))) script.Line(line);

                break;
            }

            case "echo":
            {
                var message = call.First;
                if (message is GroovyString text)
                {
                    script.Line(text.Interpolated
                        ? $"echo \"{RewriteInterpolation(text.Text, call.Line).Replace("\"", "\\\"")}\""
                        : $"echo {ShellQuote(text.Text)}");
                }
                else if (message is not null)
                {
                    var value = ValueToShellLiteral(message, call.Line, out var literal);
                    script.Line(literal ? $"echo {ShellQuote(value)}" : $"echo \"{value}\"");
                }

                break;
            }

            case "dir":
            {
                var path = StringOf(call.First) ?? StringOf(call.Named("path")) ?? ".";
                script.Line("(");
                script.Indent();
                script.Line($"cd {ShellQuote(path)}");
                ConvertSteps(call.Body ?? [], context, false);
                script.Outdent();
                script.Line(")");
                break;
            }

            case "withEnv":
            {
                script.Line("(");
                script.Indent();
                if (call.First is GroovyList list)
                    foreach (var item in list.Items)
                    {
                        var text = StringOf(item) ?? string.Empty;
                        var equals = text.IndexOf('=');
                        if (equals <= 0) continue;

                        var interpolated = item is GroovyString { Interpolated: true };
                        var value = text[(equals + 1)..];
                        script.Line(interpolated
                            ? $"export {text[..equals]}=\"{RewriteInterpolation(value, call.Line)}\""
                            : $"export {text[..equals]}={ShellQuote(value)}");
                    }
                else
                    Warn(JenkinsWarningSeverity.Unsupported, "withEnv 的參數不是字串清單，已略過。", call.Line);

                ConvertSteps(call.Body ?? [], context, false);
                script.Outdent();
                script.Line(")");
                break;
            }

            case "withCredentials":
            {
                ConvertCredentialBindings(call);
                ConvertSteps(call.Body ?? [], context, false);
                break;
            }

            case "timeout":
                Warn(JenkinsWarningSeverity.Info, $"stage「{context.StageName}」內的 timeout 沒有對應，只有整場建置的逾時。", call.Line);
                ConvertSteps(call.Body ?? [], context, false);
                break;

            case "retry":
                Warn(JenkinsWarningSeverity.Info,
                    $"stage「{context.StageName}」內只包住部分步驟的 retry 沒有對應，內容照跑一次；要重試整個步驟可以在步驟設定填重試次數。", call.Line);
                ConvertSteps(call.Body ?? [], context, false);
                break;

            case "script":
            case "steps":
            case "lock":
            case "ws":
            case "ansiColor":
            case "timestamps":
            case "wrap":
            case "configFileProvider":
            case "sshagent":
            case "withMaven":
            case "withSonarQubeEnv":
            case "withAWS":
            case "nodejs":
            case "withNPM":
            case "withDockerRegistry":
            case "withDockerContainer":
            case "withKubeConfig":
                if (call.ShortName is not ("script" or "steps"))
                    Warn(JenkinsWarningSeverity.Warning,
                        $"stage「{context.StageName}」的 {call.ShortName} 包裝沒有對應，裡面的步驟照跑；需要的環境請自己準備。", call.Line);

                ConvertSteps(call.Body ?? [], context, false);
                break;

            case "node":
                if (call.First is not null)
                    Warn(JenkinsWarningSeverity.Info, $"stage「{context.StageName}」內的 node 標籤沒有對應，整個任務用同一個執行器。",
                        call.Line);

                ConvertSteps(call.Body ?? [], context, false);
                break;

            case "stage":
                script.Comment("stage：", StringOf(call.First) ?? string.Empty);
                ConvertSteps(call.Body ?? [], context, false);
                break;

            case "parallel":
                Warn(JenkinsWarningSeverity.Info,
                    $"stage「{context.StageName}」內的 parallel 已改成依序執行；要真的平行請拆成多個步驟並勾「與前一步同時執行」。", call.Line);
                foreach (var branch in call.Arguments)
                    if (branch.Value is GroovyClosure closure)
                    {
                        script.Comment("parallel 分支：", branch.Name ?? string.Empty);
                        ConvertSteps(closure.Body, context, false);
                    }

                break;

            case "checkout":
                ConvertCheckout(call, context);
                break;

            case "git":
            {
                var url = StringOf(call.Named("url")) ?? StringOf(call.First);
                var branch = StringOf(call.Named("branch"));
                if (url is not null) _job.Repository ??= url;

                if (branch is not null) _job.Branch = branch;

                if (call.Named("credentialsId") is not null)
                    Warn(JenkinsWarningSeverity.Warning,
                        "git 步驟用了 credentialsId；Walis 的 git 走執行器上的認證（憑證助手或 SSH 金鑰），請確認執行器拉得到程式碼。", call.Line);

                EmitCheckout(script, url ?? _job.Repository, branch ?? _job.Branch, call.Line);
                break;
            }

            case "archiveArtifacts":
            {
                var patterns = StringOf(call.Named("artifacts")) ?? StringOf(call.First);
                if (patterns is not null)
                    _artifacts.AddRange(patterns.Split(',').Select(pattern => pattern.Trim())
                        .Where(pattern => pattern.Length > 0));

                if (call.Named("excludes") is not null)
                    Warn(JenkinsWarningSeverity.Info, "archiveArtifacts 的 excludes 沒有對應。", call.Line);

                break;
            }

            case "junit":
            case "publishHTML":
            case "cobertura":
            case "jacoco":
            case "recordIssues":
            case "recordCoverage":
            case "publishCoverage":
            case "xunit":
            case "step":
            {
                var pattern = StringOf(call.First) ?? StringOf(call.Named("testResults"));
                if (pattern is not null && call.ShortName is "junit" or "xunit")
                {
                    _artifacts.AddRange(pattern.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0));
                    Warn(JenkinsWarningSeverity.Info, $"{call.ShortName} 報告沒有對應的檢視器，檔案「{pattern}」已加進產出路徑保存。",
                        call.Line);
                }
                else
                {
                    Warn(JenkinsWarningSeverity.Unsupported, $"報告外掛 {call.ShortName} 不支援；可以把報告檔加進產出路徑保存。", call.Line);
                }

                break;
            }

            case "stash":
            case "unstash":
                Warn(JenkinsWarningSeverity.Info, $"{call.ShortName} 不需要：Walis 的步驟共用同一個工作目錄。", call.Line);
                break;

            case "input":
                Warn(JenkinsWarningSeverity.Unsupported,
                    $"stage「{context.StageName}」的 input（人工核准）不支援，已留成註解；可以拆成兩個任務由人手觸發第二個。", call.Line);
                script.Comment("TODO input（人工核准）：",
                    StringOf(call.Named("message")) ?? StringOf(call.First) ?? string.Empty);
                break;

            case "build":
            {
                var target = StringOf(call.Named("job")) ?? StringOf(call.First);
                if (target is null) break;

                var name = target.Contains('/') ? target[(target.LastIndexOf('/') + 1)..] : target;
                if (!_downstream.Any(existing => existing.JobName == name))
                    _downstream.Add(new DownstreamTarget { JobName = name, When = DownstreamWhen.Success });

                Warn(JenkinsWarningSeverity.Warning,
                    $"build job「{target}」改成「完成後觸發下游任務」：Jenkins 是在流程中同步等它跑完，Walis 是這個任務成功後才觸發。下游任務需要存在且名稱相同。",
                    call.Line);
                if (call.Named("parameters") is not null)
                    Warn(JenkinsWarningSeverity.Info, $"傳給「{name}」的參數沒有對應；下游會用自己的預設值，並拿到 WALIS_UPSTREAM_* 變數。",
                        call.Line);

                script.Comment("build job（已設為下游任務）：", target);
                break;
            }

            case "sleep":
            {
                var amount = IntOf(call.Named("time")) ?? IntOf(call.First) ?? 1;
                var unit = (StringOf(call.Named("unit")) ?? "SECONDS").ToUpperInvariant();
                var seconds = unit switch
                {
                    "MINUTES" => amount * 60,
                    "HOURS" => amount * 3600,
                    "MILLISECONDS" => Math.Max(1, (int)Math.Ceiling(amount / 1000.0)),
                    _ => amount
                };
                script.Line($"sleep {seconds}");
                break;
            }

            case "error":
            {
                var message = StringOf(call.First) ?? StringOf(call.Named("message")) ?? "error";
                script.Line($"echo {ShellQuote(message)} >&2");
                script.Line("exit 1");
                break;
            }

            case "unstable":
                Warn(JenkinsWarningSeverity.Info, "unstable 沒有對應（Walis 沒有不穩定狀態），改成印出訊息並繼續。", call.Line);
                script.Line(
                    $"echo {ShellQuote("UNSTABLE: " + (StringOf(call.Named("message")) ?? StringOf(call.First) ?? string.Empty))}");
                break;

            case "catchError":
            case "warnError":
                Warn(JenkinsWarningSeverity.Info, $"{call.ShortName} 改成「失敗也繼續」的子 shell。", call.Line);
                script.Line("(");
                script.Indent();
                ConvertSteps(call.Body ?? [], context, false);
                script.Outdent();
                script.Line(") || true");
                break;

            case "deleteDir":
            case "cleanWs":
                Warn(JenkinsWarningSeverity.Warning, $"{call.ShortName} 改成清空目前目錄的命令；也可以改用系統設定的「清空工作目錄」。", call.Line);
                script.Line("find . -mindepth 1 -delete");
                break;

            case "writeFile":
            {
                var file = StringOf(call.Named("file"));
                var text = call.Named("text");
                if (file is null || text is null)
                {
                    Warn(JenkinsWarningSeverity.Unsupported, "writeFile 缺少 file 或 text，已略過。", call.Line);
                    break;
                }

                var content = ValueToShellLiteral(text, call.Line, out var literal);
                script.Line($"cat > {ShellQuote(file)} <<{(literal ? "'WALIS_EOF'" : "WALIS_EOF")}");
                foreach (var line in content.Split('\n')) script.Raw(line);

                script.Line("WALIS_EOF");
                break;
            }

            case "readFile":
            case "fileExists":
            case "readJSON":
            case "readYaml":
            case "readProperties":
            case "findFiles":
            case "httpRequest":
            case "waitUntil":
            case "libraryResource":
                Warn(JenkinsWarningSeverity.Unsupported,
                    $"stage「{context.StageName}」的 {call.ShortName} 是 Groovy 層的操作，請改成 shell 命令。", call.Line,
                    RawText(call));
                script.Comment("TODO：", RawText(call));
                break;

            case "mail":
            case "emailext":
            case "slackSend":
            case "hipchatSend":
            case "office365ConnectorSend":
            case "telegramSend":
            case "discordSend":
                Warn(JenkinsWarningSeverity.Info, $"{call.ShortName} 已略過；請改用 Walis 的通知設定（失敗、成功、恢復都可以送）。", call.Line);
                break;

            case "tool":
                Warn(JenkinsWarningSeverity.Warning, $"tool「{StringOf(call.First)}」沒有對應；請確認執行器的 PATH 已有這個工具。",
                    call.Line);
                break;

            case "bat":
            case "powershell":
            case "pwsh":
            {
                var command = StringOf(call.First) ?? StringOf(call.Named("script")) ?? string.Empty;
                Warn(JenkinsWarningSeverity.Unsupported,
                    $"stage「{context.StageName}」的 {call.ShortName}（Windows）不支援，已留成註解。", call.Line);
                script.Comment($"TODO {call.ShortName}：", command);
                break;
            }

            case "milestone":
            case "properties":
            case "skipDefaultCheckout":
                break;

            case "docker":
                Warn(JenkinsWarningSeverity.Unsupported,
                    $"stage「{context.StageName}」的 docker.* 呼叫需要手動改成 docker 命令或「在容器中執行」動作。", call.Line, RawText(call));
                script.Comment("TODO docker：", RawText(call));
                break;

            default:
                Warn(JenkinsWarningSeverity.Unsupported, $"stage「{context.StageName}」的步驟「{call.ShortName}」不支援，已留成註解。",
                    call.Line, RawText(call));
                script.Comment("TODO 不支援的步驟：", RawText(call));
                break;
        }
    }

    private void ConvertCredentialBindings(GroovyCall call)
    {
        var bindings = call.First is GroovyList list
            ? list.Items
            : call.Arguments.Select(argument => argument.Value).ToList();

        foreach (var binding in bindings.OfType<GroovyCallValue>().Select(value => value.Call))
        {
            var id = StringOf(binding.Named("credentialsId")) ?? "?";
            var variables = binding.Name switch
            {
                "usernamePassword" => new[]
                    { StringOf(binding.Named("usernameVariable")), StringOf(binding.Named("passwordVariable")) },
                "string" or "file" or "sshUserPrivateKey" or "certificate" or "dockerCert" =>
                [
                    StringOf(binding.Named("variable")) ?? StringOf(binding.Named("keyFileVariable")) ??
                    StringOf(binding.Named("usernameVariable")),
                    StringOf(binding.Named("passphraseVariable"))
                ],
                "usernameColonPassword" => [StringOf(binding.Named("variable"))],
                _ => []
            };

            var added = false;
            foreach (var variable in variables.Where(variable => !string.IsNullOrWhiteSpace(variable)))
            {
                if (_environment.All(existing => existing.Name != variable))
                    _environment.Add(new JenkinsEnvironmentVariable
                    {
                        Name = variable!, Value = string.Empty, IsSecret = true,
                        Source = $"Jenkins 憑證 {id}（{binding.Name}）"
                    });

                added = true;
            }

            if (binding.Name is "file" or "sshUserPrivateKey" or "certificate")
                Warn(JenkinsWarningSeverity.Warning,
                    $"withCredentials 的 {binding.Name}「{id}」在 Jenkins 是檔案路徑；Walis 的機密是值，腳本可能要改成先把值寫進檔案。", binding.Line);
            else if (added)
                Warn(JenkinsWarningSeverity.Warning,
                    $"withCredentials「{id}」對應成機密環境變數 {string.Join("、", variables.Where(v => v is not null))}，匯入後請填入值。",
                    binding.Line);
            else
                Warn(JenkinsWarningSeverity.Unsupported, $"withCredentials 的 {binding.Name} 不支援。", binding.Line);
        }
    }

    private void ConvertCheckout(GroovyCall call, StepContext context)
    {
        string? url = null;
        string? branch = null;

        if (call.First is GroovyMap map)
        {
            url = FindUrl(map);
            branch = FindBranch(map);
        }
        else if (call.First is GroovyCallValue { Call: var scm } && scm.Name is "scmGit" or "git")
        {
            url = StringOf(scm.Named("url"));
            branch = StringOf(scm.Named("branch"));
            if (scm.Named("userRemoteConfigs") is GroovyList remotes)
                url ??= remotes.Items.OfType<GroovyMap>().Select(FindUrl).FirstOrDefault(found => found is not null)
                        ?? remotes.Items.OfType<GroovyCallValue>().Select(remote => StringOf(remote.Call.Named("url")))
                            .FirstOrDefault(found => found is not null);

            if (scm.Named("branches") is GroovyList branches)
                branch ??= branches.Items.OfType<GroovyMap>()
                    .Select(item => StringOf(item.Entries.FirstOrDefault(entry => entry.Name == "name")?.Value))
                    .FirstOrDefault(found => found is not null);
        }

        if (url is not null) _job.Repository ??= url;

        if (branch is not null) _job.Branch = branch.Replace("*/", string.Empty).Replace("refs/heads/", string.Empty);

        if (_skipDefaultCheckout && call.First is GroovyIdentifier { Name: "scm" })
        {
            // skipDefaultCheckout followed by an explicit checkout scm: the code still has to be fetched.
        }

        EmitCheckout(context.Script, url ?? _job.Repository, branch ?? _job.Branch, call.Line);
    }

    private static string? FindUrl(GroovyMap map)
    {
        foreach (var entry in map.Entries)
        {
            if (entry.Name == "url" && StringOf(entry.Value) is { } url) return url;

            if (entry.Value is GroovyList list)
                foreach (var item in list.Items.OfType<GroovyMap>())
                    if (FindUrl(item) is { } nested)
                        return nested;
        }

        return null;
    }

    private static string? FindBranch(GroovyMap map)
    {
        var branches = map.Entries.FirstOrDefault(entry => entry.Name == "branches")?.Value as GroovyList;
        var first = branches?.Items.OfType<GroovyMap>().FirstOrDefault();
        return StringOf(first?.Entries.FirstOrDefault(entry => entry.Name == "name")?.Value);
    }

    private void EmitCheckout(ScriptBuilder script, string? repository, string branch, int line)
    {
        var target = branch.Replace("*/", string.Empty).Replace("refs/heads/", string.Empty);
        if (string.IsNullOrWhiteSpace(repository))
        {
            Warn(JenkinsWarningSeverity.Warning, "checkout 需要儲存庫網址：請在任務設定填「儲存庫」，或把工作目錄指到已經 clone 好的目錄。", line);
            script.Comment("TODO checkout：", "請填任務的儲存庫網址，或把工作目錄指到已 clone 的目錄");
            return;
        }

        Warn(JenkinsWarningSeverity.Info, "checkout 對應成「第一次 clone、之後 fetch 並切到指定分支」，程式碼會留在工作目錄。", line);
        script.Line("if [ -d .git ]; then");
        script.Line($"  git fetch --all --prune && git checkout {ShellQuote(target)} && git pull --ff-only");
        script.Line("else");
        script.Line($"  git clone --branch {ShellQuote(target)} {ShellQuote(repository)} .");
        script.Line("fi");
    }

    // ---------------------------------------------------------------- scripted

    private void ConvertScripted(IReadOnlyList<GroovyNode> nodes)
    {
        var loose = new ScriptBuilder();
        var looseContext = new StepContext("前置", loose, 0);
        var count = 0;

        void FlushLoose(string name)
        {
            if (loose.Lines.Count != 0)
            {
                _steps.Add(
                    new ImportedStep(name, [.. loose.Lines], StepRunCondition.Success, false, 0, _pipelineDocker));
                loose.Clear();
            }
        }

        void Visit(IReadOnlyList<GroovyNode> body)
        {
            foreach (var node in body)
                switch (node)
                {
                    case GroovyCall { Name: "node" } machine:
                    {
                        var label = StringOf(machine.First) ?? StringOf(machine.Named("label"));
                        if (label is not null)
                        {
                            _job.RunnerName ??= label;
                            Warn(JenkinsWarningSeverity.Warning,
                                $"node label「{label}」已對應成執行器名稱；Walis 沒有這個執行器的話請改成 local。", machine.Line);
                        }

                        Visit(machine.Body ?? []);
                        break;
                    }

                    case GroovyCall { Name: "stage" } stage:
                    {
                        FlushLoose(count == 0 ? "前置" : $"腳本 {count}");
                        var name = StringOf(stage.First) ?? StringOf(stage.Named("name")) ?? $"stage {++count}";
                        var script = new ScriptBuilder();
                        var context = new StepContext(name, script, _defaultRetries);
                        ConvertSteps(stage.Body ?? [], context, true);
                        _steps.Add(new ImportedStep(name, script.Lines, StepRunCondition.Success, false,
                            context.Retries, _pipelineDocker));
                        count++;
                        break;
                    }

                    case GroovyCall { Name: "parallel" } parallel
                        when parallel.Arguments.Any(argument => argument.Value is GroovyClosure):
                    {
                        FlushLoose(count == 0 ? "前置" : $"腳本 {count}");
                        var first = true;
                        foreach (var branch in parallel.Arguments)
                        {
                            if (branch.Value is not GroovyClosure closure) continue;

                            var name = branch.Name ?? $"parallel {++count}";
                            var script = new ScriptBuilder();
                            var context = new StepContext(name, script, _defaultRetries);
                            ConvertSteps(closure.Body, context, true);
                            _steps.Add(new ImportedStep(name, script.Lines, StepRunCondition.Success, !first,
                                context.Retries, _pipelineDocker));
                            first = false;
                        }

                        break;
                    }

                    case GroovyCall { Name: "properties" } properties:
                        ConvertProperties(properties);
                        break;

                    case GroovyCall { Name: "timeout", Body: not null } timeout:
                    {
                        var minutes = TimeoutMinutes(timeout);
                        if (minutes is not null) _job.TimeoutMinutes = minutes.Value;

                        Visit(timeout.Body);
                        break;
                    }

                    case GroovyCall { Name: "pipeline" }:
                        break;

                    default:
                        ConvertSteps([node], looseContext, false);
                        break;
                }
        }

        Visit(nodes);
        FlushLoose(count == 0 ? "腳本" : $"腳本 {count}");
    }

    private void ConvertProperties(GroovyCall properties)
    {
        if (properties.First is not GroovyList list) return;

        foreach (var item in list.Items.OfType<GroovyCallValue>().Select(value => value.Call))
            switch (item.Name)
            {
                case "parameters":
                    if (item.First is GroovyList parameters)
                        ConvertParameters(parameters.Items.OfType<GroovyCallValue>()
                            .Select(value => (GroovyNode)value.Call).ToList());

                    break;
                case "pipelineTriggers":
                    if (item.First is GroovyList triggers)
                        ConvertTriggers(triggers.Items.OfType<GroovyCallValue>().Select(value => (GroovyNode)value.Call)
                            .ToList());

                    break;
                case "disableConcurrentBuilds":
                    _job.AllowConcurrentBuilds = false;
                    break;
                case "buildDiscarder":
                    Warn(JenkinsWarningSeverity.Info, "buildDiscarder 沒有對應；建置紀錄的保留天數在系統設定。", item.Line);
                    break;
                default:
                    Warn(JenkinsWarningSeverity.Unsupported, $"properties 的「{item.Name}」不支援。", item.Line);
                    break;
            }
    }

    // ---------------------------------------------------------------- helpers

    private void Warn(string severity, string message, int? line = null, string? snippet = null)
    {
        var key = $"{severity}|{message}|{line}";
        if (_seenWarnings.Add(key))
            _warnings.Add(new JenkinsWarning(severity, message, line, snippet is null ? null : Truncate(snippet, 300)));
    }

    private static string Truncate(string value, int length)
    {
        return value.Length <= length ? value : value[..length] + "…";
    }

    private static string? StringOf(GroovyValue? value)
    {
        return value switch
        {
            GroovyString text => text.Text,
            GroovyNumber number => number.Text,
            GroovyIdentifier { Name: "true" or "false" } flag => flag.Name,
            _ => null
        };
    }

    private static int? IntOf(GroovyValue? value)
    {
        return value switch
        {
            GroovyNumber number when int.TryParse(number.Text, out var parsed) => parsed,
            GroovyString text when int.TryParse(text.Text, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>Turns a value into a string usable in shell; literal true means it can be wrapped in single quotes.</summary>
    private string ValueToShellLiteral(GroovyValue value, int line, out bool literal)
    {
        switch (value)
        {
            case GroovyString { Interpolated: false } text:
                literal = true;
                return text.Text;
            case GroovyString text:
                literal = false;
                return RewriteInterpolation(text.Text, line).Replace("\"", "\\\"");
            case GroovyNumber number:
                literal = true;
                return number.Text;
            case GroovyIdentifier identifier:
                literal = false;
                return identifier.Name is "true" or "false" or "null"
                    ? identifier.Name
                    : $"${{{MapVariable(identifier.Name, line)}}}";
            default:
                literal = false;
                Warn(JenkinsWarningSeverity.Warning, "有一個值是 Groovy 運算式，已原樣放進腳本，請確認。", line, RawValueText(value));
                return RawValueText(value);
        }
    }

    private string RewriteShell(string command, bool interpolated, int line)
    {
        var text = interpolated ? RewriteInterpolation(command, line) : command;
        return JenkinsVariable().Replace(text,
            match => "$" + MapJenkinsVariable(match.Groups[1].Value, match.Groups[2].Value));
    }

    /// <summary>Replaces GString <c>${env.X}</c>/<c>${params.X}</c>/<c>$X</c> with shell <c>${X}</c>.</summary>
    private string RewriteInterpolation(string text, int line)
    {
        var rewritten = Interpolation().Replace(text, match =>
        {
            var expression = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            expression = expression.Trim();

            if (SimplePath().IsMatch(expression)) return $"${{{MapVariable(expression, line)}}}";

            Warn(JenkinsWarningSeverity.Warning, $"字串裡的 Groovy 運算式「${{{expression}}}」需要手動改寫。", line);
            return "${" + expression + "}";
        });

        return rewritten;
    }

    private string MapVariable(string path, int line)
    {
        if (path.StartsWith("env.", StringComparison.Ordinal)) return MapJenkinsVariable(string.Empty, path[4..]);

        if (path.StartsWith("params.", StringComparison.Ordinal)) return path[7..];

        if (path.Contains('.'))
        {
            Warn(JenkinsWarningSeverity.Warning, $"變數「{path}」是 Groovy 物件，需要手動改寫。", line);
            return path.Replace('.', '_');
        }

        return MapJenkinsVariable(string.Empty, path);
    }

    private static string MapJenkinsVariable(string braces, string name)
    {
        var mapped = name switch
        {
            "BUILD_NUMBER" or "BUILD_ID" => "WALIS_BUILD_NUMBER",
            "JOB_NAME" or "JOB_BASE_NAME" => "WALIS_JOB_NAME",
            "BRANCH_NAME" or "GIT_BRANCH" => "WALIS_BRANCH",
            "WORKSPACE" => "PWD",
            _ => name
        };

        return braces.Length == 0 ? mapped : $"{{{mapped}}}";
    }

    private static IEnumerable<string> Dedent(string script)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count != 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);

        while (lines.Count != 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);

        var indent = lines.Where(line => line.Trim().Length != 0)
            .Select(line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();

        return lines.Select(line => line.Length >= indent ? line[indent..] : line.TrimStart());
    }

    private static string ShellQuote(string value)
    {
        return ActionTemplate.Quote(value);
    }

    private static string RawText(GroovyNode node)
    {
        return node switch
        {
            GroovyRaw raw => raw.Text,
            GroovyCall call =>
                $"{call.Name}({string.Join(", ", call.Arguments.Select(argument => argument.Name is null ? RawValueText(argument.Value) : $"{argument.Name}: {RawValueText(argument.Value)}"))})",
            GroovyAssignment assignment => $"{assignment.Target} = {RawValueText(assignment.Value)}",
            _ => string.Empty
        };
    }

    private static string RawValueText(GroovyValue value)
    {
        return value switch
        {
            GroovyString text => $"'{text.Text}'",
            GroovyNumber number => number.Text,
            GroovyIdentifier identifier => identifier.Name,
            GroovyList list => $"[{string.Join(", ", list.Items.Select(RawValueText))}]",
            GroovyMap map =>
                $"[{string.Join(", ", map.Entries.Select(entry => $"{entry.Name}: {RawValueText(entry.Value)}"))}]",
            GroovyClosure => "{ … }",
            GroovyCallValue call => RawText(call.Call),
            GroovyRawValue raw => raw.Text,
            _ => string.Empty
        };
    }

    [GeneratedRegex(@"\$\{([^{}]+)\}|\$([A-Za-z_][A-Za-z0-9_.]*)")]
    private static partial Regex Interpolation();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex SimplePath();

    /// <summary>Jenkins built-in variables used directly in shell: $BUILD_NUMBER, ${WORKSPACE}.</summary>
    [GeneratedRegex(@"\$(\{?)(BUILD_NUMBER|BUILD_ID|JOB_NAME|JOB_BASE_NAME|BRANCH_NAME|GIT_BRANCH|WORKSPACE)\}?")]
    private static partial Regex JenkinsVariable();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex ParameterName();

    // ---------------------------------------------------------------- steps → shell

    private sealed class StepContext(string stageName, ScriptBuilder script, int retries)
    {
        public string StageName { get; } = stageName;
        public ScriptBuilder Script { get; } = script;
        public int Retries { get; set; } = retries;
    }

    private sealed record DockerAgent(string Image, string? Args);

    private sealed class ImportedStep(
        string name,
        IReadOnlyList<string> lines,
        string runWhen,
        bool parallel,
        int retries,
        DockerAgent? docker)
    {
        public ActionStep ToActionStep()
        {
            var script = string.Join('\n', lines);
            var parameters = docker is null
                ? new Dictionary<string, string?> { ["command"] = script }
                : new Dictionary<string, string?>
                    { ["image"] = docker.Image, ["script"] = script, ["args"] = docker.Args ?? string.Empty };

            return new ActionStep
            {
                Id = Guid.NewGuid().ToString("N"),
                ActionId = docker is null ? ShellAction : ContainerAction,
                Name = name.Length > 200 ? name[..200] : name,
                Enabled = true,
                RunWhen = runWhen,
                Retries = retries,
                Parallel = parallel,
                Parameters = parameters
            };
        }
    }

    /// <summary>Accumulates a step script, handling indentation and comments.</summary>
    private sealed class ScriptBuilder
    {
        private readonly List<string> _lines = [];
        private int _indent;

        public IReadOnlyList<string> Lines => _lines;

        public void Line(string text)
        {
            _lines.Add(new string(' ', _indent * 2) + text);
        }

        /// <summary>Heredoc content is not indented.</summary>
        public void Raw(string text)
        {
            _lines.Add(text);
        }

        public void Comment(string label, string text)
        {
            var body = text.Replace("\r\n", "\n").Split('\n');
            _lines.Add($"{new string(' ', _indent * 2)}# {label}{body[0]}");
            foreach (var line in body.Skip(1)) _lines.Add($"{new string(' ', _indent * 2)}#   {line}");
        }

        public void Indent()
        {
            _indent++;
        }

        public void Outdent()
        {
            _indent = Math.Max(0, _indent - 1);
        }

        public void Clear()
        {
            _lines.Clear();
        }
    }
}