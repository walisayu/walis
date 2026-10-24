using System.Data;
using System.Globalization;
using Dapper;

namespace Walis.Core.Database;

/// <summary>
///     Reads the database clock (<see cref="ISqlDialect.SelectUtcNow" />) and guarantees the result is UTC with Kind=Utc.
///     A plain <c>ExecuteScalarAsync&lt;DateTime&gt;</c> trips over this: SQLite returns a string with a trailing <c>Z</c>,
///     .NET parses it into local time (Kind=Local), and writing it back is off by the time zone, 8 hours in Taipei.
///     Leases, claims and throttling all compare it with UTC times in the database, so it must go through here.
/// </summary>
public static class DatabaseClock
{
    public static async Task<DateTime> ReadUtcNowAsync(
        this IDbConnection connection,
        ISqlDialect dialect,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var raw = await connection.ExecuteScalarAsync<object>(new CommandDefinition(
            dialect.SelectUtcNow,
            transaction: transaction,
            cancellationToken: cancellationToken));
        return ToUtc(raw);
    }

    public static DateTime ToUtc(object? raw)
    {
        return raw switch
        {
            DateTime { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
            DateTime value => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            DateTimeOffset offset => offset.UtcDateTime,
            string text => DateTime.Parse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            _ => throw new DataException($"資料庫時鐘回傳了無法解析的值：{raw?.GetType().Name ?? "null"}")
        };
    }
}