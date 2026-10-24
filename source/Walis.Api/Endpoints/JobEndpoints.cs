using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Walis.Api.Services;
using Walis.Core.Actions;
using Walis.Core.Execution;
using Walis.Core.Jenkins;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Scheduling;

namespace Walis.Api.Endpoints;

public static class JobEndpoints
{
    private const int MaximumRequestBytes = 1024 * 1024;
    private const int MaximumCommandLength = 20_000;
    private const int MaximumActions = 100;
    private const int MaximumActionParameters = 64;
    private const int MaximumActionsJsonLength = 262_144;

    /// <summary>Set on a 200 response when the trigger matched an existing external reference and no new build was created.</summary>
    public const string DeduplicatedHeader = "Walis-Deduplicated";

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/api/jobs")
            .WithTags("Jobs")
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBytes));

        jobs.MapGet("/", async (
            HttpContext context,
            IJobRepository repository,
            ISettingsRepository settings,
            CancellationToken cancellationToken) =>
        {
            var role = CurrentRole(context);
            var list = await repository.ListJobsAsync(cancellationToken);
            list = [.. list.Where(job => JobAccess.CanView(role, job))];
            var configuration = await settings.GetAsync(cancellationToken);
            JobScheduleEnricher.Apply(list, TimeZoneResolver.Resolve(configuration.TimeZone));
            return Results.Ok(list);
        });

        // The paged query for the list page: with thousands of jobs the whole set cannot be sent to the frontend. Permissions are filtered in SQL,
        // and the statistics (total, enabled, project list) are limited only by permissions, not by the filter.
        jobs.MapGet("/page", async (
            string? search,
            string? status,
            string? trigger,
            string? project,
            string? sort,
            string? order,
            int? page,
            int? pageSize,
            HttpContext context,
            IJobRepository repository,
            ISettingsRepository settings,
            CancellationToken cancellationToken) =>
        {
            var role = CurrentRole(context);
            var visibleRoles = new[] { UserRole.Viewer, UserRole.Operator, UserRole.Admin }
                .Where(viewRole => JobAccess.CanView(role, new Job { ViewRole = viewRole }))
                .ToArray();

            var result = await repository.QueryJobsAsync(
                new JobQuery(
                    search,
                    status,
                    trigger,
                    project,
                    visibleRoles,
                    JobSort.Normalize(sort),
                    string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase),
                    page ?? 1,
                    pageSize ?? JobQuery.DefaultPageSize),
                cancellationToken);

            var configuration = await settings.GetAsync(cancellationToken);
            JobScheduleEnricher.Apply(result.Items, TimeZoneResolver.Resolve(configuration.TimeZone));
            return Results.Ok(result);
        });

        // Import from Jenkins: converts and previews only, never writes to the database; the user confirms in the editor and saves through the normal create job.
        jobs.MapPost("/import/jenkins", (JenkinsImportRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["content"] = ["請貼上 Jenkinsfile 或 config.xml 的內容。"] });

            if (request.Content.Length > JenkinsImporter.MaximumLength)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["content"] = [$"內容不可超過 {JenkinsImporter.MaximumLength / 1024} KB。"] });

            if (request.Name?.Length > 80)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["任務名稱不可超過 80 個字元。"] });

            var result = JenkinsImporter.Import(request.Content, request.Name);
            return Results.Ok(new
            {
                result.Format,
                job = new
                {
                    result.Job.Name,
                    result.Job.Description,
                    result.Job.Repository,
                    result.Job.Branch,
                    result.Job.RunnerName,
                    result.Job.TimeoutMinutes,
                    result.Job.TriggerKind,
                    result.Job.Cron,
                    result.Job.ArtifactPath,
                    result.Job.MatrixJson,
                    result.Job.ParametersJson,
                    result.Job.AllowConcurrentBuilds,
                    result.Job.DownstreamJson,
                    result.Job.IsDisabled,
                    actions = result.Job.Steps
                },
                environment = result.Environment,
                warnings = result.Warnings
            });
        }).WithMetadata(new RequestSizeLimitAttribute(JenkinsImporter.MaximumLength * 2));

        jobs.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext context,
            IJobRepository repository,
            ISettingsRepository settings,
            CancellationToken cancellationToken) =>
        {
            var job = await repository.GetJobAsync(id, cancellationToken);
            if (job is null) return Results.NotFound();

            // Invisible jobs always get 404, revealing nothing.
            if (!JobAccess.CanView(CurrentRole(context), job)) return Results.NotFound();

            var configuration = await settings.GetAsync(cancellationToken);
            JobScheduleEnricher.Apply([job], TimeZoneResolver.Resolve(configuration.TimeZone));
            return Results.Ok(job);
        });

        jobs.MapGet("/{id:guid}/builds", async (
            Guid id,
            int? limit,
            HttpContext context,
            IJobRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (await LoadVisibleAsync(id, context, repository, cancellationToken) is null) return Results.NotFound();

            return Results.Ok(await repository.ListBuildsAsync(new BuildQuery(null, null, id, null, limit ?? 20),
                cancellationToken));
        });

        jobs.MapPost("/", async (
            JobRequest request,
            HttpContext context,
            IJobRepository repository,
            IActionRepository actions,
            PluginActionRendererFactory renderers,
            AuditService audit,
            IRunnerRepository runners,
            CancellationToken cancellationToken) =>
        {
            var inputErrors = ValidateInputBounds(request);
            if (inputErrors.Count != 0) return Results.ValidationProblem(inputErrors);

            var draft = await BuildDraftAsync(request, actions, renderers, cancellationToken);
            var errors = Validate(request, draft.Draft, draft.Errors);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            if (CheckRequestedRole(request, context) is { } denied) return denied;

            if (await CheckPlacementAsync(null, draft.Draft, context, repository, runners, cancellationToken) is { } misplaced)
                return misplaced;

            var job = await repository.CreateJobAsync(draft.Draft, cancellationToken);
            await audit.RecordAsync(AuditAction.Create, "job", job.Id.ToString(), $"建立任務 {job.Name}",
                cancellationToken);
            return Results.Created($"/api/jobs/{job.Id}", job);
        });

        jobs.MapPut("/{id:guid}", async (
            Guid id,
            JobRequest request,
            HttpContext context,
            IJobRepository repository,
            IActionRepository actions,
            PluginActionRendererFactory renderers,
            AuditService audit,
            IRunnerRepository runners,
            CancellationToken cancellationToken) =>
        {
            if (await CheckModifyAsync(id, context, repository, cancellationToken) is { } denied) return denied;

            var inputErrors = ValidateInputBounds(request);
            if (inputErrors.Count != 0) return Results.ValidationProblem(inputErrors);

            var draft = await BuildDraftAsync(request, actions, renderers, cancellationToken);
            var errors = Validate(request, draft.Draft, draft.Errors);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            if (CheckRequestedRole(request, context) is { } roleDenied) return roleDenied;

            if (await CheckPlacementAsync(id, draft.Draft, context, repository, runners, cancellationToken) is { } misplaced)
                return misplaced;

            var job = await repository.UpdateJobAsync(id, draft.Draft, cancellationToken);

            if (job is not null)
                await audit.RecordAsync(AuditAction.Update, "job", job.Id.ToString(), $"更新任務 {job.Name}",
                    cancellationToken);

            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        jobs.MapPatch("/{id:guid}/enabled", async (
            Guid id,
            EnabledRequest request,
            HttpContext context,
            IJobRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (await CheckModifyAsync(id, context, repository, cancellationToken) is { } denied) return denied;

            var job = await repository.SetJobEnabledAsync(id, request.Enabled, cancellationToken);

            if (job is not null)
                await audit.RecordAsync(
                    AuditAction.Update,
                    "job",
                    job.Id.ToString(),
                    $"{(request.Enabled ? "啟用" : "暫停")}任務 {job.Name}",
                    cancellationToken);

            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        jobs.MapDelete("/{id:guid}", async (
            Guid id,
            HttpContext context,
            IJobRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (await CheckModifyAsync(id, context, repository, cancellationToken) is { } denied) return denied;

            var job = await repository.GetJobAsync(id, cancellationToken);

            if (!await repository.DeleteJobAsync(id, cancellationToken)) return Results.NotFound();

            await audit.RecordAsync(AuditAction.Delete, "job", id.ToString(), $"刪除任務 {job?.Name ?? id.ToString()}",
                cancellationToken);
            return Results.NoContent();
        });

        jobs.MapPost("/{id:guid}/builds", async (
            Guid id,
            BuildRequest? request,
            HttpContext context,
            IJobRepository repository,
            BuildLauncher launcher,
            SecurityEventService securityEvents,
            CancellationToken cancellationToken) =>
        {
            var target = await repository.GetJobAsync(id, cancellationToken);

            if (target is null) return Results.NotFound();

            var role = CurrentRole(context);

            if (!JobAccess.CanView(role, target)) return Results.NotFound();

            if (!JobAccess.CanRun(role, target))
            {
                await securityEvents.AccessDeniedAsync(context, AccessDeniedReason.JobRunRole,
                    $"角色 {role} 不足以執行任務 {target.Name}（需要 {target.RunRole}）", cancellationToken: cancellationToken);
                return Results.Json(
                    new { message = $"這個任務需要 {target.RunRole} 以上的角色才能執行。" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (request?.TriggeredBy is { } triggeredBy &&
                (triggeredBy.Length > 200 || triggeredBy.Any(char.IsControl)))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["triggeredBy"] = ["觸發來源不可超過 200 個字元或包含控制字元。"]
                });

            if (request?.Parameters is { } supplied &&
                (supplied.Count > BuildParameters.MaximumCount ||
                 supplied.Any(pair =>
                     pair.Key.Length > 64 || (pair.Value?.Length ?? 0) > BuildParameters.MaximumValueLength)))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["parameters"] =
                        [$"參數最多 {BuildParameters.MaximumCount} 個，值不可超過 {BuildParameters.MaximumValueLength:N0} 個字元。"]
                });

            if (ResolveExternalRef(request?.ExternalRef, context.Request.Headers[BuildExternalRefs.HeaderName],
                    out var externalRef) is { } externalRefError)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["externalRef"] = [externalRefError]
                });

            // schedule, push, mcp and downstream:<job> are written by the server only. What a caller sends is kept as a note
            // under the manual prefix, so it can label the trigger without impersonating another source in the audit log.
            var source = string.IsNullOrWhiteSpace(request?.TriggeredBy) || request.TriggeredBy.Trim() == "manual"
                ? "manual"
                : $"manual:{request.TriggeredBy.Trim()}";

            var result = await launcher.LaunchAsync(
                id,
                source,
                cancellationToken,
                new BuildLaunchOptions { Parameters = request?.Parameters, ExternalRef = externalRef });

            if (result.JobNotFound) return Results.NotFound();

            if (result.Error is not null)
                return Results.Conflict(result.ErrorCode is null
                    ? new { message = result.Error }
                    : new { message = result.Error, code = result.ErrorCode });

            // A retry with the same reference returns the original build; BuildLauncher audited it the first time and skips the retry.
            if (result.Deduplicated)
            {
                context.Response.Headers[DeduplicatedHeader] = "true";
                return Results.Ok(result.Build);
            }

            return Results.Accepted($"/api/builds/{result.Build!.Id}", result.Build);
        });

        return app;
    }

    /// <summary>
    ///     The idempotency reference may come from the body (externalRef) or the Idempotency-Key header; both together must agree.
    ///     Returns an error message, or null with <paramref name="externalRef" /> set (null when neither was given).
    /// </summary>
    private static string? ResolveExternalRef(string? body, StringValues header, out string? externalRef)
    {
        externalRef = null;

        if (header.Count > 1) return $"{BuildExternalRefs.HeaderName} 標頭只能有一個值。";

        var fromHeader = header.Count == 1 ? header[0] : null;

        if (body is not null && fromHeader is not null && !string.Equals(body, fromHeader, StringComparison.Ordinal))
            return $"externalRef 與 {BuildExternalRefs.HeaderName} 標頭不一致。";

        var candidate = body ?? fromHeader;
        if (candidate is null) return null;

        if (!BuildExternalRefs.IsValid(candidate))
            return $"externalRef 需為 1 到 {BuildExternalRefs.MaximumLength} 個字元，且不可為空白或包含控制字元。";

        externalRef = candidate;
        return null;
    }

    /// <summary>Falls back to the lowest viewer permission without a valid session.</summary>
    private static string CurrentRole(HttpContext context)
    {
        return RequestAccess.Role(context);
    }

    // RunRole also limits the permission of automatic downstream triggers; an operator must not be able to raise a job they can edit to admin.
    private static IResult? CheckRequestedRole(JobRequest request, HttpContext context)
    {
        if (JobAccess.Rank(JobAccess.NormalizeRunRole(request.RunRole)) > JobAccess.Rank(CurrentRole(context)))
            return Results.Json(new { message = "不能將任務執行權限設為高於自己的角色。" },
                statusCode: StatusCodes.Status403Forbidden);

        // A trusted job runs outside the sandbox with the service account's full access; an operator saving one, or keeping the
        // flag on an existing one, would be the exact bypass the sandbox exists to prevent.
        if (request.TrustedUnsandboxed && !RequestAccess.IsAdmin(context))
            return Results.Json(new { message = "只有管理員可以把任務設為信任任務（不套用沙箱）。" },
                statusCode: StatusCodes.Status403Forbidden);

        return null;
    }

    /// <summary>Invisible jobs always return null and the caller responds 404, revealing nothing.</summary>
    private static async Task<Job?> LoadVisibleAsync(
        Guid id,
        HttpContext context,
        IJobRepository repository,
        CancellationToken cancellationToken)
    {
        var job = await repository.GetJobAsync(id, cancellationToken);
        return job is not null && JobAccess.CanView(CurrentRole(context), job) ? job : null;
    }

    /// <summary>
    ///     Modifying, disabling or deleting a job: invisible ones get 404; visible but not runnable get 403.
    ///     The threshold is "can run" rather than "can view", otherwise an operator could lower the run role of an admin-only job and run it.
    /// </summary>
    /// <summary>
    ///     Where a job may go, checked on every save:
    ///     <list type="bullet">
    ///         <item>its name is unique (downstream triggers, MCP and imports find jobs by name);</item>
    ///         <item>a remote runner reserved for administrator jobs (<see cref="Runner.MinimumRunRole" />) only takes jobs that an
    ///         administrator saves with that run role, because remote runners run without Walis's sandbox;</item>
    ///         <item>a custom working directory must not overlap the directory of a job the caller cannot modify: otherwise an
    ///         operator could read an administrator's checkout or plant .git/hooks that a trusted job then runs.</item>
    ///     </list>
    /// </summary>
    private static async Task<IResult?> CheckPlacementAsync(
        Guid? id,
        JobDraft draft,
        HttpContext context,
        IJobRepository repository,
        IRunnerRepository runners,
        CancellationToken cancellationToken)
    {
        var role = CurrentRole(context);
        var others = (await repository.ListJobsAsync(cancellationToken)).Where(job => job.Id != id).ToList();

        if (others.Any(job => string.Equals(job.Name.Trim(), draft.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new { message = "任務名稱已被使用，請換一個名稱。" });

        if (!string.IsNullOrWhiteSpace(draft.RunnerName) &&
            !string.Equals(draft.RunnerName.Trim(), Runner.LocalName, StringComparison.OrdinalIgnoreCase) &&
            await runners.GetByNameAsync(draft.RunnerName.Trim(), cancellationToken) is { } runner &&
            (JobAccess.Rank(role) < JobAccess.Rank(runner.MinimumRunRole) ||
             JobAccess.Rank(JobAccess.NormalizeRunRole(draft.RunRole)) < JobAccess.Rank(runner.MinimumRunRole)))
            return Results.Json(
                new { message = $"執行器「{runner.Name}」只接受執行角色 {runner.MinimumRunRole} 以上、且由該角色設定的任務。" },
                statusCode: StatusCodes.Status403Forbidden);

        if (!string.IsNullOrWhiteSpace(draft.WorkingDirectory) && role != UserRole.Admin)
        {
            var clash = others.FirstOrDefault(job =>
                !string.IsNullOrWhiteSpace(job.WorkingDirectory) &&
                !JobAccess.CanModify(role, job) &&
                !BuildIsolation.IsDisjointFrom(draft.WorkingDirectory, [job.WorkingDirectory]));

            if (clash is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["workingDirectory"] = ["這個工作目錄與另一個你無法修改的任務重疊，請改用其他目錄或留空使用系統管理的目錄。"]
                });
        }

        return null;
    }

    private static async Task<IResult?> CheckModifyAsync(
        Guid id,
        HttpContext context,
        IJobRepository repository,
        CancellationToken cancellationToken)
    {
        var job = await LoadVisibleAsync(id, context, repository, cancellationToken);

        if (job is null) return Results.NotFound();

        if (!JobAccess.CanModify(CurrentRole(context), job))
            return Results.Json(
                new { message = job.TrustedUnsandboxed ? "只有管理員可以修改信任任務。" : $"這個任務需要 {job.RunRole} 以上的角色才能修改。" },
                statusCode: StatusCodes.Status403Forbidden);

        return null;
    }

    /// <summary>Flow-mode jobs compile their actions into a script stored in Command, so the executor never needs to know actions exist.</summary>
    private static async Task<(JobDraft Draft, IReadOnlyList<string> Errors)> BuildDraftAsync(
        JobRequest request,
        IActionRepository actions,
        PluginActionRendererFactory renderers,
        CancellationToken cancellationToken)
    {
        if (request.ScriptMode != ScriptModes.Actions)
            return (request.ToDraft(request.Command, SerializeActions(request.Actions)), []);

        var steps = request.Actions ?? [];
        var definitions = await actions.MapAsync(cancellationToken);
        var renderer = await renderers.CreateAsync(cancellationToken: cancellationToken);
        var compiled = ActionScriptCompiler.Compile(steps, definitions, renderer);

        return (request.ToDraft(compiled.Script, SerializeActions(steps)), compiled.Errors);
    }

    private static string? SerializeActions(List<ActionStep>? steps)
    {
        return steps is null || steps.Count == 0
            ? null
            : JsonSerializer.Serialize(steps, ActionManifest.SerializerOptions);
    }

    private static Dictionary<string, string[]> Validate(
        JobRequest request,
        JobDraft draft,
        IReadOnlyList<string> compileErrors)
    {
        var errors = new Dictionary<string, string[]>();

        if (compileErrors.Count != 0) errors["actions"] = [.. compileErrors];

        // Without scriptMode the request is treated as a directly edited script, keeping the old API compatible.
        if (request.ScriptMode is not null && !ScriptModes.IsValid(request.ScriptMode))
            errors["scriptMode"] = ["腳本模式只能是 source 或 actions。"];

        if (string.IsNullOrWhiteSpace(request.Name))
            errors["name"] = ["請輸入任務名稱。"];
        else if (request.Name.Length > 80) errors["name"] = ["任務名稱不可超過 80 個字元。"];
        else if (AuditText.HasControlCharacters(request.Name)) errors["name"] = ["任務名稱不可包含控制字元。"];

        if (request.ScriptMode == ScriptModes.Actions)
        {
            if ((request.Actions?.Count ?? 0) == 0) errors["actions"] = ["請至少加入一個動作。"];
        }
        else if (string.IsNullOrWhiteSpace(request.Command))
        {
            errors["command"] = ["請輸入要執行的命令。"];
        }
        else if (request.Command.Length > MaximumCommandLength)
        {
            errors["command"] = [$"命令不可超過 {MaximumCommandLength:N0} 個字元。"];
        }

        if (draft.Command.Length > MaximumCommandLength)
            errors["command"] = [$"產生的命令不可超過 {MaximumCommandLength:N0} 個字元。"];

        if (draft.ActionsJson?.Length > MaximumActionsJsonLength)
            errors["actions"] = [$"動作設定不可超過 {MaximumActionsJsonLength:N0} 個字元。"];

        if (!TriggerMode.IsValid(request.TriggerKind))
            errors["triggerKind"] = ["觸發方式只能是 manual、push 或 cron。"];
        else if (request.TriggerKind == TriggerMode.Cron &&
                 !CronExpression.TryParse(request.Cron, out _, out var cronError))
            errors["cron"] = [cronError!];
        else if (request.TriggerKind == TriggerMode.Push && string.IsNullOrWhiteSpace(request.Repository))
            errors["repository"] = ["推送觸發任務必須指定儲存庫，避免其他儲存庫的同名分支誤觸發。"];

        // The address goes into WALIS_REPOSITORY, the build log, .git/config and the export file, all readable by viewers.
        if (RepositoryCredentials.HasUserInfo(request.Repository))
            errors["repository"] =
            [
                "儲存庫網址不可內嵌帳號或權杖（例如 https://user:token@host/repo.git），它會出現在建置日誌、工作目錄與匯出檔。" +
                "私有儲存庫請把權杖放進這個任務的機密環境變數，再用 git -c credential.helper 提供；作法見 docs/actions.md 的「私有儲存庫」。"
            ];

        if (request.TimeoutMinutes is < 1 or > 1440) errors["timeoutMinutes"] = ["逾時時間需介於 1 到 1440 分鐘。"];

        if (!BuildMatrix.IsValid(request.MatrixJson, out var matrixError)) errors["matrixJson"] = [matrixError!];

        if (!BuildParameters.IsValid(request.ParametersJson, out var parametersError))
            errors["parametersJson"] = [parametersError!];

        if (!DownstreamJobs.IsValid(request.DownstreamJson, request.Name, out var downstreamError))
            errors["downstreamJson"] = [downstreamError!];

        return errors;
    }

    /// <summary>
    ///     Cap every input that can amplify before action compilation and JSON serialization. Otherwise tens of MiB of parameters would first be
    ///     expanded by the plugin renderer into an even larger script and only be rejected when the command length is validated.
    /// </summary>
    private static Dictionary<string, string[]> ValidateInputBounds(JobRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        CheckLength(errors, "project", request.Project, 200, "專案名稱");
        CheckLength(errors, "description", request.Description, 10_000, "說明");
        CheckLength(errors, "repository", request.Repository, 500, "儲存庫");
        CheckLength(errors, "branch", request.Branch, 200, "分支");
        CheckLength(errors, "runnerName", request.RunnerName, 60, "執行器名稱");
        CheckLength(errors, "workingDirectory", request.WorkingDirectory, 4_096, "工作目錄");
        CheckLength(errors, "artifactPath", request.ArtifactPath, 32_768, "產出路徑");
        CheckLength(errors, "cachePaths", request.CachePaths, 32_768, "快取路徑");
        CheckLength(errors, "cron", request.Cron, 120, "Cron");
        CheckLength(errors, "parametersJson", request.ParametersJson, BuildParameters.MaximumJsonLength, "建置參數");
        CheckLength(errors, "downstreamJson", request.DownstreamJson, DownstreamJobs.MaximumJsonLength, "下游任務");

        if (request.ViewRole is not null && !UserRole.IsValid(request.ViewRole))
            errors["viewRole"] = ["檢視角色只能是 admin、operator 或 viewer。"];

        // Viewer is read-only everywhere else; a job must not be able to lower its run threshold below operator.
        if (request.RunRole is not null && !JobAccess.IsValidRunRole(request.RunRole))
            errors["runRole"] = ["執行角色只能是 admin 或 operator；viewer 僅能檢視，不能執行。"];

        if ((request.ViewRole is null || UserRole.IsValid(request.ViewRole)) &&
            (request.RunRole is null || JobAccess.IsValidRunRole(request.RunRole)) &&
            JobAccess.Rank(JobAccess.NormalizeViewRole(request.ViewRole)) >
            JobAccess.Rank(JobAccess.NormalizeRunRole(request.RunRole)))
            errors["roles"] = ["執行角色不可低於檢視角色。"];

        if (request.Actions is null) return errors;

        if (request.Actions.Count > MaximumActions)
        {
            errors["actions"] = [$"一個任務最多 {MaximumActions} 個動作。"];
            return errors;
        }

        long characters = 0;
        for (var index = 0; index < request.Actions.Count; index++)
        {
            var step = request.Actions[index];
            if (step is null)
            {
                errors["actions"] = [$"第 {index + 1} 個動作不可為空。"];
                return errors;
            }

            if (string.IsNullOrWhiteSpace(step.ActionId) ||
                (step.Id?.Length ?? 0) > 120 || step.ActionId.Length > 120 || step.Name?.Length > 200)
            {
                errors["actions"] = [$"第 {index + 1} 個動作的識別碼或名稱過長。"];
                return errors;
            }

            characters += (step.Id?.Length ?? 0) + step.ActionId.Length + (step.Name?.Length ?? 0);
            var parameters = step.Parameters;
            if (parameters is null || parameters.Count > MaximumActionParameters)
            {
                errors["actions"] = [$"第 {index + 1} 個動作最多 {MaximumActionParameters} 個參數。"];
                return errors;
            }

            foreach (var (name, value) in parameters)
            {
                if (name.Length > 128 || value?.Length > 4_096)
                {
                    errors["actions"] = [$"第 {index + 1} 個動作的參數名稱或內容過長。"];
                    return errors;
                }

                characters += name.Length + (value?.Length ?? 0);
            }

            if (characters > MaximumActionsJsonLength)
            {
                errors["actions"] = [$"動作設定不可超過 {MaximumActionsJsonLength:N0} 個字元。"];
                return errors;
            }
        }

        return errors;
    }

    private static void CheckLength(
        IDictionary<string, string[]> errors,
        string key,
        string? value,
        int maximum,
        string label)
    {
        if (value?.Length > maximum) errors[key] = [$"{label}不可超過 {maximum:N0} 個字元。"];
    }
}

public sealed record JobRequest(
    string? Name,
    string? Project,
    string? Description,
    string? Repository,
    string? Branch,
    string? RunnerName,
    string? WorkingDirectory,
    string? Command,
    int TimeoutMinutes,
    string? ArtifactPath,
    string? TriggerKind,
    string? Cron,
    bool NotifyOnFailure,
    bool IsDisabled,
    string? ScriptMode,
    List<ActionStep>? Actions,
    string? CachePaths = null,
    string? MatrixJson = null,
    string? ViewRole = null,
    string? RunRole = null,
    string? ParametersJson = null,
    bool AllowConcurrentBuilds = true,
    string? DownstreamJson = null,

    /// <summary>Null keeps the system setting, true and false decide it for this job alone.</summary>
    bool? CleanWorkspace = null,
    bool DenyNetwork = false,

    /// <summary>Admin only: the job runs without the sandbox on purpose.</summary>
    bool TrustedUnsandboxed = false,

    /// <summary>Clear the workspace after the build (success or failure); null keeps the system setting.</summary>
    bool? CleanWorkspaceAfterBuild = null)
{
    public JobDraft ToDraft(string? command, string? actionsJson)
    {
        return new JobDraft(
            Name!,
            Project,
            Description,
            Repository,
            Branch,
            RunnerName,
            WorkingDirectory,
            command ?? string.Empty,
            TimeoutMinutes,
            ArtifactPath,
            TriggerKind ?? TriggerMode.Manual,
            Cron,
            NotifyOnFailure,
            IsDisabled,
            ScriptMode ?? ScriptModes.Source,
            actionsJson,
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

public sealed record BuildRequest(
    string? TriggeredBy,
    Dictionary<string, string>? Parameters = null,

    /// <summary>Optional idempotency reference; the Idempotency-Key header is used when this is absent.</summary>
    string? ExternalRef = null);

public sealed record JenkinsImportRequest(string? Content, string? Name);

public sealed record EnabledRequest(bool Enabled);
