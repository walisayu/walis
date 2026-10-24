using System.Data;
using Microsoft.Data.SqlClient;

namespace Walis.Core.Database;

public sealed class SqlServerConnectionFactory(string connectionString) : IDbConnectionFactory
{
    private readonly string _connectionString = connectionString;

    public ISqlDialect Dialect { get; } = new SqlServerDialect();

    public IDbConnection Create()
    {
        var connection = new SqlConnection(_connectionString);

        // The SQL Server isolation level is session state and is not reset when a connection returns to the pool.
        // After a Serializable transaction ends, the next query that gets this connection **keeps** running under SERIALIZABLE:
        // the READPAST hint used to claim builds errors outright, and other queries silently escalate their lock ranges.
        // Explicitly set READ COMMITTED on every open; transactions that need something stricter specify it themselves.
        connection.StateChange += (_, args) =>
        {
            if (args.CurrentState != ConnectionState.Open) return;

            using var command = connection.CreateCommand();
            command.CommandText = "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;";
            command.ExecuteNonQuery();
        };

        return new SqlServerConnection(connection);
    }
}