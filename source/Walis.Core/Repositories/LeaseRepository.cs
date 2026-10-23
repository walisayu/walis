using System.Data;
using Dapper;
using Walis.Core.Database;

namespace Walis.Core.Repositories;

public sealed class LeaseRepository(IDbConnectionFactory connectionFactory) : ILeaseRepository
{
    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    public async Task<bool> TryAcquireAsync(
        string name,
        string owner,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var now = await connection.ReadUtcNowAsync(_dialect, cancellationToken: cancellationToken);
        var expires = now.Add(duration);

        // Try to grab it first: if this node already holds it, or someone else's lease has expired, it can be updated to ours.
        // This is a single conditional UPDATE, which all four databases guarantee to be atomic.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Leases
            SET Owner = @Owner, AcquiredAt = @Now, ExpiresAt = @Expires
            WHERE Name = @Name AND (Owner = @Owner OR ExpiresAt <= @Now);
            """,
            new { Name = name, Owner = owner, Now = now, Expires = expires },
            cancellationToken: cancellationToken));

        if (affected != 0) return true;

        // Insert only when the lease row does not exist; when two nodes insert at once the primary key rejects the slower one.
        try
        {
            var sql = _dialect.Upsert(
                "Leases",
                [
                    UpsertColumn.Parameter("Name"),
                    UpsertColumn.Parameter("Owner"),
                    new UpsertColumn("AcquiredAt", "@Now"),
                    new UpsertColumn("ExpiresAt", "@Expires")
                ],
                ["Name"],
                null);

            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                sql,
                new { Name = name, Owner = owner, Now = now, Expires = expires },
                cancellationToken: cancellationToken));

            return inserted != 0 && await IsOwnerAsync(connection, name, owner, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> RenewAsync(
        string name,
        string owner,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var now = await connection.ReadUtcNowAsync(_dialect, cancellationToken: cancellationToken);
        var expires = now.Add(duration);

        // Do not let an old process revive its lease after a pause. During that expired interval,
        // another node may already have passed a lease-protected boundary even if it has not yet
        // changed Owner. Long-running operations must use this stricter renewal path.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Leases
            SET AcquiredAt = @Now, ExpiresAt = @Expires
            WHERE Name = @Name AND Owner = @Owner AND ExpiresAt > @Now;
            """,
            new { Name = name, Owner = owner, Now = now, Expires = expires },
            cancellationToken: cancellationToken));

        return affected == 1;
    }

    public async Task ReleaseAsync(string name, string owner, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var now = await connection.ReadUtcNowAsync(_dialect, cancellationToken: cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Leases SET ExpiresAt = @Now WHERE Name = @Name AND Owner = @Owner;",
            new { Name = name, Owner = owner, Now = now },
            cancellationToken: cancellationToken));
    }

    public async Task<string?> GetOwnerAsync(string name, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var now = await connection.ReadUtcNowAsync(_dialect, cancellationToken: cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Owner FROM Leases WHERE Name = @Name AND ExpiresAt > @Now;",
            new { Name = name, Now = now },
            cancellationToken: cancellationToken));
    }

    private static async Task<bool> IsOwnerAsync(
        IDbConnection connection,
        string name,
        string owner,
        CancellationToken cancellationToken)
    {
        var current = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Owner FROM Leases WHERE Name = @Name;",
            new { Name = name },
            cancellationToken: cancellationToken));

        return string.Equals(current, owner, StringComparison.Ordinal);
    }
}