namespace Walis.Core.Database;

public sealed class SqlServerDialect : SqlDialectBase
{
    public override DatabaseProvider Provider => DatabaseProvider.SqlServer;

    public override string SelectUtcNow => "SELECT SYSUTCDATETIME();";

    // SQL Server has no SKIP LOCKED; use table hints instead: lock the rows to update and skip rows locked by others.
    //
    // READCOMMITTED must be specified together with it: READPAST only works under READ COMMITTED or REPEATABLE READ,
    // and the connection pool leaves the previous Serializable transaction's isolation level on the session; without pinning it in the hint,
    // this SELECT blows up at random depending on who used the connection last.
    public override string SkipLockedHint => " WITH (READCOMMITTED, UPDLOCK, READPAST, ROWLOCK)";

    public override string ForUpdateHint => " WITH (UPDLOCK, HOLDLOCK, ROWLOCK)";

    public override string CountAll => "COUNT_BIG(1)";

    public override string ListColumnsSql =>
        "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @Table";

    public override string ListIndexesSql =>
        "SELECT name FROM sys.indexes WHERE name IS NOT NULL";

    public override string Limit(string count)
    {
        return $"OFFSET 0 ROWS FETCH NEXT {count} ROWS ONLY";
    }

    public override string Page(string count, string offset)
    {
        return $"OFFSET {offset} ROWS FETCH NEXT {count} ROWS ONLY";
    }

    public override string SecondsBetween(string later, string earlier)
    {
        return $"(DATEDIFF_BIG(MILLISECOND, {earlier}, {later}) / CAST(1000.0 AS float))";
    }

    public override string DateText(string column)
    {
        return $"CONVERT(char(10), {column}, 23)";
    }

    public override string Concat(string left, string right)
    {
        return $"({left} + {right})";
    }

    public override string Length(string expression)
    {
        return $"LEN({expression})";
    }

    public override string CastToText(string expression)
    {
        return $"CAST({expression} AS nvarchar(64))";
    }

    public override string AddColumn(string table, ColumnDefinition column)
    {
        return $"ALTER TABLE {table} ADD {ColumnClause(column)}";
    }

    public override string Upsert(
        string table,
        IReadOnlyList<UpsertColumn> columns,
        IReadOnlyList<string> key,
        IReadOnlyList<string>? update)
    {
        var source = string.Join(", ", columns.Select(column => $"{column.Value} AS {column.Name}"));
        var match = string.Join(" AND ", key.Select(column => $"t.{column} = s.{column}"));
        var names = string.Join(", ", columns.Select(column => column.Name));
        var values = string.Join(", ", columns.Select(column => "s." + column.Name));

        var matched = update is { Count: > 0 }
            ? $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", update.Select(column => $"t.{column} = s.{column}"))}\n"
            : string.Empty;

        return $"""
                MERGE {table} WITH (HOLDLOCK) AS t
                USING (SELECT {source}) AS s ON ({match})
                {matched}WHEN NOT MATCHED THEN INSERT ({names}) VALUES ({values});
                """;
    }

    protected override string TypeOf(ColumnDefinition column)
    {
        return column.Kind switch
        {
            ColumnKind.Guid => "uniqueidentifier",
            ColumnKind.Text => "nvarchar(max)",
            ColumnKind.ShortText => $"nvarchar({column.Length})",
            // It must be datetime2(7): SYSUTCDATETIME() has 100-nanosecond precision, and storing it in datetime2(3)
            // rounds up (.0009999 -> .0010000), so a "now" just written reads back larger than now
            // and the lease and throttle "not expired yet" checks see a future time.
            ColumnKind.Timestamp => "datetime2(7)",
            ColumnKind.Int => "int",
            ColumnKind.BigInt => "bigint",
            ColumnKind.Bool => "bit",
            _ => "nvarchar(max)"
        };
    }
}