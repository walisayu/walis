namespace Walis.Core.Database;

public sealed class PostgreSqlDialect : SqlDialectBase
{
    public override DatabaseProvider Provider => DatabaseProvider.PostgreSql;

    public override string SelectUtcNow => "SELECT CURRENT_TIMESTAMP;";

    // Lock this row when claiming a build and skip rows other nodes are handling, so no build is grabbed by two nodes.
    public override string SkipLockedSuffix => " FOR UPDATE SKIP LOCKED";

    public override string ForUpdateSuffix => " FOR UPDATE";

    public override string ListColumnsSql =>
        """
        SELECT column_name FROM information_schema.columns
        WHERE table_schema = current_schema() AND table_name = LOWER(@Table)
        """;

    public override string ListIndexesSql =>
        "SELECT indexname FROM pg_indexes WHERE schemaname = current_schema()";

    public override string SecondsBetween(string later, string earlier)
    {
        return $"CAST(EXTRACT(EPOCH FROM ({later} - {earlier})) AS double precision)";
    }

    public override string DateText(string column)
    {
        return $"to_char({column}, 'YYYY-MM-DD')";
    }

    public override string Boolean(bool value)
    {
        return value ? "TRUE" : "FALSE";
    }

    public override string CastToText(string expression)
    {
        return $"CAST({expression} AS text)";
    }

    public override IReadOnlyList<string> AdditionalIndexes()
    {
        return
        [
            .. base.AdditionalIndexes(),
            // PostgreSQL text comparison is case-sensitive, so accounts need a separate lowercase index to keep Leo and leo from coexisting.
            "CREATE UNIQUE INDEX UX_Users_UsernameLower ON Users (LOWER(Username))"
        ];
    }

    protected override string TypeOf(ColumnDefinition column)
    {
        return column.Kind switch
        {
            ColumnKind.Guid => "uuid",
            ColumnKind.Text => "text",
            ColumnKind.ShortText => $"varchar({column.Length})",
            ColumnKind.Timestamp => "timestamptz",
            ColumnKind.Int => "integer",
            ColumnKind.BigInt => "bigint",
            ColumnKind.Bool => "boolean",
            _ => "text"
        };
    }

    protected override string DefaultOf(ColumnDefinition column)
    {
        return column.Kind == ColumnKind.Bool
            ? column.Default switch { "1" => "TRUE", "0" => "FALSE", _ => column.Default ?? string.Empty }
            : column.Default ?? string.Empty;
    }
}