namespace Walis.Core.Database;

public sealed class SqliteDialect : SqlDialectBase
{
    public override DatabaseProvider Provider => DatabaseProvider.Sqlite;

    public override string SelectUtcNow => "SELECT strftime('%Y-%m-%dT%H:%M:%fZ', 'now');";

    public override string ListColumnsSql => "SELECT name FROM pragma_table_info(@Table)";

    public override string ListIndexesSql => "SELECT name FROM sqlite_master WHERE type = 'index'";

    protected override string CaseInsensitiveSuffix => " COLLATE NOCASE";

    public override string SecondsBetween(string later, string earlier)
    {
        return $"((julianday({later}) - julianday({earlier})) * 86400.0)";
    }

    public override string DateText(string column)
    {
        return $"date({column})";
    }

    protected override string TypeOf(ColumnDefinition column)
    {
        return column.Kind switch
        {
            ColumnKind.Int or ColumnKind.BigInt or ColumnKind.Bool => "INTEGER",
            _ => "TEXT"
        };
    }
}