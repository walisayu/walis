using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Walis.Core.Actions;
using Walis.Core.Models;

namespace Walis.Core.Jenkins;

/// <summary>
///     A Jenkins job's config.xml: shell build steps, schedule, Git, parameters, artifacts and downstream of a freestyle job (<c>&lt;project&gt;</c>);
///     the embedded script of a pipeline (<c>&lt;flow-definition&gt;</c>) is handed to <see cref="JenkinsfileConverter" />.
/// </summary>
public static partial class JenkinsConfigXmlConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static JenkinsImportResult Convert(string xml, string? preferredName)
    {
        XDocument document;
        try
        {
            // Jenkins writes XML 1.1 and .NET only accepts 1.0; the content is the same, so just rewrite the declaration.
            document = XDocument.Parse(XmlVersion().Replace(xml, "version=\"1.0\"", 1));
        }
        catch (Exception exception)
        {
            var broken = new JenkinsJobProposal { Name = preferredName ?? string.Empty };
            return new JenkinsImportResult("freestyle", broken, [],
                [new JenkinsWarning(JenkinsWarningSeverity.Unsupported, $"config.xml 不是有效的 XML：{exception.Message}")]);
        }

        var root = document.Root;
        if (root is null)
            return new JenkinsImportResult("freestyle", new JenkinsJobProposal { Name = preferredName ?? string.Empty },
                [], [new JenkinsWarning(JenkinsWarningSeverity.Unsupported, "config.xml 是空的。")]);

        if (root.Name.LocalName == "flow-definition") return ConvertPipelineDefinition(root, preferredName);

        if (root.Name.LocalName is not ("project" or "matrix-project"))
            return new JenkinsImportResult("freestyle", new JenkinsJobProposal { Name = preferredName ?? string.Empty },
                [],
                [
                    new JenkinsWarning(JenkinsWarningSeverity.Unsupported,
                        $"不支援的 Jenkins 任務型別「{root.Name.LocalName}」；支援 freestyle 的 project、matrix-project 與 pipeline 的 flow-definition。")
                ]);

        var warnings = new List<JenkinsWarning>();
        var environment = new List<JenkinsEnvironmentVariable>();
        var job = new JenkinsJobProposal
        {
            Name = preferredName ?? string.Empty,
            Description = Text(root.Element("description")),
            IsDisabled = string.Equals(Text(root.Element("disabled")), "true", StringComparison.OrdinalIgnoreCase)
        };

        if (string.Equals(Text(root.Element("concurrentBuild")), "false", StringComparison.OrdinalIgnoreCase) ||
            root.Element("concurrentBuild") is null)
            // Freestyle does not run concurrently by default ("Execute concurrent builds if necessary" unchecked).
            job.AllowConcurrentBuilds = false;

        var label = Text(root.Element("assignedNode"));
        if (!string.IsNullOrWhiteSpace(label))
        {
            job.RunnerName = label;
            warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Warning,
                $"「限制專案執行的節點」標籤「{label}」已對應成執行器名稱；Walis 沒有這個執行器的話請改成 local。"));
        }

        ConvertScm(root.Element("scm"), job, warnings);
        ConvertTriggers(root.Element("triggers"), job, warnings);
        ConvertProperties(root.Element("properties"), job, warnings);
        ConvertAxes(root.Element("axes"), job, warnings);
        ConvertBuildWrappers(root.Element("buildWrappers"), job, environment, warnings);
        ConvertBuilders(root.Element("builders"), job, environment, warnings);
        ConvertPublishers(root.Element("publishers"), job, warnings);

        if (job.Steps.Count == 0)
            warnings.Add(
                new JenkinsWarning(JenkinsWarningSeverity.Warning, "沒有找到 shell 建置步驟；只有 Windows 批次或外掛步驟時要手動改寫。"));

        return new JenkinsImportResult(root.Name.LocalName == "matrix-project" ? "matrix" : "freestyle", job,
            environment, warnings);
    }

    private static JenkinsImportResult ConvertPipelineDefinition(XElement root, string? preferredName)
    {
        var definition = root.Element("definition");
        var script = Text(definition?.Element("script"));

        if (string.IsNullOrWhiteSpace(script))
        {
            var path = Text(definition?.Element("scriptPath")) ?? "Jenkinsfile";
            var proposal = new JenkinsJobProposal
                { Name = preferredName ?? string.Empty, Description = Text(root.Element("description")) };
            return new JenkinsImportResult("pipeline", proposal, [],
            [
                new JenkinsWarning(JenkinsWarningSeverity.Warning,
                    $"這個 pipeline 任務的腳本在儲存庫裡（{path}），config.xml 沒有內容；請直接貼上該檔案。")
            ]);
        }

        var result = JenkinsfileConverter.Convert(script, preferredName);
        var description = Text(root.Element("description"));
        if (!string.IsNullOrWhiteSpace(description)) result.Job.Description ??= description;

        if (string.Equals(Text(root.Element("disabled")), "true", StringComparison.OrdinalIgnoreCase))
            result.Job.IsDisabled = true;

        var warnings = result.Warnings.ToList();
        ConvertProperties(root.Element("properties"), result.Job, warnings);
        return result with { Warnings = warnings };
    }

    private static void ConvertScm(XElement? scm, JenkinsJobProposal job, List<JenkinsWarning> warnings)
    {
        if (scm is null) return;

        var kind = scm.Attribute("class")?.Value ?? string.Empty;
        if (kind.Contains("NullSCM", StringComparison.Ordinal)) return;

        if (!kind.Contains("GitSCM", StringComparison.Ordinal))
        {
            warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Unsupported, $"版本控制「{kind}」不支援，只支援 Git。"));
            return;
        }

        var url = scm.Descendants("url").Select(Text).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var branch = scm.Element("branches")?.Descendants("name").Select(Text)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (url is not null) job.Repository = url;

        if (branch is not null) job.Branch = branch.Replace("*/", string.Empty).Replace("refs/heads/", string.Empty);

        if (scm.Descendants("credentialsId").Any(element => !string.IsNullOrWhiteSpace(Text(element))))
            warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Warning,
                "Git 用了 Jenkins 憑證；Walis 的 git 走執行器上的認證，請確認執行器拉得到程式碼。"));

        if (url is not null)
        {
            var target = job.Branch;
            job.Steps.Add(Shell("Checkout",
                string.Join('\n', "if [ -d .git ]; then",
                    $"  git fetch --all --prune && git checkout {ActionTemplate.Quote(target)} && git pull --ff-only",
                    "else", $"  git clone --branch {ActionTemplate.Quote(target)} {ActionTemplate.Quote(url)} .",
                    "fi")));
            warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Info,
                "Git 對應成「第一次 clone、之後 fetch 並切到指定分支」的步驟，程式碼會留在工作目錄。"));
        }
    }

    private static void ConvertTriggers(XElement? triggers, JenkinsJobProposal job, List<JenkinsWarning> warnings)
    {
        foreach (var trigger in triggers?.Elements() ?? [])
        {
            var name = trigger.Name.LocalName;
            if (name.EndsWith("TimerTrigger", StringComparison.Ordinal))
            {
                var spec = Text(trigger.Element("spec"));
                if (spec is not null)
                {
                    var cron = JenkinsCron.Convert(spec, job.Name.Length == 0 ? spec : job.Name, warnings);
                    if (cron is not null)
                    {
                        job.TriggerKind = TriggerMode.Cron;
                        job.Cron = cron;
                    }
                }
            }
            else if (name.EndsWith("SCMTrigger", StringComparison.Ordinal))
            {
                job.TriggerKind = TriggerMode.Push;
                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Warning,
                    "SCM 輪詢對應成「推送後自動建置」，請到系統設定拿 webhook 網址設到 Git 伺服器。"));
            }
            else if (name.Contains("ReverseBuildTrigger", StringComparison.Ordinal))
            {
                var upstream = Text(trigger.Element("upstreamProjects")) ?? "?";
                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Warning,
                    $"「在其他專案建置後觸發」要反過來設定：請到任務「{upstream}」的「完成後觸發下游任務」加上這個任務。"));
            }
            else if (name.Contains("GitHubPush", StringComparison.Ordinal) ||
                     name.Contains("GitLab", StringComparison.Ordinal) ||
                     name.Contains("Bitbucket", StringComparison.Ordinal))
            {
                job.TriggerKind = TriggerMode.Push;
                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Info, $"{name} 對應成「推送後自動建置」，請設定 webhook。"));
            }
            else
            {
                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Unsupported, $"觸發方式「{name}」不支援。"));
            }
        }
    }

    private static void ConvertProperties(XElement? properties, JenkinsJobProposal job, List<JenkinsWarning> warnings)
    {
        foreach (var property in properties?.Elements() ?? [])
        {
            var name = property.Name.LocalName;
            if (name.EndsWith("ParametersDefinitionProperty", StringComparison.Ordinal))
            {
                var definitions = new List<BuildParameterDefinition>();
                foreach (var definition in property.Element("parameterDefinitions")?.Elements() ?? [])
                    ConvertParameter(definition, definitions, warnings);

                if (definitions.Count != 0) job.ParametersJson = BuildParameters.Serialize(definitions);
            }
            else if (name.EndsWith("BuildDiscarderProperty", StringComparison.Ordinal))
            {
                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Info, "「捨棄舊的建置」沒有對應；建置紀錄的保留天數在系統設定。"));
            }
            else if (name.EndsWith("DisableConcurrentBuildsJobProperty", StringComparison.Ordinal))
            {
                job.AllowConcurrentBuilds = false;
            }
            else if (name.EndsWith("PipelineTriggersJobProperty", StringComparison.Ordinal))
            {
                ConvertTriggers(property.Element("triggers"), job, warnings);
            }
            else if (!name.EndsWith("GithubProjectProperty", StringComparison.Ordinal))
            {
                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Unsupported, $"任務屬性「{name}」不支援。"));
            }
        }
    }

    private static void ConvertParameter(XElement definition, List<BuildParameterDefinition> definitions,
        List<JenkinsWarning> warnings)
    {
        var kind = definition.Name.LocalName;
        var name = Text(definition.Element("name"));
        if (string.IsNullOrWhiteSpace(name)) return;

        if (!ParameterName().IsMatch(name) || name.StartsWith("WALIS_", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Unsupported, $"參數名稱「{name}」不符合 Walis 的規則，已略過。"));
            return;
        }

        var description = Text(definition.Element("description"));
        var defaultValue = Text(definition.Element("defaultValue"));

        var parameter = kind switch
        {
            "hudson.model.StringParameterDefinition" => New(BuildParameterTypes.String, defaultValue),
            "hudson.model.TextParameterDefinition" => New(BuildParameterTypes.Text, defaultValue),
            "hudson.model.BooleanParameterDefinition" => New(BuildParameterTypes.Boolean,
                string.Equals(defaultValue, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false"),
            "hudson.model.PasswordParameterDefinition" => New(BuildParameterTypes.Password, null),
            "hudson.model.ChoiceParameterDefinition" => Choice(),
            _ => null
        };

        if (parameter is null)
        {
            warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Unsupported, $"參數型別「{kind}」（{name}）不支援，已略過。"));
            return;
        }

        definitions.Add(parameter);
        return;

        BuildParameterDefinition New(string type, string? value)
        {
            return new BuildParameterDefinition
            {
                Name = name,
                Label = name,
                Type = type,
                Default = value,
                Description = description
            };
        }

        BuildParameterDefinition? Choice()
        {
            var choices = definition.Element("choices")?.Descendants("string").Select(Text)
                .Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).ToList() ?? [];
            if (choices.Count == 0)
            {
                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Unsupported, $"選項參數 {name} 沒有選項，已略過。"));
                return null;
            }

            var choice = New(BuildParameterTypes.Choice, choices[0]);
            choice.Options.AddRange(choices);
            return choice;
        }
    }

    private static void ConvertAxes(XElement? axes, JenkinsJobProposal job, List<JenkinsWarning> warnings)
    {
        if (axes is null) return;

        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var axis in axes.Elements())
        {
            var name = Text(axis.Element("name"));
            var values = axis.Element("values")?.Elements("string").Select(Text)
                .Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).ToList() ?? [];
            if (!string.IsNullOrWhiteSpace(name) && values.Count != 0)
                map[name] = values;
            else
                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Unsupported,
                    $"矩陣軸「{axis.Name.LocalName}」不支援（只支援文字軸）。"));
        }

        if (map.Count != 0)
        {
            job.MatrixJson = JsonSerializer.Serialize(map, JsonOptions);
            warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Info, "矩陣軸已對應成任務的矩陣，每個組合各跑一次。"));
        }
    }

    private static void ConvertBuildWrappers(XElement? wrappers, JenkinsJobProposal job,
        List<JenkinsEnvironmentVariable> environment, List<JenkinsWarning> warnings)
    {
        foreach (var wrapper in wrappers?.Elements() ?? [])
        {
            var name = wrapper.Name.LocalName;
            if (name.Contains("BuildTimeoutWrapper", StringComparison.Ordinal))
            {
                var minutes = wrapper.Descendants("timeoutMinutes").Select(Text).FirstOrDefault();
                if (int.TryParse(minutes, out var parsed))
                    job.TimeoutMinutes = Math.Clamp(parsed, 1, 1440);
                else
                    warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Info, "建置逾時策略不是固定分鐘數，沿用預設逾時。"));
            }
            else if (name.Contains("SecretBuildWrapper", StringComparison.Ordinal))
            {
                foreach (var binding in wrapper.Element("bindings")?.Elements() ?? [])
                {
                    var credential = Text(binding.Element("credentialsId")) ?? "?";
                    foreach (var variable in new[]
                                 { "variable", "usernameVariable", "passwordVariable", "keyFileVariable" })
                    {
                        var value = Text(binding.Element(variable));
                        if (!string.IsNullOrWhiteSpace(value) && environment.All(existing => existing.Name != value))
                            environment.Add(new JenkinsEnvironmentVariable
                            {
                                Name = value, Value = string.Empty, IsSecret = true, Source = $"Jenkins 憑證 {credential}"
                            });
                    }
                }

                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Warning, "憑證綁定已對應成機密環境變數，匯入後請填入值。"));
            }
            else if (name.Contains("EnvInjectBuildWrapper", StringComparison.Ordinal) ||
                     name.Contains("EnvInjectPasswordWrapper", StringComparison.Ordinal))
            {
                var content = wrapper.Descendants("propertiesContent").Select(Text).FirstOrDefault();
                foreach (var line in (content ?? string.Empty).Split('\n'))
                {
                    var equals = line.IndexOf('=');
                    if (equals > 0)
                        environment.Add(new JenkinsEnvironmentVariable
                        {
                            Name = line[..equals].Trim(), Value = line[(equals + 1)..].Trim(),
                            IsSecret = name.Contains("Password", StringComparison.Ordinal), Source = "EnvInject"
                        });
                }
            }
            else if (!name.Contains("TimestamperBuildWrapper", StringComparison.Ordinal) &&
                     !name.Contains("AnsiColor", StringComparison.Ordinal))
            {
                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Unsupported, $"建置環境「{name}」不支援。"));
            }
        }
    }

    private static void ConvertBuilders(XElement? builders, JenkinsJobProposal job,
        List<JenkinsEnvironmentVariable> environment, List<JenkinsWarning> warnings)
    {
        var index = 0;
        foreach (var builder in builders?.Elements() ?? [])
        {
            index++;
            var name = builder.Name.LocalName;
            if (name == "hudson.tasks.Shell")
            {
                var command = Text(builder.Element("command")) ?? string.Empty;
                job.Steps.Add(Shell($"Shell {index}", command.Replace("\r\n", "\n").Trim('\n')));
            }
            else if (name == "hudson.tasks.BatchFile")
            {
                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Unsupported,
                    $"第 {index} 個建置步驟是 Windows 批次，已留成註解。"));
                job.Steps.Add(Shell($"Batch {index}",
                    "# TODO Windows 批次：\n" + string.Join('\n',
                        (Text(builder.Element("command")) ?? string.Empty).Split('\n').Select(line => "# " + line))));
            }
            else if (name.EndsWith("TriggerBuilder", StringComparison.Ordinal))
            {
                foreach (var project in builder.Descendants("projects").Select(Text)
                             .Where(value => !string.IsNullOrWhiteSpace(value)))
                    AddDownstream(job, project!, DownstreamWhen.Success);

                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Warning, "「觸發其他專案的建置」步驟改成完成後觸發下游任務。"));
            }
            else
            {
                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Unsupported, $"建置步驟「{name}」不支援，請改寫成 shell。"));
            }
        }
    }

    private static void ConvertPublishers(XElement? publishers, JenkinsJobProposal job, List<JenkinsWarning> warnings)
    {
        foreach (var publisher in publishers?.Elements() ?? [])
        {
            var name = publisher.Name.LocalName;
            if (name == "hudson.tasks.ArtifactArchiver")
            {
                var patterns = Text(publisher.Element("artifacts")) ?? string.Empty;
                job.ArtifactPath = string.Join('\n',
                    patterns.Split(',').Select(pattern => pattern.Trim()).Where(pattern => pattern.Length > 0));
            }
            else if (name == "hudson.tasks.BuildTrigger")
            {
                var children = Text(publisher.Element("childProjects")) ?? string.Empty;
                var threshold = Text(publisher.Element("threshold")?.Element("name")) ?? "SUCCESS";
                var when = threshold switch
                {
                    "FAILURE" => DownstreamWhen.Always,
                    "UNSTABLE" => DownstreamWhen.Always,
                    _ => DownstreamWhen.Success
                };
                foreach (var child in
                         children.Split(',').Select(child => child.Trim()).Where(child => child.Length > 0))
                    AddDownstream(job, child, when);
            }
            else if (name.Contains("parameterizedtrigger", StringComparison.Ordinal))
            {
                foreach (var config in publisher.Descendants("projects").Select(Text)
                             .Where(value => !string.IsNullOrWhiteSpace(value)))
                foreach (var child in config!.Split(',').Select(child => child.Trim()).Where(child => child.Length > 0))
                    AddDownstream(job, child, DownstreamWhen.Success);

                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Info,
                    "參數化觸發的下游任務已加入，但傳遞的參數沒有對應；下游會拿到 WALIS_UPSTREAM_* 變數。"));
            }
            else if (name.Contains("JUnitResultArchiver", StringComparison.Ordinal))
            {
                var results = Text(publisher.Element("testResults"));
                if (results is not null)
                    job.ArtifactPath = string.Join('\n',
                        new[] { job.ArtifactPath, results }.Where(value => !string.IsNullOrWhiteSpace(value)));

                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Info, "JUnit 報告沒有檢視器，檔案已加進產出路徑保存。"));
            }
            else if (name.Contains("Mailer", StringComparison.Ordinal) ||
                     name.Contains("ExtendedEmailPublisher", StringComparison.Ordinal) ||
                     name.Contains("Slack", StringComparison.Ordinal))
            {
                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Info, $"通知「{name}」已略過；請改用 Walis 的通知設定。"));
            }
            else
            {
                warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Unsupported, $"建置後動作「{name}」不支援。"));
            }
        }
    }

    private static void AddDownstream(JenkinsJobProposal job, string name, string when)
    {
        var targets = DownstreamJobs.Parse(job.DownstreamJson).ToList();
        if (targets.Any(target => target.JobName == name)) return;

        targets.Add(new DownstreamTarget { JobName = name, When = when });
        job.DownstreamJson = JsonSerializer.Serialize(targets, JsonOptions);
    }

    private static ActionStep Shell(string name, string command)
    {
        return new ActionStep
        {
            Id = Guid.NewGuid().ToString("N"),
            ActionId = "shell.run",
            Name = name,
            Enabled = true,
            RunWhen = StepRunCondition.Success,
            Parameters = new Dictionary<string, string?> { ["command"] = command }
        };
    }

    private static string? Text(XElement? element)
    {
        return element is null ? null : element.Value;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex ParameterName();

    [GeneratedRegex("version\\s*=\\s*['\"]1\\.1['\"]")]
    private static partial Regex XmlVersion();
}