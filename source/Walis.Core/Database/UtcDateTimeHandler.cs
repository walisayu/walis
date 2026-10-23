using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Walis.Core.Database;

/// <summary>
///     SQLite, MySQL and SQL Server store times without a time zone, so they read back as Unspecified
///     and the frontend mistakes them for local time. This writes UTC uniformly and marks values as UTC on read.
/// </summary>
public sealed class UtcDateTimeHandler : SqlMapper.TypeHandler<DateTime>
{
    public override DateTime Parse(object value)
    {
        return value switch
        {
            DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            string text => DateTime.SpecifyKind(DateTime.Parse(text), DateTimeKind.Utc),
            _ => throw new DataException($"無法將 {value.GetType()} 轉換為 DateTime。")
        };
    }

    // Npgsql's timestamptz only accepts Kind=Utc; times of unknown origin are treated as UTC before sending.
    public override void SetValue(IDbDataParameter parameter, DateTime value)
    {
        parameter.Value = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };
    }

    /// <summary>
    ///     SqlClient treats DateTime parameters as legacy <c>datetime</c> by default (1/300-second precision that **rounds up**),
    ///     so a "now" just written reads back larger than the original, the lease and throttle "not expired yet" checks see a future time,
    ///     and builds get blocked by their own maintenance checks. No column type can fix that; the parameter type is what must change.
    /// </summary>
    internal static void UseHighPrecision(IDbDataParameter parameter)
    {
        if (parameter is SqlParameter sqlParameter) sqlParameter.SqlDbType = SqlDbType.DateTime2;
    }
}

public sealed class NullableUtcDateTimeHandler : SqlMapper.TypeHandler<DateTime?>
{
    private static readonly UtcDateTimeHandler Inner = new();

    public override DateTime? Parse(object? value)
    {
        return value is null or DBNull ? null : Inner.Parse(value);
    }

    public override void SetValue(IDbDataParameter parameter, DateTime? value)
    {
        if (value is null)
        {
            parameter.Value = DBNull.Value;
            return;
        }

        Inner.SetValue(parameter, value.Value);
    }
}

/// <summary>
///     SQLite stores Guid as TEXT and MySQL as char(36), and Dapper's default conversion cannot turn the string back into a Guid,
///     so reads and writes are handled explicitly; PostgreSQL and SQL Server have native types and pass through.
/// </summary>
public sealed class GuidTypeHandler : SqlMapper.TypeHandler<Guid>
{
    public override Guid Parse(object value)
    {
        return value switch
        {
            Guid guid => guid,
            string text => Guid.Parse(text),
            byte[] bytes => new Guid(bytes),
            _ => throw new DataException($"無法將 {value.GetType()} 轉換為 Guid。")
        };
    }

    public override void SetValue(IDbDataParameter parameter, Guid value)
    {
        parameter.Value = value;
    }
}

public sealed class NullableGuidTypeHandler : SqlMapper.TypeHandler<Guid?>
{
    private static readonly GuidTypeHandler Inner = new();

    public override Guid? Parse(object? value)
    {
        return value is null or DBNull ? null : Inner.Parse(value);
    }

    public override void SetValue(IDbDataParameter parameter, Guid? value)
    {
        if (value is null)
        {
            parameter.Value = DBNull.Value;
            return;
        }

        Inner.SetValue(parameter, value.Value);
    }
}

public static class DapperConfiguration
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;

        SqlMapper.AddTypeHandler(new UtcDateTimeHandler());
        SqlMapper.AddTypeHandler(new NullableUtcDateTimeHandler());
        SqlMapper.AddTypeHandler(new GuidTypeHandler());
        SqlMapper.AddTypeHandler(new NullableGuidTypeHandler());
        _registered = true;
    }
}