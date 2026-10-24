using Walis.Core.Models;

namespace Walis.Core.Repositories;

public interface ISecurityRepository
{
    Task<bool> HasAnyUserAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WalisUser>> ListUsersAsync(CancellationToken cancellationToken = default);

    Task<WalisUser?> FindUserAsync(string username, CancellationToken cancellationToken = default);

    Task<WalisUser?> GetUserAsync(Guid id, CancellationToken cancellationToken = default);

    Task<WalisUser> CreateUserAsync(UserDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Atomically creates the first admin using a permanent claim in the database. Returns null when someone exists or another node already took the claim.
    /// </summary>
    Task<WalisUser?> TryCreateFirstAdminAsync(UserDraft draft, CancellationToken cancellationToken = default);

    Task<WalisUser?> UpdateProfileAsync(Guid id, UserProfileDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Updates an account within a single serializable transaction and guarantees at least one enabled admin remains; when the role or disabled
    ///     state changes, the same transaction revokes all of the account's sessions so an existing session cannot gain privileges without re-authenticating.
    ///     Every admin-side profile change should go through this to avoid the count-then-update race across nodes.
    /// </summary>
    Task<UserMutationResult> UpdateProfilePreservingAdminAsync(
        Guid id,
        UserProfileDraft draft,
        CancellationToken cancellationToken = default);

    Task<int> CountAdminsAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes or clears the OTP settings; a null secret means disabled.</summary>
    Task<bool> SetTotpAsync(
        Guid id,
        string? secret,
        DateTime? enabledAt,
        IReadOnlyList<string>? recoveryCodes,
        CancellationToken cancellationToken = default);

    /// <summary>Updates the pending secret only while the account has no OTP enabled and the password is unchanged.</summary>
    Task<bool> BeginTotpEnrollmentAsync(
        Guid id, string expectedPasswordHash, string secret, CancellationToken cancellationToken = default);

    /// <summary>Atomically enables the just-verified pending secret and consumes the code; refused when already enabled or the setup changed.</summary>
    Task<bool> EnableTotpAsync(
        Guid id, string expectedPasswordHash, string secret, long counter,
        IReadOnlyList<string> recoveryCodes, CancellationToken cancellationToken = default);

    /// <summary>Verifies and consumes one backup code; it becomes invalid after success.</summary>
    Task<bool> ConsumeRecoveryCodeAsync(Guid id, string code, CancellationToken cancellationToken = default);

    /// <summary>Atomically verifies and consumes a TOTP time-step; a counter already used by the account cannot be reused.</summary>
    Task<bool> ConsumeTotpCodeAsync(Guid id, string code, CancellationToken cancellationToken = default);

    Task<bool> ChangePasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Updates only while the password hash is still the value the caller just verified. For users changing their own password, so that if an admin
    ///     reset the password between verification and write, the holder of the old password cannot overwrite the admin's reset.
    /// </summary>
    Task<bool> ChangePasswordIfCurrentAsync(
        Guid userId,
        string expectedPasswordHash,
        string password,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Deletes an account within a single serializable transaction, protecting the last account and the last enabled admin.</summary>
    Task<UserMutationResult> DeleteUserPreservingAdminAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a one-time-password challenge and returns the token. Stored in the database so another node can verify it.</summary>
    Task<string> CreateOtpChallengeAsync(Guid userId, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>Consumes the challenge; returns the user id on success, null when expired or missing.</summary>
    Task<Guid?> ConsumeOtpChallengeAsync(string? token, CancellationToken cancellationToken = default);

    Task<AuthSession> CreateSessionAsync(Guid userId, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>Creates a session flagged for one channel: a bearer session for CLIs, otherwise the browser cookie session.</summary>
    Task<AuthSession> CreateSessionAsync(Guid userId, TimeSpan lifetime, bool isBearer,
        CancellationToken cancellationToken = default);

    Task<AuthSession?> GetSessionAsync(string token, CancellationToken cancellationToken = default);

    Task<bool> DeleteSessionAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Revokes every session of a user; called on password change and one-time-password reset,
    ///     otherwise a stolen token stays valid until it expires. <paramref name="exceptToken" /> is the operator's own current session, which is kept.
    /// </summary>
    Task<int> DeleteSessionsForUserAsync(Guid userId, string? exceptToken = null,
        CancellationToken cancellationToken = default);

    Task<int> PurgeExpiredSessionsAsync(CancellationToken cancellationToken = default);
}

public enum UserMutationStatus
{
    Success,
    NotFound,
    LastUser,
    LastActiveAdmin
}

public sealed record UserMutationResult(
    UserMutationStatus Status,
    WalisUser? Current = null,
    WalisUser? Previous = null);