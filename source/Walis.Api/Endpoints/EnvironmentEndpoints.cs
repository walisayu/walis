using Walis.Api.Services;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Endpoints;

public static class EnvironmentEndpoints
{
    public static IEndpointRouteBuilder MapEnvironmentEndpoints(this IEndpointRouteBuilder app)
    {
        var variables = app.MapGroup("/api/environment-variables").WithTags("Environment");

        variables.MapGet("/", async (
            bool? reveal,
            HttpContext context,
            IEnvironmentVariableRepository repository,
            IJobRepository jobs,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var role = RequestAccess.Role(context);
            if (role == UserRole.Viewer)
                return Results.Json(
                    new { message = "只有操作者或管理員可以查看環境變數。" },
                    statusCode: StatusCodes.Status403Forbidden);

            // Plaintext is for admins only: the whole point of a secret is that not everyone who can sign in can read it.
            if (reveal == true && !RequestAccess.IsAdmin(context))
                return Results.Json(
                    new { message = "只有管理員可以顯示機密值。" },
                    statusCode: StatusCodes.Status403Forbidden);

            var list = await repository.ListAsync(cancellationToken);
            var visibleJobIds = (await jobs.ListJobsAsync(cancellationToken))
                .Where(job => JobAccess.CanView(role, job))
                .Select(job => job.Id)
                .ToHashSet();
            list = [.. list.Where(item => item.JobId is null || visibleJobIds.Contains(item.JobId.Value))];

            // The MaskSecrets setting for build logs is not an API display preference. Even an admin must explicitly pass reveal=true on this
            // request to get plaintext, so an ordinary list is never recorded by accident in a browser or debugging tool.
            var revealSecrets = RequestAccess.IsAdmin(context) && reveal == true;

            // Reading plaintext secrets is an event in its own right: one record per request naming the secret variables, never their values.
            if (revealSecrets)
            {
                var revealed = list.Where(item => item.IsSecret).Select(item => item.Name).ToList();
                await audit.RecordAsync(AuditAction.Reveal, "variable", null,
                    revealed.Count == 0
                        ? "顯示環境變數明文（目前沒有機密變數）"
                        : $"顯示 {revealed.Count} 個機密環境變數的明文：{string.Join("、", revealed)}",
                    cancellationToken);
            }

            return Results.Ok(list.Select(item => Present(item, revealSecrets)));
        });

        variables.MapPost("/", async (
            EnvironmentVariableRequest request,
            HttpContext context,
            IEnvironmentVariableRepository repository,
            IJobRepository jobs,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var errors = Validate(request);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            if (await CheckScopeAsync(request.JobId, context, jobs, true, cancellationToken) is
                { } denied) return denied;

            try
            {
                var created = await repository.CreateAsync(request.ToDraft(), cancellationToken);
                await audit.RecordAsync(AuditAction.Create, "variable", created.Id.ToString(), $"新增環境變數 {created.Name}",
                    cancellationToken);
                return Results.Created(
                    $"/api/environment-variables/{created.Id}",
                    Present(created, RequestAccess.IsAdmin(context)));
            }
            catch (Exception exception) when (exception.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new { message = "同一個套用範圍下已有同名的變數。" });
            }
        });

        variables.MapPut("/{id:guid}", async (
            Guid id,
            EnvironmentVariableRequest request,
            HttpContext context,
            IEnvironmentVariableRepository repository,
            IJobRepository jobs,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var current = await repository.GetAsync(id, cancellationToken);
            if (current is null) return Results.NotFound();

            if (await CheckScopeAsync(current.JobId, context, jobs, false, cancellationToken) is
                { } currentDenied) return currentDenied;

            var resolved = request;

            // The UI never receives a secret's plaintext, so it sends the mask back untouched when only the name, the
            // scope or the flag changed. Keep the stored value then, the way settings and backup destinations read a
            // masked field back as "unchanged".
            if (current.IsSecret && request.Value == EnvironmentVariable.Mask)
            {
                // Clearing the secret flag publishes the value in the list, so that case always needs a real value:
                // otherwise anyone allowed to edit the variable could turn a secret they may not reveal into plaintext.
                if (!request.IsSecret)
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["value"] = ["取消機密時請重新輸入變數內容。"]
                    });

                resolved = request with { Value = current.Value };
            }

            var errors = Validate(resolved);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            if (await CheckScopeAsync(resolved.JobId, context, jobs, true, cancellationToken) is
                { } targetDenied) return targetDenied;

            var updated = await repository.UpdateAsync(id, resolved.ToDraft(), cancellationToken);

            if (updated is not null)
                await audit.RecordAsync(AuditAction.Update, "variable", updated.Id.ToString(), $"更新環境變數 {updated.Name}",
                    cancellationToken);

            return updated is null
                ? Results.NotFound()
                : Results.Ok(Present(updated, RequestAccess.IsAdmin(context)));
        });

        variables.MapDelete("/{id:guid}", async (
            Guid id,
            HttpContext context,
            IEnvironmentVariableRepository repository,
            IJobRepository jobs,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var variable = await repository.GetAsync(id, cancellationToken);
            if (variable is null) return Results.NotFound();

            if (await CheckScopeAsync(variable.JobId, context, jobs, false, cancellationToken) is
                { } denied) return denied;

            if (!await repository.DeleteAsync(id, cancellationToken)) return Results.NotFound();

            await audit.RecordAsync(AuditAction.Delete, "variable", id.ToString(),
                $"刪除環境變數 {variable?.Name ?? id.ToString()}", cancellationToken);
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>
    ///     Job-level variables follow the job's run threshold; global variables affect every job, so only admins may change them.
    ///     Invisible jobs return 404 so their existence cannot be probed through the variable endpoint.
    /// </summary>
    private static async Task<IResult?> CheckScopeAsync(
        Guid? jobId,
        HttpContext context,
        IJobRepository jobs,
        bool targetScope,
        CancellationToken cancellationToken)
    {
        if (jobId is null)
            return RequestAccess.IsAdmin(context)
                ? null
                : Results.Json(new { message = "只有管理員可以修改全域環境變數。" }, statusCode: StatusCodes.Status403Forbidden);

        // An existing variable must remain cleanable by an admin even if old data left an invalid JobId; a new scope must point at an existing job.
        if (RequestAccess.IsAdmin(context) && !targetScope) return null;

        var job = await jobs.GetJobAsync(jobId.Value, cancellationToken);
        if (job is null || !JobAccess.CanView(RequestAccess.Role(context), job)) return Results.NotFound();

        if (!JobAccess.CanModify(RequestAccess.Role(context), job))
            return Results.Json(
                new { message = job.TrustedUnsandboxed ? "只有管理員可以修改信任任務的環境變數。" : $"這個任務需要 {job.RunRole} 以上的角色才能修改環境變數。" },
                statusCode: StatusCodes.Status403Forbidden);

        return null;
    }

    private static object Present(EnvironmentVariable variable, bool reveal)
    {
        return new
        {
            variable.Id,
            variable.Name,
            Value = variable.IsSecret && !reveal ? EnvironmentVariable.Mask : variable.Value,
            variable.IsSecret,
            variable.JobId,
            variable.JobName,
            variable.UpdatedAt
        };
    }

    private static Dictionary<string, string[]> Validate(EnvironmentVariableRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Name))
            errors["name"] = ["請輸入變數名稱。"];
        else if (request.Name.Length > 200)
            errors["name"] = ["變數名稱不可超過 200 個字元。"];
        else if (!request.Name.All(character => char.IsLetterOrDigit(character) || character == '_'))
            errors["name"] = ["變數名稱只能使用英數字與底線。"];
        // Same rule as build parameters: WALIS_* carries the build number, step outputs and upstream status that
        // notifications and downstream jobs rely on; a job variable must not be able to shadow them.
        else if (request.Name.StartsWith(ShellCommandExecutor.ReservedVariablePrefix, StringComparison.OrdinalIgnoreCase))
            errors["name"] = [$"變數名稱不可以 {ShellCommandExecutor.ReservedVariablePrefix} 開頭，那是保留給系統的。"];

        if (request.Value is null)
            errors["value"] = ["請輸入變數內容。"];
        else if (request.Value.Length > ShellCommandExecutor.MaximumSecretLength)
            errors["value"] = [$"變數內容不可超過 {ShellCommandExecutor.MaximumSecretLength:N0} 個字元。"];
        else if (request.IsSecret && request.Value.Length < 4) errors["value"] = ["機密值至少需要 4 個字元，才能安全遮蔽建置輸出。"];

        return errors;
    }
}

public sealed record EnvironmentVariableRequest(string? Name, string? Value, bool IsSecret, Guid? JobId)
{
    public EnvironmentVariableDraft ToDraft()
    {
        return new EnvironmentVariableDraft(Name!, Value ?? string.Empty, IsSecret, JobId);
    }
}
