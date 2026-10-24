using Dapper;
using Microsoft.Data.Sqlite;
using Walis.Core.Database;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Tests;

/// <summary>
///     The database for tests. Defaults to a separate temporary SQLite file; when another database is chosen it connects
///     to the connection string from <see cref="TestDatabases" /> and clears the tables afterwards.
///     The schema is always created by DatabaseInitializer, so the production flow is what gets tested.
/// </summary>
public sealed class TemporaryDatabase : IAsyncDisposable
{
    private readonly string _path;

    /// <summary>Every test database encrypts stored secrets with this key, so the repositories run the production path.</summary>
    public const string TestEncryptionKey = "walis-tests-secret-encryption-key-0123456789";

    private TemporaryDatabase(string path, IDbConnectionFactory factory)
    {
        _path = path;
        Factory = factory;
        Protector = new SecretProtector(TestEncryptionKey);
        Leases = new LeaseRepository(factory);
        Jobs = new JobRepository(factory, Protector);
        Runners = new RunnerRepository(factory);
        Variables = new EnvironmentVariableRepository(factory, Protector);
        Notifications = new NotificationRepository(factory, new SecretProtector(TestEncryptionKey));
        Settings = new SettingsRepository(factory, Protector);
        Localization = new LocalizationRepository(factory);
        Security = new SecurityRepository(factory, Protector);
        Audit = new AuditRepository(factory);
        Ai = new AiRepository(factory, Protector);
    }

    public IDbConnectionFactory Factory { get; }

    public ISecretProtector Protector { get; }

    public IJobRepository Jobs { get; }

    public IRunnerRepository Runners { get; }

    public IEnvironmentVariableRepository Variables { get; }

    public INotificationRepository Notifications { get; }

    public ISettingsRepository Settings { get; }

    public ILocalizationRepository Localization { get; }

    public ISecurityRepository Security { get; }

    public IAuditRepository Audit { get; }

    public IAiRepository Ai { get; }

    public ILeaseRepository Leases { get; }

    public DatabaseProvider Provider => Factory.Provider;

    public async ValueTask DisposeAsync()
    {
        if (Factory.Provider != DatabaseProvider.Sqlite)
        {
            await DropAllAsync(Factory);
            return;
        }

        // Only this database's pool: ClearAllPools would close connections that tests running in parallel still use.
        using (var connection = (SqliteConnection)Factory.Create())
            SqliteConnection.ClearPool(connection);

        if (File.Exists(_path)) File.Delete(_path);
    }

    public static async Task<TemporaryDatabase> CreateAsync(DatabaseProvider provider = DatabaseProvider.Sqlite)
    {
        DapperConfiguration.Register();

        if (provider == DatabaseProvider.Sqlite)
        {
            var path = Path.Combine(Path.GetTempPath(), $"walis-tests-{Guid.NewGuid():N}.db");
            var sqlite = new SqliteConnectionFactory($"Data Source={path}");
            await new DatabaseInitializer(sqlite).InitializeAsync();

            return new TemporaryDatabase(path, sqlite);
        }

        var connectionString = TestDatabases.ConnectionStringFor(provider)
                               ?? throw new InvalidOperationException($"沒有設定 {provider} 的測試連線字串。");

        var factory = DbConnectionFactory.Create(provider, connectionString);

        // The external databases are shared, so each test cleans up and recreates first.
        await DropAllAsync(factory);
        await new DatabaseInitializer(factory).InitializeAsync();

        return new TemporaryDatabase(string.Empty, factory);
    }

    /// <summary>
    ///     Drops every Walis table.
    ///     Exceptions must not be swallowed here: a failed drop leaves the old schema, later initialization only adds columns and never changes types,
    ///     and the tests then run on the previous schema, with flaky symptoms that look like product bugs.
    ///     SQL Server has no CASCADE, so all foreign keys are removed before dropping tables.
    /// </summary>
    private static async Task DropAllAsync(IDbConnectionFactory factory)
    {
        using var connection = factory.Create();

        if (factory.Provider == DatabaseProvider.SqlServer)
            await connection.ExecuteAsync("""
                                          DECLARE @sql nvarchar(max) = N'';
                                          SELECT @sql += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id))
                                              + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id))
                                              + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'
                                          FROM sys.foreign_keys;
                                          IF LEN(@sql) > 0 EXEC sp_executesql @sql;
                                          """);

        if (factory.Provider == DatabaseProvider.MySql) await connection.ExecuteAsync("SET FOREIGN_KEY_CHECKS = 0;");

        var cascade = factory.Provider == DatabaseProvider.PostgreSql ? " CASCADE" : string.Empty;

        foreach (var table in DatabaseSchema.Tables.Reverse())
            await connection.ExecuteAsync($"DROP TABLE IF EXISTS {table.Name}{cascade}");

        if (factory.Provider == DatabaseProvider.MySql) await connection.ExecuteAsync("SET FOREIGN_KEY_CHECKS = 1;");
    }
}