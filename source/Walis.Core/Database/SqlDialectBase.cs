using System.Text;

namespace Walis.Core.Database;

/// <summary>
///     The assembly logic shared by all four dialects: CREATE TABLE skeleton, column definitions, indexes.
///     Only type names and a handful of syntax details differ, overridden by subclasses.
/// </summary>
public abstract class SqlDialectBase : ISqlDialect
{
    protected virtual string CaseInsensitiveSuffix => string.Empty;
    public abstract DatabaseProvider Provider { get; }

    public virtual string Limit(string count)
    {
        return $"LIMIT {count}";
    }

    public virtual string Page(string count, string offset)
    {
        return $"LIMIT {count} OFFSET {offset}";
    }

    public abstract string SecondsBetween(string later, string earlier);

    public abstract string DateText(string column);

    public virtual string Concat(string left, string right)
    {
        return $"{left} || {right}";
    }

    public virtual string Boolean(bool value)
    {
        return value ? "1" : "0";
    }

    public virtual string CountAll => "COUNT(1)";

    public virtual string Length(string expression)
    {
        return $"LENGTH({expression})";
    }

    public virtual string CastToText(string expression)
    {
        return $"CAST({expression} AS TEXT)";
    }

    public abstract string SelectUtcNow { get; }

    public virtual string SkipLockedSuffix => string.Empty;

    public virtual string SkipLockedHint => string.Empty;

    public virtual string ForUpdateSuffix => string.Empty;

    public virtual string ForUpdateHint => string.Empty;

    public abstract string ListColumnsSql { get; }

    public abstract string ListIndexesSql { get; }

    public virtual string CreateTable(TableDefinition table)
    {
        var builder = new StringBuilder();
        builder.Append("CREATE TABLE ").Append(table.Name).AppendLine(" (");

        var parts = new List<string>();

        foreach (var column in table.Columns) parts.Add("    " + ColumnClause(column));

        parts.Add($"    PRIMARY KEY ({string.Join(", ", table.PrimaryKey)})");

        foreach (var unique in table.Unique ?? []) parts.Add($"    UNIQUE ({string.Join(", ", unique)})");

        foreach (var foreignKey in table.ForeignKeys ?? [])
            parts.Add(
                $"    FOREIGN KEY ({foreignKey.Column}) REFERENCES {foreignKey.ReferencedTable}" +
                $"({foreignKey.ReferencedColumn}) ON DELETE CASCADE");

        if (!string.IsNullOrWhiteSpace(table.Check)) parts.Add($"    CHECK ({table.Check})");

        builder.AppendLine(string.Join(",\n", parts));
        builder.Append(')');
        return builder.ToString();
    }

    public virtual string AddColumn(string table, ColumnDefinition column)
    {
        return $"ALTER TABLE {table} ADD COLUMN {ColumnClause(column)}";
    }

    public virtual string CreateIndex(IndexDefinition index)
    {
        return $"CREATE {(index.Unique ? "UNIQUE " : string.Empty)}INDEX {index.Name} " +
               $"ON {index.Table} ({string.Join(", ", index.Columns)})";
    }

    public virtual IReadOnlyList<string> AdditionalIndexes()
    {
        return
        [
            "CREATE UNIQUE INDEX UX_EnvironmentVariables_Job ON EnvironmentVariables (Name, JobId) " +
            "WHERE JobId IS NOT NULL",
            "CREATE UNIQUE INDEX UX_EnvironmentVariables_Global ON EnvironmentVariables (Name) " +
            "WHERE JobId IS NULL"
        ];
    }

    public virtual string Upsert(
        string table,
        IReadOnlyList<UpsertColumn> columns,
        IReadOnlyList<string> key,
        IReadOnlyList<string>? update)
    {
        var names = string.Join(", ", columns.Select(column => column.Name));
        var values = string.Join(", ", columns.Select(column => column.Value));
        var conflict = string.Join(", ", key);

        var action = update is { Count: > 0 }
            ? "DO UPDATE SET " + string.Join(", ", update.Select(column => $"{column} = EXCLUDED.{column}"))
            : "DO NOTHING";

        return $"INSERT INTO {table} ({names}) VALUES ({values}) ON CONFLICT ({conflict}) {action}";
    }

    protected abstract string TypeOf(ColumnDefinition column);

    /// <summary>Boolean defaults must be TRUE/FALSE in PostgreSQL; the other databases accept 1/0.</summary>
    protected virtual string DefaultOf(ColumnDefinition column)
    {
        return column.Default ?? string.Empty;
    }

    protected string ColumnClause(ColumnDefinition column)
    {
        var builder = new StringBuilder();
        builder.Append(column.Name).Append(' ').Append(TypeOf(column));

        if (column.CaseInsensitive) builder.Append(CaseInsensitiveSuffix);

        builder.Append(column.Nullable ? " NULL" : " NOT NULL");

        var defaultValue = DefaultOf(column);
        if (!string.IsNullOrWhiteSpace(defaultValue)) builder.Append(" DEFAULT ").Append(defaultValue);

        return builder.ToString();
    }
}