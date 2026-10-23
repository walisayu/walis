using System.Security.Cryptography;
using System.Text;
using Dapper;
using Walis.Core.Database;
using Walis.Core.Models;
using Walis.Core.Security;

namespace Walis.Core.Repositories;

public sealed class ApiKeyRepository(IDbConnectionFactory connectionFactory) : IApiKeyRepository
{
    /// <summary>The token looks like walis_&lt;prefix&gt;_&lt;secret&gt;. The prefix is for lookup; only a hash of the secret is stored.</summary>
    public const string TokenPrefix = "walis_";

    private const string HashPrefix = "sha256$";
    private const int PrefixLength = 6;
    private const int SecretLength = 24;

    private const string Columns =
        "Id, Name, Prefix, Role, Scope, IsDisabled, CreatedAt, CreatedBy, ExpiresAt, LastUsedAt, RunnerName, AllowedAddresses";

    public async Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var keys = await connection.QueryAsync<ApiKey>(new CommandDefinition(
            $"SELECT {Columns} FROM ApiKeys ORDER BY CreatedAt DESC;",
            cancellationToken: cancellationToken));

        return [.. keys];
    }

    public async Task<ApiKeyIssued> CreateAsync(
        ApiKeyDraft draft,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        var prefix = Random(6);
        var secret = Random(24);
        var token = $"{TokenPrefix}{prefix}_{secret}";

        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = draft.Name.Trim(),
            Prefix = prefix,
            Role = UserRole.IsValid(draft.Role) ? draft.Role : UserRole.Operator,
            Scope = ApiKeyScope.Normalize(draft.Scope, draft.RunnerName),
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy,
            ExpiresAt = draft.ExpiresAt,
            RunnerName = string.IsNullOrWhiteSpace(draft.RunnerName) ? null : draft.RunnerName.Trim(),
            AllowedAddresses = NormalizeAllowedAddresses(draft.AllowedAddresses)
        };

        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
                INSERT INTO ApiKeys (Id, Name, Prefix, Hash, Role, Scope, IsDisabled, CreatedAt, CreatedBy, ExpiresAt, RunnerName, AllowedAddresses)
                VALUES (@Id, @Name, @Prefix, @Hash, @Role, @Scope, 0, @CreatedAt, @CreatedBy, @ExpiresAt, @RunnerName, @AllowedAddresses);
            """,
            new
            {
                key.Id,
                key.Name,
                key.Prefix,
                Hash = HashSecret(secret),
                key.Role,
                key.Scope,
                key.CreatedAt,
                key.CreatedBy,
                key.ExpiresAt,
                key.RunnerName,
                key.AllowedAddresses
            },
            cancellationToken: cancellationToken));

        return new ApiKeyIssued(key, token);
    }

    public async Task<ApiKey?> AuthenticateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!TryParse(token, out var prefix, out var secret)) return null;

        using var connection = connectionFactory.Create();

        // The hash is queried separately so the public model does not carry it.
        var hash = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Hash FROM ApiKeys WHERE Prefix = @Prefix;",
            new { Prefix = prefix },
            cancellationToken: cancellationToken));

        // API secrets contain more than 120 bits of CSPRNG entropy, so a fast
        // one-way hash is appropriate. Password-strength PBKDF2 only made a
        // known key prefix a CPU exhaustion primitive.
        if (!VerifySecret(secret, hash)) return null;

        var key = await connection.QuerySingleOrDefaultAsync<ApiKey>(new CommandDefinition(
            $"SELECT {Columns} FROM ApiKeys WHERE Prefix = @Prefix;",
            new { Prefix = prefix },
            cancellationToken: cancellationToken));

        if (key is null || key.IsDisabled || key.IsExpired) return null;

        // The last-used time tells whether anyone still uses the key.
        var now = DateTime.UtcNow;
        await connection.ExecuteAsync(new CommandDefinition(
            """
                UPDATE ApiKeys
                SET LastUsedAt = @Now
                WHERE Id = @Id AND (LastUsedAt IS NULL OR LastUsedAt < @Threshold);
            """,
            new { key.Id, Now = now, Threshold = now.AddMinutes(-5) },
            cancellationToken: cancellationToken));

        return key with { LastUsedAt = now };
    }

    public async Task<ApiKey?> SetDisabledAsync(
        Guid id,
        bool disabled,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE ApiKeys SET IsDisabled = @Disabled WHERE Id = @Id;",
            new { Id = id, Disabled = disabled },
            cancellationToken: cancellationToken));

        return await connection.QuerySingleOrDefaultAsync<ApiKey>(new CommandDefinition(
            $"SELECT {Columns} FROM ApiKeys WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ApiKeys WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken));

        return affected != 0;
    }

    public static bool TryParse(string? token, out string prefix, out string secret)
    {
        prefix = string.Empty;
        secret = string.Empty;

        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal)) return false;

        var parts = token[TokenPrefix.Length..].Split('_', 2);

        if (parts.Length != 2 ||
            parts[0].Length != PrefixLength ||
            parts[1].Length != SecretLength ||
            !parts[0].All(IsTokenCharacter) ||
            !parts[1].All(IsTokenCharacter))
            return false;

        prefix = parts[0];
        secret = parts[1];
        return true;
    }

    /// <summary>
    ///     Stores the allowlist in canonical form (one normalized range per line) or null. The endpoint has already validated
    ///     it; an entry that still fails to parse is dropped rather than widened into "any address".
    /// </summary>
    public static string? NormalizeAllowedAddresses(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;

        var ranges = IpAddressRange.Split(configured)
            .Select(entry => IpAddressRange.TryParse(entry, out var range) ? range!.Text : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return ranges.Length == 0 ? null : string.Join('\n', ranges);
    }

    /// <summary>Lowercase alphanumerics only, so no escaping is needed in URLs or environment variables.</summary>
    private static string Random(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        return RandomNumberGenerator.GetString(alphabet, length);
    }

    private static string HashSecret(string secret)
    {
        var bytes = Encoding.ASCII.GetBytes(secret);

        try
        {
            return HashPrefix + Convert.ToBase64String(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool VerifySecret(string secret, string? stored)
    {
        if (stored is null || !stored.StartsWith(HashPrefix, StringComparison.Ordinal))
            // Older PBKDF2 API keys are intentionally rejected and disabled by
            // the database initializer; they must be rotated once after update.
            return false;

        try
        {
            var expected = Convert.FromBase64String(stored[HashPrefix.Length..]);
            var bytes = Encoding.ASCII.GetBytes(secret);

            try
            {
                var actual = SHA256.HashData(bytes);
                return expected.Length == actual.Length &&
                       CryptographicOperations.FixedTimeEquals(expected, actual);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsTokenCharacter(char value)
    {
        return value is >= 'a' and <= 'z' or >= '0' and <= '9';
    }
}