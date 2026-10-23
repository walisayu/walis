using System.Data;

namespace Walis.Core.Database;

public interface IDbConnectionFactory
{
    /// <summary>The SQL dialect for this connection; repositories use it to build syntax every database accepts.</summary>
    ISqlDialect Dialect { get; }

    DatabaseProvider Provider => Dialect.Provider;
    IDbConnection Create();
}