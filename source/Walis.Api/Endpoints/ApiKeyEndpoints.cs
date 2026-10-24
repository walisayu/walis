using Walis.Api.Services;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Api.Endpoints;

public static class ApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var keys = app.MapGroup("/api/api-keys").WithTags("ApiKeys");

        keys.MapGet("/", async (IApiKeyRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListAsync(cancellationToken)));

        keys.MapPost("/", async (
            ApiKeyRequest request,
            HttpContext context,
            IApiKeyRepository repository,
            AuditService audit,
            IReauthentication reauthentication,
            CancellationToken cancellationToken) =>
        {
            // A DateTime without an offset (or with a local one) is taken as UTC, so the expiry does not move by a time zone.
            if (request.ExpiresAt is { } expiresAt)
                request = request with
                {
                    ExpiresAt = expiresAt.Kind switch
                    {
                        DateTimeKind.Local => expiresAt.ToUniversalTime(),
                        DateTimeKind.Unspecified => DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc),
                        _ => expiresAt
                    }
                };

            var errors = Validate(request);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            // A key cannot create keys: a leaked key must not be able to grow more of itself.
            if (RequestAccess.ApiKey(context) is not null)
                return Results.Json(
                    new { message = "請用管理帳號登入後再建立金鑰。" },
                    statusCode: StatusCodes.Status403Forbidden);

            // All of /api/api-keys is already admin-only (blocked by the middleware); guard once more here:
            // a key's role must not exceed its creator's, so relaxing the middleware later cannot become a privilege-escalation door.
            if (JobAccess.Rank(request.Role ?? UserRole.Operator) > JobAccess.Rank(RequestAccess.Role(context)))
                return Results.Json(
                    new { message = "金鑰的角色不能高過你自己的角色。" },
                    statusCode: StatusCodes.Status403Forbidden);

            // A key outlives the session that creates it (and need not expire); a hijacked session must not be able to mint one.
            if (await reauthentication.RequireAsync(context, cancellationToken) is { } refused) return refused;

            var issued = await repository.CreateAsync(
                new ApiKeyDraft(
                    request.Name!,
                    request.Role ?? UserRole.Operator,
                    request.ExpiresAt,
                    request.RunnerName,
                    request.Scope,
                    request.AllowedAddresses),
                context.Items[AuditService.ActorKey] as string,
                cancellationToken);

            await audit.RecordAsync(
                AuditAction.Create,
                "api-key",
                issued.Key.Id.ToString(),
                // What the key may do is the point of the record: role, audience, runner binding, source limit and expiry.
                $"建立 API 金鑰 {issued.Key.Name}（{issued.Key.Prefix}；角色 {issued.Key.Role}；範圍 {issued.Key.Scope}" +
                $"{(string.IsNullOrWhiteSpace(issued.Key.RunnerName) ? string.Empty : $"；執行器 {issued.Key.RunnerName}")}" +
                $"；來源限制 {(string.IsNullOrWhiteSpace(issued.Key.AllowedAddresses) ? "無" : issued.Key.AllowedAddresses.Replace('\n', ','))}" +
                $"；到期 {(issued.Key.ExpiresAt is { } expiry ? expiry.ToString("yyyy-MM-dd HH:mm 'UTC'") : "永不")}）",
                cancellationToken);

            // The plaintext appears here once only; afterwards only the hash remains.
            return Results.Created($"/api/api-keys/{issued.Key.Id}", new { key = issued.Key, token = issued.Token });
        });

        keys.MapPost("/{id:guid}/disabled", async (
            Guid id,
            DisableApiKeyRequest request,
            IApiKeyRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var key = await repository.SetDisabledAsync(id, request.IsDisabled, cancellationToken);

            if (key is null) return Results.NotFound();

            await audit.RecordAsync(
                AuditAction.Update,
                "api-key",
                id.ToString(),
                $"{(request.IsDisabled ? "停用" : "啟用")} API 金鑰 {key.Name}",
                cancellationToken);

            return Results.Ok(key);
        });

        keys.MapDelete("/{id:guid}", async (
            Guid id,
            IApiKeyRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (!await repository.DeleteAsync(id, cancellationToken)) return Results.NotFound();

            await audit.RecordAsync(AuditAction.Delete, "api-key", id.ToString(), "刪除 API 金鑰", cancellationToken);
            return Results.NoContent();
        });

        return app;
    }

    private static Dictionary<string, string[]> Validate(ApiKeyRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Name))
            errors["name"] = ["請輸入金鑰名稱。"];
        else if (request.Name.Length > 60) errors["name"] = ["金鑰名稱不可超過 60 個字元。"];
        else if (AuditText.HasControlCharacters(request.Name)) errors["name"] = ["金鑰名稱不可包含控制字元。"];

        if (request.Role is not null && !UserRole.IsValid(request.Role))
            errors["role"] = ["角色只能是 admin、operator 或 viewer。"];

        if (request.ExpiresAt is not null && request.ExpiresAt <= DateTime.UtcNow) errors["expiresAt"] = ["到期時間必須在未來。"];

        if (request.RunnerName is not null &&
            (string.IsNullOrWhiteSpace(request.RunnerName) ||
             request.RunnerName.Trim().Length > 60 ||
             request.RunnerName.Any(character =>
                 char.IsControl(character) || character is '/' or '\\' or '?' or '#' or '%')))
            errors["runnerName"] = ["執行器名稱不可為空、不可超過 60 個字元，且不可含控制字元或路徑保留字元。"];
        // A key bound to the built-in runner's name could claim every build meant for the server itself and read their secrets.
        else if (request.RunnerName is not null &&
                 string.Equals(request.RunnerName.Trim(), Runner.LocalName, StringComparison.OrdinalIgnoreCase))
            errors["runnerName"] = [$"「{Runner.LocalName}」是內建執行器的保留名稱，不能綁定到 API 金鑰。"];

        if (request.Scope is not null && request.Scope is not (ApiKeyScope.Api or ApiKeyScope.Mcp))
            errors["scope"] = ["金鑰範圍只能是 api 或 mcp；執行器範圍會由 runnerName 自動設定。"];

        var validAddresses = IpAddressRange.TryParseList(request.AllowedAddresses, out _, out var invalidAddress);
        if (request.AllowedAddresses is { Length: > 8_192 } || !validAddresses)
            errors["allowedAddresses"] =
            [
                invalidAddress is null
                    ? $"允許的來源位址最多 {IpAddressRange.MaximumEntries} 筆。"
                    : $"允許的來源位址每行一個 IPv4／IPv6 位址或 CIDR 範圍；無法解析：{invalidAddress}"
            ];

        return errors;
    }
}

/// <summary>
///     When RunnerName is set, this key can only claim and report builds for that runner. AllowedAddresses (optional, one
///     address or CIDR range per line) pins the key to those source addresses.
/// </summary>
public sealed record ApiKeyRequest(
    string? Name,
    string? Role,
    DateTime? ExpiresAt,
    string? RunnerName = null,
    string? Scope = null,
    string? AllowedAddresses = null);

public sealed record DisableApiKeyRequest(bool IsDisabled);