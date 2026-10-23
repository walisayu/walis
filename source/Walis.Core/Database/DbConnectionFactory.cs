namespace Walis.Core.Database;

public static class DbConnectionFactory
{
    /// <summary>Picks a database from the settings; inferred from the connection string when unset, defaulting to single-node SQLite.</summary>
    public static IDbConnectionFactory Create(string? provider, string connectionString)
    {
        return Create(DatabaseProviders.Resolve(provider, connectionString), connectionString);
    }

    public static IDbConnectionFactory Create(DatabaseProvider provider, string connectionString)
    {
        return provider switch
        {
            DatabaseProvider.PostgreSql => new PostgreSqlConnectionFactory(connectionString),
            DatabaseProvider.MySql => new MySqlConnectionFactory(connectionString),
            DatabaseProvider.SqlServer => new SqlServerConnectionFactory(connectionString),
            _ => new SqliteConnectionFactory(connectionString)
        };
    }
}