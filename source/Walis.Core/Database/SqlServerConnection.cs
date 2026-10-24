using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;

namespace Walis.Core.Database;

/// <summary>
///     Wraps <see cref="SqlConnection" /> to change DateTime parameters to <c>datetime2</c> before sending them.
///     Why this layer is needed: Dapper gives DateTime parameters the type <see cref="DbType.DateTime" />
///     (its built-in type table answers first, so custom type handlers never run), and SqlClient then sends the value
///     as the legacy <c>datetime</c>: 1/300-second precision that **rounds up**. A "now" just written may read back
///     larger than the original, the lease and throttle "not expired yet" checks then see a future time, builds get blocked by their own
///     maintenance checks, and it works or fails depending on the millisecond of the moment.
///     Changing Dapper's global type mapping is not an option: it would make Npgsql send <c>timestamp without time zone</c>,
///     fighting our <c>timestamptz</c> columns. So the fix lives on this one connection type only.
/// </summary>
internal sealed class SqlServerConnection(SqlConnection inner) : DbConnection
{
    public SqlConnection Inner { get; } = inner;

    [AllowNull]
    public override string ConnectionString
    {
        get => Inner.ConnectionString;
        set => Inner.ConnectionString = value;
    }

    public override string Database => Inner.Database;

    public override string DataSource => Inner.DataSource;

    public override string ServerVersion => Inner.ServerVersion;

    public override ConnectionState State => Inner.State;

    public override void ChangeDatabase(string databaseName)
    {
        Inner.ChangeDatabase(databaseName);
    }

    public override void Close()
    {
        Inner.Close();
    }

    public override void Open()
    {
        Inner.Open();
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        return Inner.OpenAsync(cancellationToken);
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        return Inner.BeginTransaction(isolationLevel);
    }

    protected override DbCommand CreateDbCommand()
    {
        return new SqlServerCommand(Inner.CreateCommand());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Inner.Dispose();

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        return Inner.DisposeAsync();
    }
}

internal sealed class SqlServerCommand(SqlCommand inner) : DbCommand
{
    [AllowNull]
    public override string CommandText
    {
        get => inner.CommandText;
        set => inner.CommandText = value;
    }

    public override int CommandTimeout
    {
        get => inner.CommandTimeout;
        set => inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => inner.CommandType;
        set => inner.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => inner.DesignTimeVisible;
        set => inner.DesignTimeVisible = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => inner.UpdatedRowSource;
        set => inner.UpdatedRowSource = value;
    }

    protected override DbConnection? DbConnection
    {
        get => inner.Connection;
        set => inner.Connection = value is SqlServerConnection wrapper ? wrapper.Inner : value as SqlConnection;
    }

    protected override DbParameterCollection DbParameterCollection => inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => inner.Transaction;
        set => inner.Transaction = value as SqlTransaction;
    }

    public override void Cancel()
    {
        inner.Cancel();
    }

    public override void Prepare()
    {
        inner.Prepare();
    }

    protected override DbParameter CreateDbParameter()
    {
        return inner.CreateParameter();
    }

    public override int ExecuteNonQuery()
    {
        UseDateTime2();
        return inner.ExecuteNonQuery();
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        UseDateTime2();
        return inner.ExecuteNonQueryAsync(cancellationToken);
    }

    public override object? ExecuteScalar()
    {
        UseDateTime2();
        return inner.ExecuteScalar();
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        UseDateTime2();
        return inner.ExecuteScalarAsync(cancellationToken);
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        UseDateTime2();
        return inner.ExecuteReader(behavior);
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        UseDateTime2();
        return await inner.ExecuteReaderAsync(behavior, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();

        base.Dispose(disposing);
    }

    /// <summary>Changes DateTime parameters from legacy datetime to datetime2 to avoid the 1/300-second quantization and rounding.</summary>
    private void UseDateTime2()
    {
        foreach (SqlParameter parameter in inner.Parameters)
            if (parameter.SqlDbType == SqlDbType.DateTime)
                parameter.SqlDbType = SqlDbType.DateTime2;
    }
}