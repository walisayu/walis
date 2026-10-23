namespace Walis.Core.Database;

/// <summary>The databases Walis supports. SQLite is the single-node default; the other three are for multiple nodes sharing one dataset.</summary>
public enum DatabaseProvider
{
    Sqlite,
    PostgreSql,
    MySql,
    SqlServer
}

public static class DatabaseProviders
{
    /// <summary>
    ///     Decides the database from the configured name, guessing from the connection string when unset. A wrong guess fails at startup,
    ///     which beats discovering a dialect mismatch halfway through.
    /// </summary>
    public static DatabaseProvider Resolve(string? name, string connectionString)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim().ToLowerInvariant() switch
            {
                "sqlite" => DatabaseProvider.Sqlite,
                "postgres" or "postgresql" or "npgsql" => DatabaseProvider.PostgreSql,
                "mysql" or "mariadb" => DatabaseProvider.MySql,
                "sqlserver" or "mssql" or "azuresql" => DatabaseProvider.SqlServer,
                _ => throw new ArgumentException(
                    $"不認得的資料庫 {name}，可用 sqlite、postgresql、mysql、sqlserver。", nameof(name))
            };

        var text = connectionString ?? string.Empty;

        if (Contains(text, "Host=") || Contains(text, "postgres")) return DatabaseProvider.PostgreSql;

        if ((Contains(text, "Server=") && Contains(text, "Uid=")) ||
            Contains(text, "mysql") || Contains(text, "mariadb"))
            return DatabaseProvider.MySql;

        if (Contains(text, "Initial Catalog=") || Contains(text, "TrustServerCertificate") ||
            Contains(text, "Integrated Security"))
            return DatabaseProvider.SqlServer;

        return DatabaseProvider.Sqlite;
    }

    public static string DisplayName(DatabaseProvider provider)
    {
        return provider switch
        {
            DatabaseProvider.PostgreSql => "PostgreSQL",
            DatabaseProvider.MySql => "MySQL",
            DatabaseProvider.SqlServer => "SQL Server",
            _ => "SQLite"
        };
    }

    /// <summary>SQLite is a single file and breaks when several API nodes share it; only the other three suit HA.</summary>
    public static bool SupportsMultipleNodes(DatabaseProvider provider)
    {
        return provider != DatabaseProvider.Sqlite;
    }

    private static bool Contains(string text, string token)
    {
        return text.Contains(token, StringComparison.OrdinalIgnoreCase);
    }
}