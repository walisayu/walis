using System.Data;
using Npgsql;

namespace Walis.Core.Database;

public sealed class PostgreSqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    private readonly NpgsqlDataSource _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();

    public ISqlDialect Dialect { get; } = new PostgreSqlDialect();

    public IDbConnection Create()
    {
        return _dataSource.CreateConnection();
    }
}