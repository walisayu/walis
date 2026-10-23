using Walis.Core.Actions;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Walis.Api.Services;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Endpoints;

public static class BuildEndpoints
{
    public const int MaximumRunnerCallbackBytes = 512 * 1024;

    /// <summary>Set on runner callbacks once a cancel has been requested; walis-runner stops the build when it sees it.</summary>
    public const string CancelRequestedHeader = "Walis-Cancel-Requested";

    public static IEndpointRouteBuilder MapBuildEndpoints(this IEndpointRouteBuilder app)
    {
        var builds = app.MapGroup("/api/builds").WithTags("Builds");

        builds.MapGet("/", async (
            string? status,
            string? runner,
            Guid? jobId,
            string? search,
            int? limit,
            HttpContext context,
            IJobRepository repository,
            CancellationToken cancellationToken) =>
        {
            var list = await repository.ListBuildsAsync(
                new BuildQuery(status, runner, jobId, search, limit ?? 30),
                cancellationToken);

            return Results.Ok(await FilterVisibleAsync(list, context, repository, cancellationToken));
        });

        // The paged list for the builds page: filtering, permissions and paging happen in SQL, so the totals and the
        // per-status tally stay right no matter which page is being viewed.
        builds.MapGet("/page", async (
            string? status,
            string? runner,
            Guid? jobId,
            string? search,
            int? page,
            int? pageSize,
            HttpContext context,
            IJobRepository repository,
            CancellationToken cancellationToken) =>
        {
            var role = RequestAccess.Role(context);
            var visibleRoles = new[] { UserRole.Viewer, UserRole.Operator, UserRole.Admin }
                .Where(viewRole => JobAccess.CanView(role, new Job { ViewRole = viewRole }))
                .ToArray();

            var size = pageSize ?? BuildQuery.DefaultPageSize;

            return Results.Ok(await repository.QueryBuildsAsync(
                new BuildQuery(status, runner, jobId, search, size, visibleRoles, page ?? 1, size),
                cancellationToken));
        });

        builds.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext context,
            IJobRepository repository,
            CancellationToken cancellationToken) =>
        {
            var visible = await LoadVisibleAsync(id, context, repository, cancellationToken);
            return visible is null ? Results.NotFound() : Results.Ok(visible.Build);
        });

        builds.MapPost("/{id:guid}/cancel", async (
            Guid id,
            HttpContext context,
            IJobRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var visible = await LoadVisibleAsync(id, context, repository, cancellationToken);
            if (visible is null) return Results.NotFound();

            // Cancelling is the same level of operation as running.
            if (visible.Job is not null && !JobAccess.CanRun(RequestAccess.Role(context), visible.Job))
                return Results.Json(
                    new { message = $"這個任務需要 {visible.Job.RunRole} 以上的角色才能取消建置。" },
                    statusCode: StatusCodes.Status403Forbidden);

            // Queued builds end here; a running one keeps its status until the executor has actually stopped the shell
            // (the local worker polls every couple of seconds, a remote runner is told on its next report).
            if (await repository.CancelBuildAsync(id, cancellationToken) == BuildCancellation.NotCancelable)
                return Results.Conflict(new { message = "只有排隊中或執行中的建置可以取消。" });

            await audit.RecordAsync(
                new AuditEvent(AuditAction.Cancel, "build", id.ToString(),
                    $"取消建置 {visible.Build.JobName} #{visible.Build.Number}") { JobId = visible.Build.JobId },
                cancellationToken);

            return Results.Ok(await repository.GetBuildAsync(id, cancellationToken));
        });

        builds.MapGet("/{id:guid}/artifacts", async (
            Guid id,
            HttpContext context,
            IJobRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (await LoadVisibleAsync(id, context, repository, cancellationToken) is null) return Results.NotFound();

            return Results.Ok(await repository.ListArtifactsAsync(id, cancellationToken));
        });

        builds.MapGet("/{id:guid}/artifacts/{artifactId:guid}", async (
            Guid id,
            Guid artifactId,
            HttpContext context,
            IJobRepository repository,
            ArtifactStore store,
            NodeIdentity node,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var build = await LoadVisibleAsync(id, context, repository, cancellationToken);

            if (build is null) return Results.NotFound();

            var artifact = await repository.GetArtifactAsync(artifactId, cancellationToken);

            if (artifact is null || artifact.BuildId != id) return Results.NotFound();

            // Do not hand the pathname to Results.File for a deferred reopen: a same-UID background process left by the build could
            // swap the file for a symlink after the check. ArtifactStore opens with O_NOFOLLOW and validates the handle.
            if (!store.TryOpenFile(id, artifact.RelativePath, out var stream))
            {
                // Artifacts stay on the disk of the node that ran the build. Without shared storage in a multi-node setup
                // they cannot be fetched from another node; saying which node has them beats a bare "no longer exists".
                var elsewhere = node.IsClustered
                                && build.Build.Node is { Length: > 0 }
                                && build.Build.Node != node.Name;

                return Results.NotFound(new
                {
                    message = elsewhere
                        ? $"這個建置的產出檔案在節點 {build.Build.Node} 上。叢集部署請把 data/artifacts 放在共用儲存。"
                        : "產出檔案已不存在。"
                });
            }

            // Artifacts may hold compiled secrets; who fetched which file is part of the trail.
            await audit.RecordAsync(
                new AuditEvent(AuditAction.Download, "build", id.ToString(),
                    $"下載產出檔案 {build.Build.JobName} #{build.Build.Number}：{artifact.RelativePath}")
                {
                    JobId = build.Build.JobId
                },
                cancellationToken);

            return Results.File(stream, "application/octet-stream", Path.GetFileName(artifact.RelativePath));
        });

        // Lets a remote runner upload artifacts; each file's name is its path relative to the workspace.
        // The same caps as local collection (200 files, 64 MB), otherwise one runaway runner could fill the disk.
        builds.MapPost("/{id:guid}/artifacts", async (
            Guid id,
            HttpRequest request,
            HttpContext context,
            IJobRepository repository,
            RemoteArtifactUploadService uploads,
            CancellationToken cancellationToken) =>
        {
            var build = await repository.GetBuildAsync(id, cancellationToken);

            if (build is null) return Results.NotFound();

            var runnerKey = RequestAccess.RunnerKey(context);

            if (runnerKey is null ||
                !string.Equals(runnerKey.RunnerName, build.RunnerName, StringComparison.OrdinalIgnoreCase))
                return await RefuseRunnerAsync(context, build, repository, true, cancellationToken);

            if (build.Status != BuildStatus.Running) return Results.Conflict(new { message = "只有執行中的建置可以上傳產出檔案。" });

            if (!request.HasFormContentType)
                return Results.BadRequest(new { message = "請以 multipart/form-data 上傳檔案。" });

            var result = await uploads.UploadAsync(
                id,
                runnerKey.RunnerName!,
                request,
                cancellationToken);

            return result.Status switch
            {
                RemoteArtifactUploadStatus.Saved => Results.Ok(new { saved = result.Saved, skipped = 0 }),
                RemoteArtifactUploadStatus.PayloadTooLarge => Results.Json(
                    new { message = result.Message },
                    statusCode: StatusCodes.Status413PayloadTooLarge),
                RemoteArtifactUploadStatus.NotFound => Results.NotFound(new { message = result.Message }),
                RemoteArtifactUploadStatus.Forbidden => Results.Json(
                    new { message = result.Message },
                    statusCode: StatusCodes.Status403Forbidden),
                RemoteArtifactUploadStatus.Conflict => Results.Conflict(new { message = result.Message }),
                _ => Results.BadRequest(new { message = result.Message })
            };
        }).WithMetadata(new RequestSizeLimitAttribute(RemoteArtifactUploadService.MaximumRequestBytes));

        // Lets a remote runner report the result.
        // A remote runner reports the current output mid-run so the admin UI can show live progress.
        builds.MapPost("/{id:guid}/log", async (
            Guid id,
            BuildLogRequest request,
            HttpContext context,
            IJobRepository repository,
            IEnvironmentVariableRepository variables,
            ISettingsRepository settings,
            CancellationToken cancellationToken) =>
        {
            var build = await repository.GetBuildAsync(id, cancellationToken);

            if (build is null) return Results.NotFound();

            if (!RequestAccess.CanActAsRunner(context, build.RunnerName)) return await RefuseRunnerAsync(context, build, repository, false, cancellationToken);

            MarkCancelRequested(context, build);
            await repository.UpdateBuildLogAsync(
                id,
                await SanitizeRunnerLogAsync(build, request.Log, variables, settings, cancellationToken),
                cancellationToken);
            return Results.NoContent();
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumRunnerCallbackBytes));

        // A remote runner reports the live output of a single step.
        builds.MapPost("/{id:guid}/steps/{stepId:guid}/log", async (
            Guid id,
            Guid stepId,
            BuildLogRequest request,
            HttpContext context,
            IJobRepository repository,
            IEnvironmentVariableRepository variables,
            ISettingsRepository settings,
            CancellationToken cancellationToken) =>
        {
            var check = await CheckStepAsync(id, stepId, context, repository, cancellationToken);

            if (check is not null) return check;

            var build = await repository.GetBuildAsync(id, cancellationToken);

            if (build is null) return Results.NotFound();

            MarkCancelRequested(context, build);
            await repository.UpdateBuildStepLogAsync(
                stepId,
                await SanitizeRunnerLogAsync(build, request.Log, variables, settings, cancellationToken),
                cancellationToken);
            return Results.NoContent();
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumRunnerCallbackBytes));

        builds.MapPost("/{id:guid}/steps/{stepId:guid}/result", async (
            Guid id,
            Guid stepId,
            BuildStepResultRequest request,
            HttpContext context,
            IJobRepository repository,
            IEnvironmentVariableRepository variables,
            ISettingsRepository settings,
            CancellationToken cancellationToken) =>
        {
            var check = await CheckStepAsync(id, stepId, context, repository, cancellationToken);

            if (check is not null) return check;

            var status = request.Status is BuildStepStatus.Succeeded or BuildStepStatus.Failed or BuildStepStatus.Skipped
                ? request.Status
                : BuildStepStatus.Failed;

            var build = await repository.GetBuildAsync(id, cancellationToken);

            if (build is null) return Results.NotFound();

            MarkCancelRequested(context, build);

            // Outputs are handled like logs: the format is re-filtered and secrets re-masked before writing to the database.
            var outputs = StepExpressions.Sanitize(request.Outputs);
            string? outputsJson = null;
            if (outputs.Count != 0)
            {
                var masked = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (name, value) in outputs)
                    masked[name] = await SanitizeRunnerLogAsync(build, value, variables, settings, cancellationToken);
                outputsJson = JsonSerializer.Serialize(masked);
            }

            await repository.CompleteBuildStepAsync(
                stepId,
                status,
                request.ExitCode,
                await SanitizeRunnerLogAsync(build, request.Log, variables, settings, cancellationToken),
                outputsJson,
                cancellationToken);

            return Results.NoContent();
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumRunnerCallbackBytes));

        builds.MapPost("/{id:guid}/result", async (
            Guid id,
            BuildResultRequest request,
            HttpContext context,
            IJobRepository repository,
            IEnvironmentVariableRepository variables,
            ISettingsRepository settings,
            BuildCompletionService completion,
            CancellationToken cancellationToken) =>
        {
            var build = await repository.GetBuildAsync(id, cancellationToken);
            if (build is null) return Results.NotFound();

            if (!RequestAccess.CanActAsRunner(context, build.RunnerName)) return await RefuseRunnerAsync(context, build, repository, false, cancellationToken);

            if (build.Status != BuildStatus.Running) return Results.Conflict(new { message = "這筆建置不在執行中，無法回報結果。" });

            // Canceled is only honoured when this build was in fact asked to stop; a runner cannot label a failure as a cancel.
            var status = request.Status switch
            {
                BuildStatus.Succeeded => BuildStatus.Succeeded,
                BuildStatus.Canceled when build.CancelRequestedAt is not null => BuildStatus.Canceled,
                _ => BuildStatus.Failed
            };

            // The repository layer requires the parent build to still be Running; skip the steps that never ran first,
            // then complete the build atomically so no late step mutation lands after completion.
            await repository.SkipPendingBuildStepsAsync(id, cancellationToken);

            var previousStatus =
                await repository.GetPreviousBuildStatusAsync(build.JobId, build.Number, cancellationToken);

            // Two reports racing for the same build both read Running above; only the one whose UPDATE changed the row
            // owns the wrap-up, otherwise notifications and downstream builds would go out twice.
            var changed = await repository.CompleteBuildAsync(
                id,
                new BuildCompletion(
                    status,
                    request.ExitCode,
                    await SanitizeRunnerLogAsync(build, request.Log, variables, settings, cancellationToken)),
                cancellationToken);

            if (changed == 0)
                return Results.Conflict(new { message = "這筆建置的結果已由另一次回報寫入，這次回報未生效。" });

            var finished = await repository.GetBuildAsync(id, cancellationToken);
            var job = await repository.GetJobAsync(build.JobId, cancellationToken);
            if (finished is not null && job is not null)
                await completion.HandleAsync(job, finished, previousStatus, false, CancellationToken.None);

            return Results.Ok(finished);
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumRunnerCallbackBytes));

        return app;
    }

    /// <summary>
    ///     Before reading a build, check whether its job is visible; invisible ones always get 404, revealing nothing, just like the job itself.
    ///     Orphan records whose job was deleted have no parent to authorize against, so they go to admins only.
    /// </summary>
    private static async Task<VisibleBuild?> LoadVisibleAsync(
        Guid id,
        HttpContext context,
        IJobRepository repository,
        CancellationToken cancellationToken)
    {
        var build = await repository.GetBuildAsync(id, cancellationToken);

        if (build is null) return null;

        var job = await repository.GetJobAsync(build.JobId, cancellationToken);

        if (job is null) return RequestAccess.IsAdmin(context) ? new VisibleBuild(build, null) : null;

        if (!JobAccess.CanView(RequestAccess.Role(context), job)) return null;

        return new VisibleBuild(build, job);
    }

    /// <summary>For lists: fetch the jobs once and filter out builds of invisible jobs.</summary>
    public static async Task<IReadOnlyList<Build>> FilterVisibleAsync(
        IReadOnlyList<Build> builds,
        HttpContext context,
        IJobRepository repository,
        CancellationToken cancellationToken)
    {
        var role = RequestAccess.Role(context);

        if (role == UserRole.Admin || builds.Count == 0) return builds;

        var visible = (await repository.ListJobsAsync(cancellationToken))
            .Where(job => JobAccess.CanView(role, job))
            .Select(job => job.Id)
            .ToHashSet();

        return [.. builds.Where(build => visible.Contains(build.JobId))];
    }

    /// <summary>Step reports: the build must exist, the step must belong to it, and the caller must be allowed to act as that runner.</summary>
    private static async Task<IResult?> CheckStepAsync(
        Guid buildId,
        Guid stepId,
        HttpContext context,
        IJobRepository repository,
        CancellationToken cancellationToken)
    {
        var build = await repository.GetBuildAsync(buildId, cancellationToken);

        if (build is null) return Results.NotFound();

        if (!RequestAccess.CanActAsRunner(context, build.RunnerName)) return await RefuseRunnerAsync(context, build, repository, false, cancellationToken);

        if (build.Status != BuildStatus.Running) return Results.Conflict(new { message = "這筆建置不在執行中，無法回報步驟。" });

        var steps = await repository.ListBuildStepsAsync(buildId, cancellationToken);

        return steps.Any(step => step.Id == stepId)
            ? null
            : Results.NotFound(new { message = "這筆建置沒有這個步驟。" });
    }

    /// <summary>Tells the reporting runner that an admin asked for this build to stop; the body stays as before.</summary>
    private static void MarkCancelRequested(HttpContext context, Build build)
    {
        if (build.CancelRequestedAt is not null) context.Response.Headers[CancelRequestedHeader] = "true";
    }

    /// <summary>
    ///     A caller that is not the build's runner. Someone who cannot see the job gets the same 404 as for a build that does not
    ///     exist (every other build endpoint does that), and the message never names the runner: that would confirm a hidden
    ///     build and reveal the deployment's runner layout.
    /// </summary>
    private static async Task<IResult> RefuseRunnerAsync(
        HttpContext context,
        Build build,
        IJobRepository repository,
        bool artifacts,
        CancellationToken cancellationToken)
    {
        var job = await repository.GetJobAsync(build.JobId, cancellationToken);

        if (job is null || !JobAccess.CanView(RequestAccess.Role(context), job)) return Results.NotFound();

        return Results.Json(
            new
            {
                message = artifacts
                    ? "只有這筆建置的執行器所綁定的 runner API 金鑰可以上傳產出檔案。"
                    : "只有這筆建置的執行器所綁定的 API 金鑰或管理員可以回報這筆建置。"
            },
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static async Task<string> SanitizeRunnerLogAsync(
        Build build,
        string? log,
        IEnvironmentVariableRepository variables,
        ISettingsRepository settings,
        CancellationToken cancellationToken)
    {
        var configuration = await settings.GetAsync(cancellationToken);

        // The same list the runner was given at claim time: the job's secret variables plus the password-type parameter
        // values of this build. This is the second line of defence when a runner's own masking is missing or disabled.
        var secrets = (await variables.ListSecretValuesForJobAsync(build.JobId, cancellationToken))
            .Concat(BuildParameters.ParseValues(build.SecretParametersJson).Values
                .Where(value => value.Length != 0 && value.Length <= ShellCommandExecutor.MaximumSecretLength))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return ShellCommandExecutor.SanitizeLog(log, secrets, configuration.MaskSecrets);
    }

    private sealed record VisibleBuild(Build Build, Job? Job);
}

public sealed record BuildLogRequest(string? Log);

public sealed record BuildStepResultRequest(
    string Status,
    int? ExitCode,
    string? Log,
    Dictionary<string, string?>? Outputs = null);

public sealed record BuildResultRequest(string Status, int? ExitCode, string? Log);
