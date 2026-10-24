namespace Walis.Core.Database;

public sealed class MySqlDialect : SqlDialectBase
{
    public override DatabaseProvider Provider => DatabaseProvider.MySql;

    public override string SelectUtcNow => "SELECT UTC_TIMESTAMP(6);";

    public override string SkipLockedSuffix => " FOR UPDATE SKIP LOCKED";

    public override string ForUpdateSuffix => " FOR UPDATE";

    public override string ListColumnsSql =>
        """
        SELECT COLUMN_NAME FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @Table
        """;

    public override string ListIndexesSql =>
        """
        SELECT DISTINCT INDEX_NAME FROM information_schema.STATISTICS
        WHERE TABLE_SCHEMA = DATABASE()
        """;

    public override string SecondsBetween(string later, string earlier)
    {
        return $"(TIMESTAMPDIFF(MICROSECOND, {earlier}, {later}) / 1000000.0e0)";
    }

    public override string DateText(string column)
    {
        return $"DATE_FORMAT({column}, '%Y-%m-%d')";
    }

    // In MySQL || is logical OR by default; string concatenation needs CONCAT.
    public override string Concat(string left, string right)
    {
        return $"CONCAT({left}, {right})";
    }

    public override string CastToText(string expression)
    {
        return $"CAST({expression} AS CHAR)";
    }

    public override string Upsert(
        string table,
        IReadOnlyList<UpsertColumn> columns,
        IReadOnlyList<string> key,
        IReadOnlyList<string>? update)
    {
        var names = string.Join(", ", columns.Select(column => column.Name));
        var values = string.Join(", ", columns.Select(column => column.Value));

        // For "leave existing untouched", write a no-op that assigns the key to itself; MariaDB has no DO NOTHING.
        var assignments = update is { Count: > 0 }
            ? string.Join(", ", update.Select(column => $"{column} = VALUES({column})"))
            : $"{key[0]} = {key[0]}";

        return $"INSERT INTO {table} ({names}) VALUES ({values}) ON DUPLICATE KEY UPDATE {assignments}";
    }

    public override IReadOnlyList<string> AdditionalIndexes()
    {
        return
        [
            // MySQL has no partial indexes; use a functional index that folds NULL into the empty string instead (needs 8.0.13 or later).
            "CREATE UNIQUE INDEX UX_EnvironmentVariables_Scope " +
            "ON EnvironmentVariables (Name, (IFNULL(JobId, '')))"
        ];
    }

    protected override string TypeOf(ColumnDefinition column)
    {
        return column.Kind switch
        {
            ColumnKind.Guid => "char(36)",
            ColumnKind.Text => "longtext",
            ColumnKind.ShortText => $"varchar({column.Length})",
            ColumnKind.Timestamp => "datetime(6)",
            ColumnKind.Int => "int",
            ColumnKind.BigInt => "bigint",
            ColumnKind.Bool => "tinyint(1)",
            _ => "longtext"
        };
    }

    // MySQL TEXT columns cannot take a plain default; since 8.0.13 an expression wrapped in parentheses works.
    protected override string DefaultOf(ColumnDefinition column)
    {
        return column is { Kind: ColumnKind.Text, Default: not null }
            ? $"({column.Default})"
            : column.Default ?? string.Empty;
    }
}