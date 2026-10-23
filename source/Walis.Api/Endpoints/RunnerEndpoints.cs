using Microsoft.AspNetCore.Mvc;
using Walis.Api.Services;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Endpoints;

public static class RunnerEndpoints
{
    private const string RunnerProtocolHeader = "X-Walis-Runner-Protocol";
    private const string RunnerIsolationHeader = "X-Walis-Runner-Isolation";
    private const string CurrentRunnerProtocol = "2";
    private const int MaximumRequestBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapRunnerEndpoints(this IEndpointRouteBuilder app)
    {
        var runners = app.MapGroup("/api/runners")
            .WithTags("Runners")
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBytes));

        runners.MapGet("/", async (
            HttpContext context,
            IRunnerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var list = await repository.ListAsync(cancellationToken);

            // A runner key only needs to confirm "am I registered"; there is no reason for it to see other machines' names and status.
            if (RequestAccess.RunnerKey(context) is { } key)
                list =
                [
                    .. list.Where(runner =>
                        string.Equals(runner.Name, key.RunnerName, StringComparison.OrdinalIgnoreCase))
                ];

            // OS, version, tags and the current job are infrastructure topology. Non-admins get only the fields needed to pick
            // a runner and show capacity; in particular CurrentJobName must not bypass the job ACL.
            return RequestAccess.IsAdmin(context)
                ? Results.Ok(list)
                : Results.Ok(list.Select(PresentLimited));
        });

        runners.MapPost("/", async (
            RunnerRequest request,
            HttpContext context,
            IRunnerRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var errors = Validate(request);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            // A key bound to a runner may only register under its own name.
            if (RequestAccess.RunnerKey(context) is { } key &&
                !string.Equals(key.RunnerName, request.Name!.Trim(), StringComparison.OrdinalIgnoreCase))
                return Results.Json(
                    new { message = $"這把金鑰只能註冊執行器「{key.RunnerName}」。" },
                    statusCode: StatusCodes.Status403Forbidden);

            if (await repository.GetByNameAsync(request.Name!.Trim(), cancellationToken) is { } existing)
                // Pausing a runner must also stop a key from "re-registering" it to look fresh; the admin resumes it on purpose.
                return Results.Conflict(new
                {
                    message = existing.IsPaused
                        ? $"執行器「{existing.Name}」已暫停，不接受重新註冊；請管理員先恢復接單。"
                        : "已有同名的執行器。"
                });

            // A runner registering itself does not get to choose which jobs it accepts; that is the administrator's setting.
            var draft = RequestAccess.RunnerKey(context) is not null
                ? request.ToDraft() with { MinimumRunRole = null }
                : request.ToDraft();
            var runner = await repository.CreateAsync(draft, cancellationToken);
            await audit.RecordAsync(AuditAction.Create, "runner", runner.Id.ToString(), $"註冊執行器 {runner.Name}",
                cancellationToken);
            return Results.Created($"/api/runners/{runner.Id}", runner);
        });

        runners.MapPut("/{id:guid}", async (
            Guid id,
            RunnerRequest request,
            IRunnerRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var errors = Validate(request);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            var runner = await repository.UpdateAsync(id, request.ToDraft(), cancellationToken);

            if (runner is not null)
                await audit.RecordAsync(AuditAction.Update, "runner", runner.Id.ToString(),
                    $"更新執行器 {runner.Name}（接受 {runner.MinimumRunRole} 以上的任務）", cancellationToken);

            return runner is null ? Results.NotFound() : Results.Ok(runner);
        });

        runners.MapPatch("/{id:guid}/paused", async (
            Guid id,
            PausedRequest request,
            IRunnerRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var runner = await repository.SetPausedAsync(id, request.Paused, cancellationToken);

            if (runner is null) return Results.NotFound();

            await audit.RecordAsync(AuditAction.Update, "runner", runner.Id.ToString(),
                $"{(request.Paused ? "暫停" : "恢復")}執行器 {runner.Name}", cancellationToken);

            return Results.Ok(runner);
        });

        runners.MapDelete("/{id:guid}", async (
            Guid id,
            IRunnerRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var runner = await repository.GetAsync(id, cancellationToken);
            if (runner is null) return Results.NotFound();

            if (runner.IsLocal) return Results.Conflict(new { message = "內建的本機執行器不可刪除。" });

            var deletion = await repository.DeleteWithKeysAsync(id, cancellationToken);
            if (deletion is null) return Results.NotFound();

            await audit.RecordAsync(AuditAction.Delete, "runner", id.ToString(),
                $"刪除執行器 {runner.Name}，一併停用 {deletion.DisabledKeyCount} 把綁定它的 API 金鑰",
                cancellationToken);
            return Results.Ok(new { disabledKeyCount = deletion.DisabledKeyCount });
        });

        runners.MapPost("/{name}/heartbeat", async (
            string name,
            HttpContext context,
            IRunnerRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (!RequestAccess.CanActAsRunner(context, name)) return RunnerForbidden(name);

            return await repository.HeartbeatAsync(name, cancellationToken) ? Results.NoContent() : Results.NotFound();
        });

        // A remote runner polls for its next job. The response carries all of the job's environment variables (secrets included),
        // so only the key bound to this runner or an admin may claim; an ordinary operator claiming under a false name would be reading the secrets.
        runners.MapPost("/{name}/claim", async (
            string name,
            HttpContext context,
            IRunnerRepository repository,
            IJobRepository jobs,
            IEnvironmentVariableRepository variables,
            ISettingsRepository settings,
            BuildStepPlanner planner,
            CancellationToken cancellationToken) =>
        {
            if (!RequestAccess.CanActAsRunner(context, name)) return RunnerForbidden(name);

            // Putting requireSandbox only in the claim response does not protect a rolling upgrade: an old runner ignores
            // unknown JSON fields and runs as before. Do the protocol handshake first and never claim work atomically until it passes.
            if (!string.Equals(
                    context.Request.Headers[RunnerProtocolHeader].ToString(),
                    CurrentRunnerProtocol,
                    StringComparison.Ordinal))
                return Results.Json(
                    new { message = "執行器協定過舊；請升級 runner 後再認領工作。" },
                    statusCode: StatusCodes.Status426UpgradeRequired);

            var runnerIsolation = context.Request.Headers[RunnerIsolationHeader].ToString();
            if (!string.Equals(runnerIsolation, "none", StringComparison.Ordinal) &&
                !string.Equals(runnerIsolation, "external", StringComparison.Ordinal))
                return Results.BadRequest(new { message = "不支援或未宣告的 runner 隔離能力。" });

            var runner = await repository.GetByNameAsync(name, cancellationToken);
            if (runner is null) return Results.NotFound();

            await repository.HeartbeatAsync(name, cancellationToken);

            if (runner.IsPaused) return Results.NoContent();

            var configuration = await settings.GetAsync(cancellationToken);
            var requiresIsolation = BuildIsolationPolicy.RequiresSandbox(configuration);
            var externalIsolationApproved =
                string.Equals(runnerIsolation, "external", StringComparison.Ordinal) &&
                BuildIsolationPolicy.IsExternallyIsolatedRunner(configuration, name);

            // Both refusals apply only while an admin requires an isolation boundary. Otherwise the runner gets its work
            // whatever it declares, and the build log records that the script ran unisolated.
            if (requiresIsolation &&
                string.Equals(runnerIsolation, "external", StringComparison.Ordinal) &&
                !externalIsolationApproved)
                return Results.Json(
                    new
                    {
                        message = "此 runner 未列入管理員核准的外部隔離清單；工作未被認領。" +
                                  "請管理員在「系統設定 → 建置」把它加入「已置於外部隔離的執行器」。"
                    },
                    statusCode: StatusCodes.Status403Forbidden);

            // The runner has no verifiable bwrap/container isolation yet; when isolation is required, the job is not handed out.
            // Declaring external must also be approved per runner name in the server settings; the request header alone is not trusted.
            if (requiresIsolation && !externalIsolationApproved)
                return Results.Conflict(new
                {
                    message = "系統要求隔離建置，但此 runner 僅宣告未隔離模式；工作未被認領。" +
                              "請以 --external-isolation true 啟動並由管理員核准，或在「系統設定 → 建置」關閉隔離要求。"
                });

            var build = await jobs.ClaimNextBuildAsync(name, null, cancellationToken);
            if (build is null) return Results.NoContent();

            var job = await jobs.GetJobAsync(build.JobId, cancellationToken);

            if (job is null)
            {
                await jobs.CompleteBuildAsync(
                    build.Id,
                    new BuildCompletion(BuildStatus.Failed, null, "找不到建置所屬任務，未交給執行器。\n"),
                    CancellationToken.None);
                return Results.Conflict(new { message = "找不到建置所屬任務；建置已標記為失敗。" });
            }

            // Steps are planned by the server so the remote runner never needs to know actions exist.
            var steps = await jobs.ListBuildStepsAsync(build.Id, cancellationToken);

            if (steps.Count == 0)
            {
                try
                {
                    await jobs.CreateBuildStepsAsync(
                        build.Id,
                        await planner.PlanAsync(job, cancellationToken),
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    await jobs.CompleteBuildAsync(
                        build.Id,
                        new BuildCompletion(
                            BuildStatus.Failed,
                            null,
                            $"建置規劃失敗：{exception.Message}\n"),
                        CancellationToken.None);
                    return Results.Conflict(new
                    {
                        message = "動作流程目前無法安全展開；建置已標記為失敗，且未交給執行器。"
                    });
                }

                steps = await jobs.ListBuildStepsAsync(build.Id, cancellationToken);
            }

            // Matrix combinations and build parameters are provided as environment variables, just like local execution; password parameters are also added to the mask list.
            var resolved = new Dictionary<string, string>(
                await variables.ResolveForJobAsync(build.JobId, cancellationToken), StringComparer.Ordinal);
            resolved["WALIS_BUILD_NUMBER"] = build.Number.ToString();
            resolved["WALIS_BUILD_ID"] = build.Id.ToString();

            foreach (var (key, value) in BuildParameters.ParseValues(build.MatrixValues)) resolved[key] = value;

            foreach (var (key, value) in BuildParameters.ParseValues(build.ParametersJson)) resolved[key] = value;

            var secretParameters = BuildParameters.ParseValues(build.SecretParametersJson);
            foreach (var (key, value) in secretParameters) resolved[key] = value;

            var secrets = (await variables.ListSecretValuesForJobAsync(build.JobId, cancellationToken))
                .Concat(secretParameters.Values.Where(value => value.Length != 0))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return Results.Ok(new
            {
                build,
                job,
                steps,
                variables = resolved,
                secrets,
                maskSecrets = configuration.MaskSecrets,

                // Resolved here because the runner has no access to the system settings.
                cleanWorkspace = job.CleanWorkspace ?? configuration.CleanWorkspace,

                cleanWorkspaceAfterBuild = job.CleanWorkspaceAfterBuild ?? configuration.CleanWorkspaceAfterBuild,

                // A trusted job is exempt from the boundary requirement; the runner only puts the fact into the step log.
                requireSandbox = requiresIsolation && !job.TrustedUnsandboxed,
                externalIsolationSatisfied = externalIsolationApproved,
                trustedUnsandboxed = job.TrustedUnsandboxed
            });
        });

        return app;
    }

    private static IResult RunnerForbidden(string name)
    {
        return Results.Json(
            new { message = $"只有綁定執行器「{name}」的 API 金鑰或管理員可以以它的身分操作。" },
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static Dictionary<string, string[]> Validate(RunnerRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Name))
            errors["name"] = ["請輸入執行器名稱。"];
        else if (request.Name.Trim().Length > 60)
            errors["name"] = ["執行器名稱不可超過 60 個字元。"];
        else if (request.Name.Any(character =>
                     char.IsControl(character) || character is '/' or '\\' or '?' or '#' or '%'))
            errors["name"] = ["執行器名稱不可包含控制字元或路徑保留字元。"];

        if (request.OperatingSystem?.Length > 300) errors["operatingSystem"] = ["作業系統說明不可超過 300 個字元。"];

        if (request.Version?.Length > 60) errors["version"] = ["版本不可超過 60 個字元。"];

        if (request.Labels?.Length > 500)
            errors["labels"] = ["標籤不可超過 500 個字元。"];
        else if ((request.Labels ?? string.Empty)
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { } labels &&
                 (labels.Length > 32 || labels.Any(label => label.Length > 60 || label.Any(char.IsControl))))
            errors["labels"] = ["標籤最多 32 個，每個不可超過 60 個字元或包含控制字元。"];

        if (request.TotalSlots is < 1 or > 32) errors["totalSlots"] = ["插槽數需介於 1 到 32。"];

        if (request.MinimumRunRole is not null && request.MinimumRunRole is not (UserRole.Operator or UserRole.Admin))
            errors["minimumRunRole"] = ["執行器接受的最低執行角色只能是 operator 或 admin。"];

        return errors;
    }

    private static object PresentLimited(Runner runner)
    {
        return new
        {
            runner.Id,
            runner.Name,
            operatingSystem = string.Empty,
            version = string.Empty,
            labels = string.Empty,
            runner.TotalSlots,
            runner.IsPaused,
            runner.IsLocal,
            runner.LastSeenAt,
            runner.CreatedAt,
            runner.UsedSlots,
            runner.Status,
            currentBuildNumber = (int?)null,
            currentJobName = (string?)null
        };
    }
}

public sealed record RunnerRequest(
    string? Name,
    string? OperatingSystem,
    string? Version,
    string? Labels,
    int TotalSlots,
    bool IsPaused,
    string? MinimumRunRole = null)
{
    public RunnerDraft ToDraft()
    {
        return new RunnerDraft(Name!, OperatingSystem, Version, Labels, TotalSlots, IsPaused, MinimumRunRole);
    }
}

public sealed record PausedRequest(bool Paused);