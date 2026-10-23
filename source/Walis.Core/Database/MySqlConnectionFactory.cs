using System.Data;
using MySqlConnector;

namespace Walis.Core.Database;

public sealed class MySqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    private readonly string _connectionString = connectionString;

    public ISqlDialect Dialect { get; } = new MySqlDialect();

    public IDbConnection Create()
    {
        return new MySqlConnection(_connectionString);
    }
}