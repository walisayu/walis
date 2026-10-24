using Walis.Api.Services;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Endpoints;

public static class AccountEndpoints
{
    public const int MaximumPasswordLength = 256;

    /// <summary>New passwords only; an existing shorter password keeps working until it is changed.</summary>
    public const int MinimumPasswordLength = 12;

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/api/accounts").WithTags("Accounts");

        accounts.MapGet("/", async (ISecurityRepository security, CancellationToken cancellationToken) =>
            Results.Ok((await security.ListUsersAsync(cancellationToken)).Select(Present)));

        accounts.MapPost("/", async (
            CredentialsRequest request,
            HttpContext context,
            ISecurityRepository security,
            ISettingsRepository settings,
            AuditService audit,
            SecurityEventService securityEvents,
            IReauthentication reauthentication,
            CancellationToken cancellationToken) =>
        {
            var configuration = await settings.GetAsync(cancellationToken);

            if (configuration.RequireSignIn && context.Items["walis-session"] is null)
                return Results.Json(new { message = "請先登入。" }, statusCode: StatusCodes.Status401Unauthorized);

            var errors = ValidateCredentials(request);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            if (await security.FindUserAsync(request.Username!, cancellationToken) is not null)
                return Results.Conflict(new { message = "已有同名的帳號。" });

            // A new administrator is a second key to the whole system; a hijacked session alone must not mint one.
            if (request.Role == UserRole.Admin &&
                await reauthentication.RequireAsync(context, cancellationToken) is { } refusedCreate)
                return refusedCreate;

            var user = await security.CreateUserAsync(
                new UserDraft(
                    request.Username!,
                    request.DisplayName ?? request.Username!,
                    request.Password!,
                    request.Role ?? UserRole.Viewer),
                cancellationToken);

            await audit.RecordAsync(
                AuditAction.Create,
                "user",
                user.Id.ToString(),
                $"新增帳號 {user.Username}（{user.Role}）",
                cancellationToken);

            if (user.Role == UserRole.Admin) await securityEvents.AdministratorGrantedAsync(user.Username, true, cancellationToken);

            return Results.Created($"/api/accounts/{user.Id}", Present(user));
        });

        accounts.MapPut("/{id:guid}", async (
            Guid id,
            ProfileRequest request,
            HttpContext context,
            ISecurityRepository security,
            AuditService audit,
            SecurityEventService securityEvents,
            IReauthentication reauthentication,
            CancellationToken cancellationToken) =>
        {
            var user = await security.GetUserAsync(id, cancellationToken);

            if (user is null) return Results.NotFound();

            if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 200 ||
                AuditText.HasControlCharacters(request.DisplayName))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["displayName"] = ["請輸入顯示名稱，且不可超過 200 個字元。"]
                });

            if (!UserRole.IsValid(request.Role))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["role"] = ["角色只能是 admin、operator 或 viewer。"]
                });

            var self = context.Items["walis-session"] as AuthSession;

            if (self?.UserId == id && request.IsDisabled) return Results.Conflict(new { message = "不能停用目前登入的帳號。" });

            if (user.Role != UserRole.Admin && request.Role == UserRole.Admin &&
                await reauthentication.RequireAsync(context, cancellationToken) is { } refusedPromotion)
                return refusedPromotion;

            var mutation = await security.UpdateProfilePreservingAdminAsync(
                id,
                new UserProfileDraft(request.DisplayName, request.Role!, request.IsDisabled),
                cancellationToken);

            if (mutation.Status == UserMutationStatus.LastActiveAdmin)
                return Results.Conflict(new { message = "至少要保留一個啟用中的管理員帳號。" });

            if (mutation.Status == UserMutationStatus.NotFound || mutation.Current is null) return Results.NotFound();

            var updated = mutation.Current;

            // The repository updates the role and revokes sessions in one transaction, closing the race window where the new privilege
            // is live but the session is not yet deleted; do not split this into a second database operation here.

            await audit.RecordAsync(
                AuditAction.Update,
                "user",
                id.ToString(),
                $"更新帳號 {updated.Username}（{updated.Role}{(updated.IsDisabled ? "、已停用" : string.Empty)}）",
                cancellationToken);

            if (user.Role != UserRole.Admin && updated.Role == UserRole.Admin)
                await securityEvents.AdministratorGrantedAsync(updated.Username, false, cancellationToken);

            return Results.Ok(Present(updated));
        });

        accounts.MapPost("/{id:guid}/password", async (
            Guid id,
            PasswordRequest request,
            HttpContext context,
            ISecurityRepository security,
            AuditService audit,
            IReauthentication reauthentication,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.NewPassword) ||
                request.NewPassword.Length < MinimumPasswordLength || request.NewPassword.Length > MaximumPasswordLength)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["newPassword"] = [$"密碼須為 {MinimumPasswordLength} 到 {MaximumPasswordLength} 個字元。"]
                });

            var user = await security.GetUserAsync(id, cancellationToken);

            if (user is null) return Results.NotFound();

            if (await reauthentication.RequireAsync(context, cancellationToken) is { } refusedReset) return refusedReset;

            if (!await security.ChangePasswordAsync(id, request.NewPassword, cancellationToken)) return Results.NotFound();

            // An admin resetting someone's password usually means the account was compromised, so every old session is invalidated.
            await security.DeleteSessionsForUserAsync(id, cancellationToken: cancellationToken);
            await audit.RecordAsync(AuditAction.Update, "user", id.ToString(), $"重設 {user.Username} 的密碼",
                cancellationToken);
            return Results.NoContent();
        });

        accounts.MapPost("/{id:guid}/otp/reset", async (
            Guid id,
            HttpContext context,
            ISecurityRepository security,
            AuditService audit,
            IReauthentication reauthentication,
            CancellationToken cancellationToken) =>
        {
            var user = await security.GetUserAsync(id, cancellationToken);

            if (user is null) return Results.NotFound();

            if (await reauthentication.RequireAsync(context, cancellationToken) is { } refusedOtpReset) return refusedOtpReset;

            await security.SetTotpAsync(id, null, null, null, cancellationToken);
            await security.DeleteSessionsForUserAsync(id, cancellationToken: cancellationToken);
            await audit.RecordAsync(AuditAction.Update, "user", id.ToString(), $"清除 {user.Username} 的一次性密碼",
                cancellationToken);

            return Results.NoContent();
        });

        accounts.MapDelete("/{id:guid}", async (
            Guid id,
            HttpContext context,
            ISecurityRepository security,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var user = await security.GetUserAsync(id, cancellationToken);

            if (user is null) return Results.NotFound();

            if (context.Items["walis-session"] is AuthSession session && session.UserId == id)
                return Results.Conflict(new { message = "不能刪除目前登入的帳號。" });

            var mutation = await security.DeleteUserPreservingAdminAsync(id, cancellationToken);

            if (mutation.Status == UserMutationStatus.LastUser)
                return Results.Conflict(new { message = "至少要保留一個管理帳號。" });

            if (mutation.Status == UserMutationStatus.LastActiveAdmin)
                return Results.Conflict(new { message = "至少要保留一個啟用中的管理員帳號。" });

            if (mutation.Status == UserMutationStatus.NotFound) return Results.NotFound();

            await audit.RecordAsync(
                AuditAction.Delete,
                "user",
                id.ToString(),
                $"刪除帳號 {mutation.Previous?.Username ?? user.Username}",
                cancellationToken);
            return Results.NoContent();
        });

        return app;
    }

    public static Dictionary<string, string[]> ValidateCredentials(CredentialsRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Username))
            errors["username"] = ["請輸入帳號。"];
        else if (request.Username.Length > 40 || request.Username.Any(char.IsWhiteSpace) ||
                 AuditText.HasControlCharacters(request.Username))
            errors["username"] = ["帳號不可包含空白或控制字元，且不超過 40 個字元。"];

        if (string.IsNullOrWhiteSpace(request.Password) ||
            request.Password.Length < MinimumPasswordLength || request.Password.Length > MaximumPasswordLength)
            errors["password"] = [$"密碼須為 {MinimumPasswordLength} 到 {MaximumPasswordLength} 個字元。"];

        if (!string.IsNullOrWhiteSpace(request.DisplayName) &&
            (request.DisplayName.Length > 200 || AuditText.HasControlCharacters(request.DisplayName)))
            errors["displayName"] = ["顯示名稱不可超過 200 個字元或包含控制字元。"];

        if (request.Role is not null && !UserRole.IsValid(request.Role))
            errors["role"] = ["角色只能是 admin、operator 或 viewer。"];

        return errors;
    }

    private static object Present(WalisUser user)
    {
        return new
        {
            user.Id,
            user.Username,
            user.DisplayName,
            user.Role,
            user.IsDisabled,
            hasOtp = user.HasOtp,
            user.TotpEnabledAt,
            user.CreatedAt,
            user.LastLoginAt
        };
    }
}

public sealed record ProfileRequest(string? DisplayName, string? Role, bool IsDisabled);