using System.Data;
using Microsoft.Data.Sqlite;

namespace Walis.Core.Database;

public sealed class SqliteConnectionFactory(string connectionString) : IDbConnectionFactory
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder(connectionString)
    {
        ForeignKeys = true
    }.ToString();

    public ISqlDialect Dialect { get; } = new SqliteDialect();

    public IDbConnection Create()
    {
        return new SqliteConnection(_connectionString);
    }
}