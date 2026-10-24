using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Walis.Core.Database;
using Walis.Core.Models;
using Walis.Core.Security;

namespace Walis.Core.Repositories;

public sealed class SecurityRepository(IDbConnectionFactory connectionFactory, ISecretProtector protector)
    : ISecurityRepository
{
    private const string UserColumns =
        "Id, Username, DisplayName, PasswordHash, Role, IsDisabled, TotpSecret, TotpEnabledAt, LastTotpCounter, RecoveryCodes, CreatedAt, LastLoginAt";

    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    public async Task<bool> HasAnyUserAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM Users;", cancellationToken: cancellationToken));
        return count != 0;
    }

    public async Task<IReadOnlyList<WalisUser>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var users = await connection.QueryAsync<WalisUser>(new CommandDefinition(
            $"SELECT {UserColumns} FROM Users ORDER BY Username;", cancellationToken: cancellationToken));
        return [.. users.Select(Unprotect)];
    }

    public async Task<WalisUser?> GetUserAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var user = await connection.QuerySingleOrDefaultAsync<WalisUser>(new CommandDefinition(
            $"SELECT {UserColumns} FROM Users WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken));
        return user is null ? null : Unprotect(user);
    }

    public async Task<WalisUser?> FindUserAsync(string username, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var user = await connection.QuerySingleOrDefaultAsync<WalisUser>(new CommandDefinition(
            $"SELECT {UserColumns} FROM Users WHERE LOWER(Username) = LOWER(@Username);",
            new { Username = username.Trim() },
            cancellationToken: cancellationToken));
        return user is null ? null : Unprotect(user);
    }

    /// <summary>
    ///     The TOTP secret is ciphertext in the database. An undecryptable secret reads as null: the account keeps HasOtp in the
    ///     session query (the column is not empty), so the user falls back to a recovery code and an admin resets the factor.
    /// </summary>
    private WalisUser Unprotect(WalisUser user)
    {
        if (string.IsNullOrEmpty(user.TotpSecret)) return user;

        return new WalisUser
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            PasswordHash = user.PasswordHash,
            Role = user.Role,
            IsDisabled = user.IsDisabled,
            TotpSecret = protector.Unprotect(user.TotpSecret, SecretPurposes.UserTotpSecret),
            TotpEnabledAt = user.TotpEnabledAt,
            LastTotpCounter = user.LastTotpCounter,
            RecoveryCodes = user.RecoveryCodes,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt
        };
    }

    /// <summary>
    ///     The conditional updates compare the stored secret in SQL to notice a concurrent change. Ciphertext is randomized, so
    ///     the plaintext the caller holds is first matched against what is stored, and the stored string itself goes into the WHERE.
    /// </summary>
    private async Task<string?> FindStoredTotpSecretAsync(
        IDbConnection connection,
        Guid id,
        string expectedSecret,
        CancellationToken cancellationToken)
    {
        var stored = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TotpSecret FROM Users WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken));

        if (string.IsNullOrEmpty(stored)) return null;

        return string.Equals(protector.Unprotect(stored, SecretPurposes.UserTotpSecret), expectedSecret, StringComparison.Ordinal)
            ? stored
            : null;
    }

    public async Task<WalisUser> CreateUserAsync(UserDraft draft, CancellationToken cancellationToken = default)
    {
        var user = BuildUser(draft);

        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Users (Id, Username, DisplayName, PasswordHash, Role, IsDisabled, CreatedAt, LastLoginAt)
            VALUES (@Id, @Username, @DisplayName, @PasswordHash, @Role, @IsDisabled, @CreatedAt, NULL);
            """,
            user,
            cancellationToken: cancellationToken));

        return user;
    }

    public async Task<WalisUser?> TryCreateFirstAdminAsync(
        UserDraft draft,
        CancellationToken cancellationToken = default)
    {
        const string claimName = "initial-admin";
        var claimOwner = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        using var connection = connectionFactory.Create();
        if (connection.State != ConnectionState.Open) connection.Open();

        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var claimSql = _dialect.Upsert(
            "SecurityClaims",
            [
                UpsertColumn.Parameter("Name"),
                UpsertColumn.Parameter("Owner"),
                UpsertColumn.Parameter("ClaimedAt")
            ],
            ["Name"],
            null);

        await connection.ExecuteAsync(new CommandDefinition(
            claimSql,
            new { Name = claimName, Owner = claimOwner, ClaimedAt = now },
            transaction,
            cancellationToken: cancellationToken));

        var owner = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT Owner FROM SecurityClaims{_dialect.ForUpdateHint} WHERE Name = @Name{_dialect.ForUpdateSuffix};",
            new { Name = claimName },
            transaction,
            cancellationToken: cancellationToken));

        if (!string.Equals(owner, claimOwner, StringComparison.Ordinal))
        {
            transaction.Rollback();
            return null;
        }

        var existing = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM Users;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        if (existing != 0)
        {
            // This process created the claim, so keep it even though this is an
            // upgraded database that already has users.  Rolling it back would
            // reopen first-admin bootstrap if the Users table were later emptied.
            transaction.Commit();
            return null;
        }

        // Password hashing is intentionally after the permanent claim check. Once setup has been
        // completed, anonymous requests to /setup must not be able to burn a PBKDF2 operation each.
        var user = BuildUser(draft with { Role = UserRole.Admin });

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Users (Id, Username, DisplayName, PasswordHash, Role, IsDisabled, CreatedAt, LastLoginAt)
            VALUES (@Id, @Username, @DisplayName, @PasswordHash, @Role, @IsDisabled, @CreatedAt, NULL);
            """,
            user,
            transaction,
            cancellationToken: cancellationToken));

        transaction.Commit();
        return user;
    }

    public async Task<WalisUser?> UpdateProfileAsync(Guid id, UserProfileDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Users SET DisplayName = @DisplayName, Role = @Role, IsDisabled = @IsDisabled WHERE Id = @Id;",
            new
            {
                Id = id,
                DisplayName = draft.DisplayName.Trim(),
                Role = UserRole.IsValid(draft.Role) ? draft.Role : UserRole.Viewer,
                draft.IsDisabled
            },
            cancellationToken: cancellationToken));

        return affected == 0 ? null : await GetUserAsync(id, cancellationToken);
    }

    public async Task<UserMutationResult> UpdateProfilePreservingAdminAsync(
        Guid id,
        UserProfileDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        Open(connection);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await LockAccountInvariantAsync(connection, transaction, cancellationToken);

        var current = await connection.QuerySingleOrDefaultAsync<WalisUser>(new CommandDefinition(
            $"SELECT {UserColumns} FROM Users WHERE Id = @Id;",
            new { Id = id },
            transaction,
            cancellationToken: cancellationToken));

        if (current is null)
        {
            transaction.Rollback();
            return new UserMutationResult(UserMutationStatus.NotFound);
        }

        var normalizedRole = UserRole.IsValid(draft.Role) ? draft.Role : UserRole.Viewer;
        var losesAdmin = current is { Role: UserRole.Admin, IsDisabled: false } &&
                         (normalizedRole != UserRole.Admin || draft.IsDisabled);

        if (losesAdmin && await CountActiveAdminsAsync(connection, transaction, cancellationToken) <= 1)
        {
            transaction.Rollback();
            return new UserMutationResult(UserMutationStatus.LastActiveAdmin, Previous: current);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Users SET DisplayName = @DisplayName, Role = @Role, IsDisabled = @IsDisabled WHERE Id = @Id;",
            new
            {
                Id = id,
                DisplayName = draft.DisplayName.Trim(),
                Role = normalizedRole,
                draft.IsDisabled
            },
            transaction,
            cancellationToken: cancellationToken));

        // A role change in either direction changes the session's effective permissions (GetSession JOINs the current Users.Role).
        // It must be revoked in the same transaction as the profile update; no window may exist where the update is committed and the delete has not run.
        if (draft.IsDisabled || !string.Equals(normalizedRole, current.Role, StringComparison.Ordinal))
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM Sessions WHERE UserId = @UserId;",
                new { UserId = id },
                transaction,
                cancellationToken: cancellationToken));

        var updated = await connection.QuerySingleAsync<WalisUser>(new CommandDefinition(
            $"SELECT {UserColumns} FROM Users WHERE Id = @Id;",
            new { Id = id },
            transaction,
            cancellationToken: cancellationToken));

        transaction.Commit();
        return new UserMutationResult(UserMutationStatus.Success, updated, current);
    }

    public async Task<int> CountAdminsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(1) FROM Users WHERE Role = @Role AND IsDisabled = {_dialect.Boolean(false)};",
            new { Role = UserRole.Admin },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> SetTotpAsync(
        Guid id,
        string? secret,
        DateTime? enabledAt,
        IReadOnlyList<string>? recoveryCodes,
        CancellationToken cancellationToken = default)
    {
        var hashed = HashRecoveryCodes(recoveryCodes);

        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Users
            SET TotpSecret = @Secret,
                TotpEnabledAt = @EnabledAt,
                LastTotpCounter = CASE WHEN @EnabledAt IS NULL THEN NULL ELSE LastTotpCounter END,
                RecoveryCodes = @RecoveryCodes
            WHERE Id = @Id;
            """,
            new
            {
                Id = id, Secret = protector.Protect(secret, SecretPurposes.UserTotpSecret), EnabledAt = enabledAt,
                RecoveryCodes = hashed
            },
            cancellationToken: cancellationToken));

        return affected != 0;
    }

    public async Task<bool> BeginTotpEnrollmentAsync(
        Guid id, string expectedPasswordHash, string secret, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE Users SET TotpSecret = @Secret, LastTotpCounter = NULL, RecoveryCodes = NULL
             WHERE Id = @Id AND PasswordHash = @ExpectedPasswordHash
               AND TotpEnabledAt IS NULL AND IsDisabled = {_dialect.Boolean(false)};
             """,
            new
            {
                Id = id, ExpectedPasswordHash = expectedPasswordHash,
                Secret = protector.Protect(secret, SecretPurposes.UserTotpSecret)
            },
            cancellationToken: cancellationToken)) == 1;
    }

    public async Task<bool> EnableTotpAsync(
        Guid id, string expectedPasswordHash, string secret, long counter,
        IReadOnlyList<string> recoveryCodes, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var stored = await FindStoredTotpSecretAsync(connection, id, secret, cancellationToken);

        if (stored is null) return false;

        return await connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE Users
             SET TotpEnabledAt = @EnabledAt, LastTotpCounter = @Counter, RecoveryCodes = @RecoveryCodes
             WHERE Id = @Id AND PasswordHash = @ExpectedPasswordHash AND TotpSecret = @Secret
               AND TotpEnabledAt IS NULL AND IsDisabled = {_dialect.Boolean(false)}
               AND (LastTotpCounter IS NULL OR LastTotpCounter < @Counter);
             """,
            new
            {
                Id = id, ExpectedPasswordHash = expectedPasswordHash, Secret = stored,
                EnabledAt = DateTime.UtcNow, Counter = counter, RecoveryCodes = HashRecoveryCodes(recoveryCodes)
            },
            cancellationToken: cancellationToken)) == 1;
    }

    public async Task<bool> ConsumeTotpCodeAsync(
        Guid id,
        string code,
        CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(id, cancellationToken);
        if (user is null ||
            !Totp.TryVerify(user.TotpSecret, code, out var counter) ||
            user.LastTotpCounter >= counter)
            return false;

        using var connection = connectionFactory.Create();
        var stored = await FindStoredTotpSecretAsync(connection, id, user.TotpSecret!, cancellationToken);

        if (stored is null) return false;

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Users
            SET LastTotpCounter = @Counter
            WHERE Id = @Id AND TotpSecret = @Secret
              AND (LastTotpCounter IS NULL OR LastTotpCounter < @Counter);
            """,
            new { Id = id, Secret = stored, Counter = counter },
            cancellationToken: cancellationToken));

        // Of concurrent requests in the same time-step, at most one can advance the counter.
        return affected == 1;
    }

    public async Task<bool> ConsumeRecoveryCodeAsync(Guid id, string code,
        CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(id, cancellationToken);

        if (user is null || string.IsNullOrWhiteSpace(user.RecoveryCodes)) return false;

        var normalized = Totp.NormalizeRecoveryCode(code);
        var remaining = new List<string>();
        var matched = false;

        foreach (var hash in user.RecoveryCodes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!matched && PasswordHasher.Verify(normalized, hash))
            {
                matched = true;
                continue;
            }

            remaining.Add(hash);
        }

        if (!matched) return false;

        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Users SET RecoveryCodes = @RecoveryCodes WHERE Id = @Id AND RecoveryCodes = @PreviousRecoveryCodes;",
            new
            {
                Id = id,
                RecoveryCodes = remaining.Count == 0 ? null : string.Join('\n', remaining),
                PreviousRecoveryCodes = user.RecoveryCodes
            },
            cancellationToken: cancellationToken));

        // If another request already consumed or reset the backup codes, the compare-and-swap fails; the same code must never succeed twice.
        return affected == 1;
    }

    public async Task<bool> ChangePasswordAsync(Guid userId, string password,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Users SET PasswordHash = @PasswordHash WHERE Id = @Id;",
            new { Id = userId, PasswordHash = PasswordHasher.Hash(password) },
            cancellationToken: cancellationToken));
        return affected != 0;
    }

    public async Task<bool> ChangePasswordIfCurrentAsync(
        Guid userId,
        string expectedPasswordHash,
        string password,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Users SET PasswordHash = @PasswordHash WHERE Id = @Id AND PasswordHash = @ExpectedPasswordHash;",
            new
            {
                Id = userId,
                ExpectedPasswordHash = expectedPasswordHash,
                PasswordHash = PasswordHasher.Hash(password)
            },
            cancellationToken: cancellationToken));
        return affected == 1;
    }

    public async Task<bool> DeleteUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();

        // Clear the sessions first: otherwise the JOIN finds no user but the rows linger in the table forever.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Sessions WHERE UserId = @UserId;",
            new { UserId = userId },
            cancellationToken: cancellationToken));

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Users WHERE Id = @Id;",
            new { Id = userId },
            cancellationToken: cancellationToken));
        return affected != 0;
    }

    public async Task<UserMutationResult> DeleteUserPreservingAdminAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        Open(connection);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await LockAccountInvariantAsync(connection, transaction, cancellationToken);

        var current = await connection.QuerySingleOrDefaultAsync<WalisUser>(new CommandDefinition(
            $"SELECT {UserColumns} FROM Users WHERE Id = @Id;",
            new { Id = userId },
            transaction,
            cancellationToken: cancellationToken));

        if (current is null)
        {
            transaction.Rollback();
            return new UserMutationResult(UserMutationStatus.NotFound);
        }

        var users = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM Users;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        if (users <= 1)
        {
            transaction.Rollback();
            return new UserMutationResult(UserMutationStatus.LastUser, Previous: current);
        }

        if (current is { Role: UserRole.Admin, IsDisabled: false } &&
            await CountActiveAdminsAsync(connection, transaction, cancellationToken) <= 1)
        {
            transaction.Rollback();
            return new UserMutationResult(UserMutationStatus.LastActiveAdmin, Previous: current);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Sessions WHERE UserId = @UserId;",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Users WHERE Id = @Id;",
            new { Id = userId },
            transaction,
            cancellationToken: cancellationToken));

        transaction.Commit();
        return new UserMutationResult(UserMutationStatus.Success, Previous: current);
    }

    public async Task<string> CreateOtpChallengeAsync(
        Guid userId,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var token = PasswordHasher.CreateToken();
        var storedToken = HashToken(token, "otp-challenge");

        using var connection = connectionFactory.Create();

        // Clean out expired ones on the way, so no separate job is needed.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM OtpChallenges WHERE ExpiresAt <= @Now;",
            new { Now = now },
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO OtpChallenges (Token, UserId, CreatedAt, ExpiresAt)
            VALUES (@Token, @UserId, @Now, @ExpiresAt);
            """,
            new { Token = storedToken, UserId = userId, Now = now, ExpiresAt = now.Add(lifetime) },
            cancellationToken: cancellationToken));

        return token;
    }

    public async Task<Guid?> ConsumeOtpChallengeAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;

        var storedToken = HashToken(token, "otp-challenge");

        using var connection = connectionFactory.Create();
        var userId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT UserId FROM OtpChallenges WHERE Token = @Token AND ExpiresAt > @Now;",
            new { Token = storedToken, Now = DateTime.UtcNow },
            cancellationToken: cancellationToken));

        // Delete once used, expired or not.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM OtpChallenges WHERE Token = @Token AND ExpiresAt > @Now;",
            new { Token = storedToken, Now = DateTime.UtcNow },
            cancellationToken: cancellationToken));

        // Even if the SELECT and DELETE interleave, only the request that actually deleted the row gets to use it.
        return affected == 1 ? userId : null;
    }

    public Task<AuthSession> CreateSessionAsync(Guid userId, TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        return CreateSessionAsync(userId, lifetime, false, cancellationToken);
    }

    public async Task<AuthSession> CreateSessionAsync(Guid userId, TimeSpan lifetime, bool isBearer,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var token = PasswordHasher.CreateToken();
        var storedToken = HashToken(token, "session");

        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Sessions (Token, UserId, CreatedAt, ExpiresAt, IsBearer)
            VALUES (@Token, @UserId, @CreatedAt, @ExpiresAt, @IsBearer);
            """,
            new { Token = storedToken, UserId = userId, CreatedAt = now, ExpiresAt = now.Add(lifetime), IsBearer = isBearer },
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Users SET LastLoginAt = @Now WHERE Id = @Id;",
            new { Id = userId, Now = now },
            cancellationToken: cancellationToken));

        var owner = await connection.QuerySingleAsync<SessionOwner>(
            new CommandDefinition(
                "SELECT Username, DisplayName, Role FROM Users WHERE Id = @Id;",
                new { Id = userId },
                cancellationToken: cancellationToken));

        return new AuthSession
        {
            Token = token,
            UserId = userId,
            CreatedAt = now,
            ExpiresAt = now.Add(lifetime),
            Username = owner.Username,
            DisplayName = owner.DisplayName,
            Role = owner.Role,
            IsBearer = isBearer
        };
    }

    public async Task<AuthSession?> GetSessionAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;

        var storedToken = HashToken(token, "session");
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<AuthSession>(new CommandDefinition(
            $"""
             SELECT @PresentedToken AS Token, s.UserId, s.CreatedAt, s.ExpiresAt, s.IsBearer, u.Username, u.DisplayName, u.Role,
                    CASE WHEN u.TotpSecret IS NOT NULL AND TRIM(u.TotpSecret) <> '' AND u.TotpEnabledAt IS NOT NULL
                         THEN 1 ELSE 0 END AS HasOtp
             FROM Sessions s
             INNER JOIN Users u ON u.Id = s.UserId
             WHERE s.Token = @Token AND s.ExpiresAt > @Now AND u.IsDisabled = {_dialect.Boolean(false)};
             """,
            new { Token = storedToken, PresentedToken = token, Now = DateTime.UtcNow },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> DeleteSessionAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return false;

        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Sessions WHERE Token = @Token;",
            new { Token = HashToken(token, "session") },
            cancellationToken: cancellationToken));
        return affected != 0;
    }

    public async Task<int> DeleteSessionsForUserAsync(
        Guid userId,
        string? exceptToken = null,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            exceptToken is null
                ? "DELETE FROM Sessions WHERE UserId = @UserId;"
                : "DELETE FROM Sessions WHERE UserId = @UserId AND Token <> @Token;",
            new
            {
                UserId = userId,
                Token = exceptToken is null ? string.Empty : HashToken(exceptToken, "session")
            },
            cancellationToken: cancellationToken));
    }

    public async Task<int> PurgeExpiredSessionsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Sessions WHERE ExpiresAt <= @Now;",
            new { Now = DateTime.UtcNow },
            cancellationToken: cancellationToken));
    }

    private static string? HashRecoveryCodes(IReadOnlyList<string>? codes)
    {
        return codes is null
            ? null
            : string.Join('\n', codes.Select(code => PasswordHasher.Hash(Totp.NormalizeRecoveryCode(code), PasswordHasher.RecoveryCodeIterations)));
    }

    private async Task LockAccountInvariantAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string name = "initial-admin";
        var claimSql = _dialect.Upsert(
            "SecurityClaims",
            [
                UpsertColumn.Parameter("Name"),
                UpsertColumn.Parameter("Owner"),
                UpsertColumn.Parameter("ClaimedAt")
            ],
            ["Name"],
            null);

        await connection.ExecuteAsync(new CommandDefinition(
            claimSql,
            new { Name = name, Owner = "account-invariant", ClaimedAt = DateTime.UtcNow },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT Owner FROM SecurityClaims{_dialect.ForUpdateHint} WHERE Name = @Name{_dialect.ForUpdateSuffix};",
            new { Name = name },
            transaction,
            cancellationToken: cancellationToken));
    }

    private async Task<int> CountActiveAdminsAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        CancellationToken cancellationToken)
    {
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(1) FROM Users WHERE Role = @Role AND IsDisabled = {_dialect.Boolean(false)};",
            new { Role = UserRole.Admin },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static void Open(IDbConnection connection)
    {
        if (connection.State != ConnectionState.Open) connection.Open();
    }

    private static WalisUser BuildUser(UserDraft draft)
    {
        return new WalisUser
        {
            Id = Guid.NewGuid(),
            Username = draft.Username.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(draft.DisplayName)
                ? draft.Username.Trim()
                : draft.DisplayName.Trim(),
            PasswordHash = PasswordHasher.Hash(draft.Password),
            Role = UserRole.IsValid(draft.Role) ? draft.Role : UserRole.Viewer,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    ///     Session/challenge tokens have 256 bits of CSPRNG entropy, so a
    ///     domain-separated fast hash protects database-at-rest disclosure without
    ///     turning authentication into a password-hashing CPU exhaustion vector.
    /// </summary>
    private static string HashToken(string token, string purpose)
    {
        var bytes = Encoding.UTF8.GetBytes($"walis:{purpose}\0{token}");

        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed record SessionOwner(string Username, string DisplayName, string Role);
}