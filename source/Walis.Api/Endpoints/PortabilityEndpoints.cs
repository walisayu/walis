using Walis.Core.Execution;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Walis.Api.Services;
using Walis.Core.Actions;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Scheduling;

namespace Walis.Api.Endpoints;

/// <summary>
///     Export and import of job definitions.
///     Walis jobs live in the database, which means changes take effect immediately with no PR needed to tweak one line of script;
///     the price is no code review and no travelling with the repo. Exporting to JSON is the compromise:
///     those who want version control can put the file into a repo, and there is a migration path when moving machines.
/// </summary>
public static class PortabilityEndpoints
{
    private const int FormatVersion = 1;
    public const int MaximumImportBytes = 1024 * 1024;
    public const int MaximumJobs = 200;
    public const int MaximumActions = 100;
    private const int MaximumActionsJsonLength = 262_144;

    public static IEndpointRouteBuilder MapPortabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var portability = app.MapGroup("/api/portability").WithTags("Portability");

        portability.MapGet("/export", async (
            HttpContext context,
            IJobRepository jobs,
            IActionRepository actions,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (!RequestAccess.IsAdmin(context))
                return Results.Json(new { message = "只有管理員可以匯出任務。" }, statusCode: StatusCodes.Status403Forbidden);

            var list = await jobs.ListJobsAsync(cancellationToken);
            list = [.. list.Where(job => JobAccess.CanView(RequestAccess.Role(context), job))];

            // Only custom actions are exported: built-in actions exist on every Walis instance.
            var custom = (await actions.ListAsync(cancellationToken))
                .Where(definition => !definition.IsBuiltIn && string.IsNullOrWhiteSpace(definition.PluginId))
                .ToList();

            // The export is every job's script in one file; taking it is worth a line in the trail.
            await audit.RecordAsync(AuditAction.Export, "job", null,
                $"匯出 {list.Count} 個任務與 {custom.Count} 個自訂動作", cancellationToken);

            return Results.Ok(new PortableBundle(
                FormatVersion,
                DateTime.UtcNow,
                [.. list.Select(PortableJob.From)],
                custom));
        });

        portability.MapPost("/import", async (
            ImportRequest request,
            HttpContext context,
            IJobRepository jobs,
            IActionRepository actions,
            PluginActionRendererFactory renderers,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (!RequestAccess.IsAdmin(context))
                return Results.Json(new { message = "只有管理員可以匯入任務。" }, statusCode: StatusCodes.Status403Forbidden);

            if (request.Bundle is null)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["bundle"] = ["請提供要匯入的內容。"]
                });

            if (request.Bundle.Version != FormatVersion)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["version"] = [$"這份檔案是格式 {request.Bundle.Version}，這個版本的 Walis 只讀得懂且只接受格式 {FormatVersion}。"]
                });

            if ((request.Bundle.Jobs?.Count ?? 0) > MaximumJobs ||
                (request.Bundle.Actions?.Count ?? 0) > MaximumActions)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["bundle"] = [$"一次最多匯入 {MaximumJobs} 個任務與 {MaximumActions} 個自訂動作。"]
                });

            var validationErrors = new Dictionary<string, string[]>();
            var normalizedActions = ValidateActions(request.Bundle.Actions ?? [], validationErrors);
            var definitions = (await actions.MapAsync(cancellationToken))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            foreach (var definition in normalizedActions) definitions[definition.Id] = definition;

            var renderer = await renderers.CreateAsync(cancellationToken: cancellationToken);
            var normalizedJobs = ValidateJobs(request.Bundle.Jobs ?? [], definitions, renderer, validationErrors);

            if (validationErrors.Count != 0) return Results.ValidationProblem(validationErrors);

            var existing = await jobs.ListJobsAsync(cancellationToken);
            // Older databases may hold two jobs with the same name (names were not unique before); the first one created wins,
            // the same rule as name resolution everywhere else, instead of the import failing with a server error.
            var byName = existing
                .GroupBy(job => job.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderBy(job => job.CreatedAt).First(), StringComparer.OrdinalIgnoreCase);

            var created = 0;
            var updated = 0;
            var skipped = 0;

            foreach (var definition in normalizedActions) await actions.InstallAsync(definition, cancellationToken);

            foreach (var portable in normalizedJobs)
            {
                if (byName.TryGetValue(portable.Name, out var current))
                {
                    if (!request.Overwrite)
                    {
                        skipped++;
                        continue;
                    }

                    // The middleware already restricts this to admins; the job ACL is checked again here so a future routing change cannot degrade it.
                    if (!JobAccess.CanView(RequestAccess.Role(context), current)) return Results.NotFound();

                    // Same rule as PUT /api/jobs: a trusted job (and its script) is changed by administrators only.
                    if (!JobAccess.CanModify(RequestAccess.Role(context), current))
                        return Results.Json(
                            new
                            {
                                message = current.TrustedUnsandboxed
                                    ? "只有管理員可以覆寫信任任務。"
                                    : $"這個任務需要 {current.RunRole} 以上的角色才能覆寫。"
                            },
                            statusCode: StatusCodes.Status403Forbidden);

                    await jobs.UpdateJobAsync(current.Id, portable.ToDraft(), cancellationToken);
                    updated++;
                    continue;
                }

                await jobs.CreateJobAsync(portable.ToDraft(), cancellationToken);
                created++;
            }

            await audit.RecordAsync(
                AuditAction.Create,
                "job",
                null,
                $"匯入任務：新增 {created}、更新 {updated}、略過 {skipped}",
                cancellationToken);

            return Results.Ok(new
            {
                created,
                updated,
                skipped,
                actions = normalizedActions.Count
            });
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumImportBytes));

        return app;
    }

    private static IReadOnlyList<ActionDefinition> ValidateActions(
        IReadOnlyList<ActionDefinition> actions,
        Dictionary<string, string[]> validationErrors)
    {
        var normalized = new List<ActionDefinition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < actions.Count; index++)
        {
            var key = $"bundle.actions[{index}]";

            try
            {
                var json = JsonSerializer.Serialize(actions[index], ActionManifest.SerializerOptions);
                if (!ActionManifest.TryParse(json, out var definition, out var errors))
                {
                    validationErrors[key] = [.. errors];
                    continue;
                }

                if (!ids.Add(definition!.Id))
                {
                    validationErrors[key] = [$"動作 {definition.Id} 重複出現。"];
                    continue;
                }

                normalized.Add(definition);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException
                                                  or NullReferenceException)
            {
                validationErrors[key] = ["動作定義格式不完整或無效。"];
            }
        }

        return normalized;
    }

    private static IReadOnlyList<PortableJob> ValidateJobs(
        IReadOnlyList<PortableJob> jobs,
        IReadOnlyDictionary<string, ActionDefinition> definitions,
        IActionRenderer renderer,
        Dictionary<string, string[]> validationErrors)
    {
        var normalized = new List<PortableJob>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < jobs.Count; index++)
        {
            var portable = jobs[index];
            var prefix = $"bundle.jobs[{index}]";

            if (portable is null)
            {
                validationErrors[prefix] = ["任務定義不可為空。"];
                continue;
            }

            var valid = true;

            void Reject(string field, string message)
            {
                validationErrors[$"{prefix}.{field}"] = [message];
                valid = false;
            }

            void RejectIfTooLong(string field, string? value, int maximum, string label)
            {
                if (value?.Length > maximum) Reject(field, $"{label}不可超過 {maximum:N0} 個字元。");
            }

            if (string.IsNullOrWhiteSpace(portable.Name))
                Reject("name", "請輸入任務名稱。");
            else if (portable.Name.Length > 80)
                Reject("name", "任務名稱不可超過 80 個字元。");
            else if (!names.Add(portable.Name)) Reject("name", $"任務 {portable.Name} 重複出現。");

            RejectIfTooLong("project", portable.Project, 200, "專案名稱");
            RejectIfTooLong("description", portable.Description, 10_000, "說明");
            RejectIfTooLong("repository", portable.Repository, 500, "儲存庫");

            // Same as the job form: credentials in a repository URL end up in logs, .git/config and every later export.
            if (RepositoryCredentials.HasUserInfo(portable.Repository))
                Reject("repository", "儲存庫網址不可內嵌帳號密碼，請改用機密環境變數。");
            RejectIfTooLong("branch", portable.Branch, 200, "分支");
            RejectIfTooLong("runnerName", portable.RunnerName, 60, "執行器名稱");
            RejectIfTooLong("workingDirectory", portable.WorkingDirectory, 4_096, "工作目錄");
            RejectIfTooLong("artifactPath", portable.ArtifactPath, 32_768, "產出路徑");
            RejectIfTooLong("cachePaths", portable.CachePaths, 32_768, "快取路徑");
            RejectIfTooLong("cron", portable.Cron, 120, "Cron");

            if (portable.ViewRole is not null && !UserRole.IsValid(portable.ViewRole))
                Reject("viewRole", "檢視角色只能是 admin、operator 或 viewer。");

            if (portable.RunRole is not null && !UserRole.IsValid(portable.RunRole))
                Reject("runRole", "執行角色只能是 admin、operator 或 viewer。");
            else if (portable.RunRole == UserRole.Viewer)
                Reject("runRole", "執行角色最低是 operator。");

            if ((portable.ViewRole is null || UserRole.IsValid(portable.ViewRole)) &&
                (portable.RunRole is null || UserRole.IsValid(portable.RunRole)) &&
                JobAccess.Rank(JobAccess.NormalizeViewRole(portable.ViewRole)) >
                JobAccess.Rank(JobAccess.NormalizeRunRole(portable.RunRole)))
                Reject("roles", "執行角色不可低於檢視角色。");

            if (!ScriptModes.IsValid(portable.ScriptMode)) Reject("scriptMode", "腳本模式只能是 source 或 actions。");

            if (!TriggerMode.IsValid(portable.TriggerKind))
                Reject("triggerKind", "觸發方式只能是 manual、push 或 cron。");
            else if (portable.TriggerKind == TriggerMode.Cron && portable.Cron?.Length <= 120 &&
                     !CronExpression.TryParse(portable.Cron, out _, out var cronError))
                Reject("cron", cronError!);
            else if (portable.TriggerKind == TriggerMode.Push && string.IsNullOrWhiteSpace(portable.Repository))
                Reject("repository", "推送觸發任務必須指定儲存庫，避免其他儲存庫的同名分支誤觸發。");

            if (portable.TimeoutMinutes is < 1 or > 1440) Reject("timeoutMinutes", "逾時時間需介於 1 到 1440 分鐘。");

            if (!BuildMatrix.IsValid(portable.MatrixJson, out var matrixError)) Reject("matrixJson", matrixError!);

            if (!BuildParameters.IsValid(portable.ParametersJson, out var parametersError))
                Reject("parametersJson", parametersError!);

            if (!DownstreamJobs.IsValid(portable.DownstreamJson, portable.Name, out var downstreamError))
                Reject("downstreamJson", downstreamError!);

            if (portable.ScriptMode == ScriptModes.Actions)
            {
                List<ActionStep>? steps = null;
                if (portable.ActionsJson?.Length > MaximumActionsJsonLength)
                    Reject("actionsJson", $"動作設定不可超過 {MaximumActionsJsonLength:N0} 個字元。");
                else
                    try
                    {
                        steps = string.IsNullOrWhiteSpace(portable.ActionsJson)
                            ? null
                            : JsonSerializer.Deserialize<List<ActionStep>>(portable.ActionsJson,
                                ActionManifest.SerializerOptions);
                    }
                    catch (JsonException)
                    {
                        Reject("actionsJson", "動作流程不是有效的 JSON。");
                    }

                if (steps is null || steps.Count == 0)
                {
                    Reject("actionsJson", "請至少加入一個動作。");
                }
                else if (steps.Count > 100)
                {
                    Reject("actionsJson", "一個任務最多 100 個動作。");
                }
                else if (!ValidateActionSteps(steps, out var stepError))
                {
                    Reject("actionsJson", stepError!);
                }
                else
                {
                    var compiled = ActionScriptCompiler.Compile(steps, definitions, renderer);
                    if (compiled.Errors.Count != 0)
                    {
                        validationErrors[$"{prefix}.actionsJson"] = [.. compiled.Errors];
                        valid = false;
                    }
                    else
                    {
                        if (compiled.Script.Length > 20_000)
                            Reject("actionsJson", "動作產生的命令不可超過 20,000 個字元。");
                        else
                            portable = portable with { Command = compiled.Script };
                    }
                }
            }
            else if (string.IsNullOrWhiteSpace(portable.Command))
            {
                Reject("command", "請輸入要執行的命令。");
            }
            else if (portable.Command.Length > 20_000)
            {
                Reject("command", "命令不可超過 20,000 個字元。");
            }

            if (valid) normalized.Add(portable);
        }

        return normalized;
    }

    private static bool ValidateActionSteps(IReadOnlyList<ActionStep> steps, out string? error)
    {
        error = null;
        long characters = 0;

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step is null)
            {
                error = $"第 {index + 1} 個動作不可為空。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(step.ActionId) ||
                (step.Id?.Length ?? 0) > 120 || step.ActionId.Length > 120 || step.Name?.Length > 200)
            {
                error = $"第 {index + 1} 個動作的識別碼或名稱過長。";
                return false;
            }

            if (step.Parameters is null || step.Parameters.Count > 64)
            {
                error = $"第 {index + 1} 個動作最多 64 個參數。";
                return false;
            }

            characters += (step.Id?.Length ?? 0) + step.ActionId.Length + (step.Name?.Length ?? 0);
            foreach (var (name, value) in step.Parameters)
            {
                if (name.Length > 128 || value?.Length > 4_096)
                {
                    error = $"第 {index + 1} 個動作的參數名稱或內容過長。";
                    return false;
                }

                characters += name.Length + (value?.Length ?? 0);
            }

            if (characters > MaximumActionsJsonLength)
            {
                error = $"動作設定不可超過 {MaximumActionsJsonLength:N0} 個字元。";
                return false;
            }
        }

        return true;
    }
}

public sealed record PortableBundle(
    int Version,
    DateTime ExportedAt,
    IReadOnlyList<PortableJob> Jobs,
    IReadOnlyList<ActionDefinition> Actions);

/// <summary>
///     The portable job format. Deliberately excludes ids, build statistics and timestamps, which are this machine's state
///     and would only confuse the importing side; matching is by name.
/// </summary>
public sealed record PortableJob(
    string Name,
    string? Project,
    string? Description,
    string? Repository,
    string Branch,
    string? RunnerName,
    string? WorkingDirectory,
    string Command,
    int TimeoutMinutes,
    string? ArtifactPath,
    string TriggerKind,
    string? Cron,
    bool NotifyOnFailure,
    bool IsDisabled,
    string ScriptMode,
    [property: JsonConverter(typeof(RawJsonConverter))]
    string? ActionsJson,
    string? CachePaths = null,
    [property: JsonConverter(typeof(RawJsonConverter))]
    string? MatrixJson = null,
    string? ViewRole = null,
    string? RunRole = null,
    [property: JsonConverter(typeof(RawJsonConverter))]
    string? ParametersJson = null,
    bool AllowConcurrentBuilds = true,
    [property: JsonConverter(typeof(RawJsonConverter))]
    string? DownstreamJson = null,
    bool? CleanWorkspace = null,
    bool DenyNetwork = false,
    bool TrustedUnsandboxed = false,
    bool? CleanWorkspaceAfterBuild = null)
{
    public static PortableJob From(Job job)
    {
        return new PortableJob(
            job.Name,
            job.Project,
            job.Description,
            job.Repository,
            job.Branch,
            job.RunnerName,
            job.WorkingDirectory,
            job.Command,
            job.TimeoutMinutes,
            job.ArtifactPath,
            job.TriggerKind,
            job.Cron,
            job.NotifyOnFailure,
            job.IsDisabled,
            job.ScriptMode,
            job.ActionsJson,
            job.CachePaths,
            job.MatrixJson,
            job.ViewRole,
            job.RunRole,
            job.ParametersJson,
            job.AllowConcurrentBuilds,
            job.DownstreamJson,
            job.CleanWorkspace,
            job.DenyNetwork,
            job.TrustedUnsandboxed,
            job.CleanWorkspaceAfterBuild);
    }

    public JobDraft ToDraft()
    {
        return new JobDraft(
            Name,
            Project,
            Description,
            Repository,
            Branch,
            RunnerName,
            WorkingDirectory,
            Command,
            TimeoutMinutes,
            ArtifactPath,
            TriggerKind,
            Cron,
            NotifyOnFailure,
            IsDisabled,
            ScriptMode,
            ActionsJson,
            CachePaths,
            MatrixJson,
            ViewRole,
            RunRole,
            ParametersJson,
            AllowConcurrentBuilds,
            DownstreamJson,
            CleanWorkspace,
            DenyNetwork,
            TrustedUnsandboxed,
            CleanWorkspaceAfterBuild);
    }
}

public sealed record ImportRequest(PortableBundle? Bundle, bool Overwrite);

/// <summary>The action flow is JSON itself; it appears as an object in the export so it is readable and reviewable.</summary>
public sealed class RawJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType == JsonTokenType.String) return reader.GetString();

        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            writer.WriteNullValue();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(value);
        }
    }
}