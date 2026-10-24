using Walis.Core.Database;

namespace Walis.Tests;

/// <summary>
///     Test connections for external databases. Without the environment variables only SQLite runs,
///     so everyday development needs no three database servers; when CI sets the variables those kinds run too.
///     WALIS_TEST_POSTGRES="Host=127.0.0.1;Database=walis_test;Username=leo;Password=…"
///     WALIS_TEST_MYSQL="Server=127.0.0.1;Port=13306;Database=walis_test;Uid=root;Pwd=…"
///     WALIS_TEST_SQLSERVER="Server=127.0.0.1,11433;Database=walis_test;User Id=sa;Password=…;TrustServerCertificate=true"
/// </summary>
public static class TestDatabases
{
    public static string? ConnectionStringFor(DatabaseProvider provider)
    {
        return provider switch
        {
            DatabaseProvider.PostgreSql => Environment.GetEnvironmentVariable("WALIS_TEST_POSTGRES"),
            DatabaseProvider.MySql => Environment.GetEnvironmentVariable("WALIS_TEST_MYSQL"),
            DatabaseProvider.SqlServer => Environment.GetEnvironmentVariable("WALIS_TEST_SQLSERVER"),
            _ => null
        };
    }

    /// <summary>The databases reachable in this test run; SQLite is always present.</summary>
    public static IEnumerable<object[]> Available()
    {
        yield return [DatabaseProvider.Sqlite];

        foreach (var provider in new[]
                 {
                     DatabaseProvider.PostgreSql,
                     DatabaseProvider.MySql,
                     DatabaseProvider.SqlServer
                 })
            if (!string.IsNullOrWhiteSpace(ConnectionStringFor(provider)))
                yield return [provider];
    }
}