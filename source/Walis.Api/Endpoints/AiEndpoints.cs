using Microsoft.AspNetCore.Mvc;
using Walis.Api.Services;
using Walis.Api.Services.Ai;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Api.Endpoints;

public static class AiEndpoints
{
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        var ai = app.MapGroup("/api/ai").WithTags("AI");

        ai.MapGet("/providers", async (
                HttpContext context,
                IAiRepository repository,
                CancellationToken cancellationToken) =>
            Results.Ok((await repository.ListProvidersAsync(cancellationToken))
                .Select(provider => JobAccess.Rank(RequestAccess.Role(context)) < JobAccess.Rank(UserRole.Operator)
                    // A viewer cannot use AI features; it only learns whether one is available.
                    ? new { provider.Id, provider.Kind, provider.Name, provider.IsDefault, isUsable = provider.IsUsable }
                    : Present(provider, RequestAccess.IsAdmin(context)))));

        ai.MapPut("/providers/{id:guid}", async (
            Guid id,
            AiProviderRequest request,
            IAiRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var errors = new Dictionary<string, string[]>();

            if (request.IsEnabled && string.IsNullOrWhiteSpace(request.Model))
                errors["model"] = ["啟用前請先填寫模型名稱。"];
            else if (request.Model?.Length > 120) errors["model"] = ["模型名稱不可超過 120 個字元。"];

            if (!string.IsNullOrWhiteSpace(request.BaseUrl) &&
                (request.BaseUrl.Length > 2048 ||
                 !Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri) ||
                 uri.Scheme is not ("http" or "https") ||
                 string.IsNullOrEmpty(uri.Host) ||
                 !string.IsNullOrEmpty(uri.UserInfo) ||
                 !string.IsNullOrEmpty(uri.Query) ||
                 !string.IsNullOrEmpty(uri.Fragment)))
                errors["baseUrl"] = ["請輸入不帶帳密、查詢或片段的 HTTP 或 HTTPS 完整網址。"];

            if (request.ApiKey?.Length > 8192 ||
                request.ApiKey?.Any(character => character is '\r' or '\n' or '\0') == true)
                errors["apiKey"] = ["API 金鑰不可超過 8,192 個字元或包含控制換行字元。"];

            if (errors.Count != 0) return Results.ValidationProblem(errors);

            var provider = await repository.UpdateProviderAsync(
                id,
                new AiProviderDraft(request.ApiKey, request.Model, request.BaseUrl, request.IsEnabled,
                    request.IsDefault),
                cancellationToken);

            if (provider is null) return Results.NotFound();

            await audit.RecordAsync(
                AuditAction.Update,
                "ai-provider",
                provider.Id.ToString(),
                $"更新 AI 供應商 {provider.Name}（{provider.Model}）",
                cancellationToken);

            return Results.Ok(Present(provider, true));
        }).WithMetadata(new RequestSizeLimitAttribute(32 * 1024));

        ai.MapPost("/providers/{id:guid}/test", async (
            Guid id,
            IAiRepository repository,
            AiService service,
            CancellationToken cancellationToken) =>
        {
            var provider = await repository.GetProviderAsync(id, cancellationToken);

            if (provider is null) return Results.NotFound();

            if (!provider.HasApiKey) return Results.Conflict(new { message = "請先填寫 API 金鑰。" });

            try
            {
                var answer = await service.CompleteAsync(
                    provider,
                    "你正在被測試連線，請用繁體中文回覆一句話確認你收到訊息。",
                    "請回覆「連線正常」。",
                    AiPurpose.Test,
                    null,
                    null,
                    cancellationToken);

                return Results.Ok(new { answer.Text, answer.Model, answer.DurationMs });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
        });

        ai.MapPost("/analyze/build/{buildId:guid}", async (
            Guid buildId,
            AiAnalyzeRequest? request,
            HttpContext httpContext,
            IJobRepository jobs,
            IAiRepository repository,
            IEnvironmentVariableRepository variables,
            ISettingsRepository settings,
            AiService service,
            CancellationToken cancellationToken) =>
        {
            if (await OperatorAiForbiddenAsync(httpContext, settings, cancellationToken) is { } forbidden) return forbidden;

            var context = await jobs.GetBuildContextAsync(buildId, cancellationToken);

            if (context is null) return Results.NotFound();

            if (!JobAccess.CanView(RequestAccess.Role(httpContext), context.Job)) return Results.NotFound();

            var provider = await repository.ResolveProviderAsync(request?.ProviderId, cancellationToken);

            if (provider is null) return Results.Conflict(new { message = "尚未設定可用的 AI 供應商，請先到 AI 助理頁面填寫金鑰。" });

            // The log is already masked, but the job script and anything masking missed must not reach a third party either:
            // the whole prompt is redacted with the job's secrets and this build's password parameters before it leaves.
            var secrets = (await variables.ListSecretValuesForJobAsync(context.Job.Id, cancellationToken))
                .Concat(BuildParameters.ParseValues(context.Build.SecretParametersJson).Values);

            try
            {
                var answer = await service.CompleteAsync(
                    provider,
                    AiPrompts.AnalyzeSystem,
                    RedactPrompt(AiPrompts.BuildAnalysisPrompt(context.Job, context.Build, context.Build.Log), secrets),
                    AiPurpose.AnalyzeBuild,
                    buildId,
                    context.Job.Id,
                    cancellationToken);

                return Results.Ok(answer);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
        }).WithMetadata(new RequestSizeLimitAttribute(16 * 1024));

        ai.MapPost("/review-script", async (
            AiReviewRequest request,
            HttpContext context,
            IJobRepository jobs,
            IAiRepository repository,
            IEnvironmentVariableRepository variables,
            ISettingsRepository settings,
            AiService service,
            CancellationToken cancellationToken) =>
        {
            if (await OperatorAiForbiddenAsync(context, settings, cancellationToken) is { } forbidden) return forbidden;

            if (string.IsNullOrWhiteSpace(request.Command))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["command"] = ["請先輸入要檢查的命令。"]
                });

            if (request.Command.Length > 100_000 ||
                request.WorkingDirectory?.Length > 4096 ||
                request.JobName?.Length > 200)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["command"] = ["要送給 AI 的腳本或上下文過長。"]
                });

            if (request.JobId is { } requestedJobId)
            {
                var job = await jobs.GetJobAsync(requestedJobId, cancellationToken);
                if (job is null || !JobAccess.CanView(RequestAccess.Role(context), job)) return Results.NotFound();
            }

            var provider = await repository.ResolveProviderAsync(request.ProviderId, cancellationToken);

            if (provider is null) return Results.Conflict(new { message = "尚未設定可用的 AI 供應商，請先到 AI 助理頁面填寫金鑰。" });

            var names = request.JobId is { } jobId
                ? (await variables.ResolveForJobAsync(jobId, cancellationToken)).Keys.ToList()
                : [];

            // Only the variable names go into the prompt; a script that pasted a secret's value in by hand is redacted too.
            var secrets = request.JobId is { } secretJobId
                ? await variables.ListSecretValuesForJobAsync(secretJobId, cancellationToken)
                : [];

            try
            {
                var answer = await service.CompleteAsync(
                    provider,
                    AiPrompts.ReviewSystem,
                    RedactPrompt(
                        AiPrompts.BuildReviewPrompt(request.Command, request.WorkingDirectory, request.JobName, names),
                        secrets),
                    AiPurpose.ReviewScript,
                    null,
                    request.JobId,
                    cancellationToken);

                return Results.Ok(answer);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
        }).WithMetadata(new RequestSizeLimitAttribute(256 * 1024));

        ai.MapGet("/requests", async (
            int? limit,
            HttpContext context,
            IAiRepository repository,
            IJobRepository jobs,
            CancellationToken cancellationToken) =>
        {
            var role = RequestAccess.Role(context);

            // Who asked the AI what, and how it failed, is operations detail: not for viewers (anonymous in the legacy public mode).
            if (JobAccess.Rank(role) < JobAccess.Rank(UserRole.Operator))
                return Results.Json(new { message = "需要 operator 以上的角色才能查看 AI 請求紀錄。" },
                    statusCode: StatusCodes.Status403Forbidden);

            var entries = await repository.ListRequestsAsync(limit ?? 50, cancellationToken);

            if (role == UserRole.Admin) return Results.Ok(entries);

            var visibleJobs = (await jobs.ListJobsAsync(cancellationToken))
                .Where(job => JobAccess.CanView(role, job))
                .Select(job => job.Id)
                .ToHashSet();

            // Records of system/test requests without a JobId and of hidden jobs go to admins only; records include the actor and error details.
            // Operators see the requests about jobs they can see, without the account that made them.
            return Results.Ok(entries
                .Where(entry => entry.JobId is { } jobId && visibleJobs.Contains(jobId))
                .Select(entry => new
                {
                    entry.Id, entry.StartedAt, entry.ProviderKind, entry.Model, entry.Purpose, entry.BuildId, entry.JobId,
                    entry.JobName, entry.PromptChars, entry.InputTokens, entry.OutputTokens, entry.DurationMs, entry.Succeeded,
                    entry.Error
                }));
        });

        return app;
    }

    /// <summary>
    ///     Everything sent to a provider passes through here: the job's secret values and their common encodings become <c>***</c>.
    ///     Kept public so the behaviour is pinned by tests without a fake provider.
    /// </summary>
    public static string RedactPrompt(string prompt, IEnumerable<string?> secrets)
    {
        return SecretRedactor.Redact(prompt, secrets);
    }

    /// <summary>
    ///     The AI features ship job scripts and build output to an outside service. <c>AllowOperatorAi</c> (on by default) lets
    ///     an administrator reserve them for administrators; the role rules in the middleware stay as they are.
    /// </summary>
    private static async Task<IResult?> OperatorAiForbiddenAsync(
        HttpContext context,
        ISettingsRepository settings,
        CancellationToken cancellationToken)
    {
        if (RequestAccess.IsAdmin(context)) return null;

        var configuration = await settings.GetAsync(cancellationToken);

        return configuration.AllowOperatorAi
            ? null
            : Results.Json(
                new { message = "管理員已關閉操作者使用 AI 功能；請聯絡管理員。" },
                statusCode: StatusCodes.Status403Forbidden);
    }

    private static object Present(AiProvider provider, bool administrator)
    {
        return new
        {
            provider.Id,
            provider.Kind,
            provider.Name,
            provider.Model,
            // A custom BaseUrl is often an internal host; operators only need the provider id/model to choose,
            // and viewers certainly must not probe the service topology through the list.
            baseUrl = administrator ? provider.BaseUrl : null,
            provider.IsEnabled,
            provider.IsDefault,
            provider.UpdatedAt,
            hasApiKey = provider.HasApiKey,
            isUsable = provider.IsUsable,
            defaultModel = AiProviderKind.DefaultModel(provider.Kind)
        };
    }
}

/// <summary>A null ApiKey keeps the existing key, an empty string clears it.</summary>
public sealed record AiProviderRequest(string? ApiKey, string? Model, string? BaseUrl, bool IsEnabled, bool IsDefault);

public sealed record AiAnalyzeRequest(Guid? ProviderId);

public sealed record AiReviewRequest(
    Guid? ProviderId,
    string? Command,
    string? WorkingDirectory,
    string? JobName,
    Guid? JobId);