namespace Walis.Core.Database;

/// <summary>One column written by an upsert: the value defaults to the parameter of the same name, a few columns are literals (e.g. Settings' Id = 1).</summary>
public readonly record struct UpsertColumn(string Name, string Value)
{
    public static UpsertColumn Parameter(string name)
    {
        return new UpsertColumn(name, "@" + name);
    }

    public static UpsertColumn Literal(string name, string value)
    {
        return new UpsertColumn(name, value);
    }
}

/// <summary>
///     Every difference between the four databases is collected here. Repositories only build SQL through these methods
///     and never write LIMIT, julianday, ON CONFLICT or other syntax that ties them to one database.
/// </summary>
public interface ISqlDialect
{
    DatabaseProvider Provider { get; }

    /// <summary>A count returning a 64-bit integer; SQL Server's COUNT only yields int, which does not match the statistics' long.</summary>
    string CountAll { get; }

    /// <summary>Gets the current UTC time from the database host, so clock drift between cluster nodes cannot break leases and throttling.</summary>
    string SelectUtcNow { get; }

    /// <summary>Locks the selected rows when claiming from the queue and skips rows others are handling, so several nodes never grab the same one.</summary>
    string SkipLockedSuffix { get; }

    /// <summary>SQL Server achieves the same thing with table hints; the other databases return an empty string.</summary>
    string SkipLockedHint { get; }

    /// <summary>Locks the selected rows until the transaction ends and makes others wait; used to queue up when taking a build number.</summary>
    string ForUpdateSuffix { get; }

    /// <summary>The SQL Server version, again a table hint.</summary>
    string ForUpdateHint { get; }

    /// <summary>Lists the columns of a table (parameter @Table); no rows means the table does not exist yet.</summary>
    string ListColumnsSql { get; }

    /// <summary>Lists the current index names.</summary>
    string ListIndexesSql { get; }

    /// <summary>Appended after ORDER BY to cap the row count; <paramref name="count" /> may be a constant or a parameter name.</summary>
    string Limit(string count);

    /// <summary>Appended after ORDER BY to page: skip <paramref name="offset" /> rows then take <paramref name="count" />; both may be constants or parameter names.</summary>
    string Page(string count, string offset);

    /// <summary>The difference between two time columns in seconds (floating point).</summary>
    string SecondsBetween(string later, string earlier);

    /// <summary>Formats a time column as a yyyy-MM-dd string for grouping in trend charts.</summary>
    string DateText(string column);

    string Concat(string left, string right);

    /// <summary>A boolean literal in SQL; PostgreSQL does not accept 1/0.</summary>
    string Boolean(bool value);

    /// <summary>String length; SQL Server's function is called LEN.</summary>
    string Length(string expression);

    /// <summary>Casts a value to text for LIKE; the type names differ in all four databases.</summary>
    string CastToText(string expression);

    /// <summary>Inserts or updates by key; an empty <paramref name="update" /> means leave existing rows untouched.</summary>
    string Upsert(
        string table,
        IReadOnlyList<UpsertColumn> columns,
        IReadOnlyList<string> key,
        IReadOnlyList<string>? update);

    string CreateTable(TableDefinition table);

    string AddColumn(string table, ColumnDefinition column);

    string CreateIndex(IndexDefinition index);

    /// <summary>Dialect-specific indexes that cannot be expressed with <see cref="IndexDefinition" />.</summary>
    IReadOnlyList<string> AdditionalIndexes();
}