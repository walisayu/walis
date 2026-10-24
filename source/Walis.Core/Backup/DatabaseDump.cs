using Walis.Core.Models;
using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Walis.Core.Database;

namespace Walis.Core.Backup;

/// <summary>A lease that must remain owned throughout a destructive database import.</summary>
public sealed record ImportLeaseGuard(string Name, string Owner, TimeSpan Extension);

/// <summary>
///     Exports the Walis database as database-agnostic JSON Lines that can also be imported back.
///     Why not just copy the database file: that works for SQLite but not PostgreSQL, MySQL or SQL Server,
///     which would need pg_dump/mysqldump/server-side BACKUP: three tools, three formats, and a backup that
///     can only be restored to the same kind of database. A logical export lets all four databases share one file,
///     and allows restoring from SQLite to PostgreSQL, which is what migrations and HA rollouts rely on.
///     Format: the first line is a header, every following line is one row <c>{"t":"table","r":{column:value}}</c>.
///     Processed line by line, so no amount of data is ever read into memory at once.
/// </summary>
public sealed class DatabaseDump(IDbConnectionFactory connectionFactory)
{
    /// <summary>Prefix of the summary of every audit row merged in from an archive (it never came from this server).</summary>
    public const string RestoredAuditMarker = "［還原自備份］";

    public const int FormatVersion = 1;

    /// <summary>
    ///     A dump record is deliberately bounded independently of the archive size. Without this,
    ///     <c>StreamReader.ReadLineAsync</c> can allocate until the process is killed before JSON is
    ///     ever parsed. Existing Walis text fields are far smaller than this defensive ceiling.
    /// </summary>
    public const int MaximumLineBytes = 16 * 1024 * 1024;

    /// <summary>The file name inside the backup archive; the restore side looks for this name.</summary>
    public const string FileName = "walis-data.jsonl";

    /// <summary>The path inside the backup archive.</summary>
    public const string EntryName = "walis/" + FileName;

    /// <summary>The table a restore merges into instead of replacing.</summary>
    private const string AuditTable = "AuditLogs";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    // These rows are live authentication/coordination state, not durable application data. Restoring
    // them would revive old sessions and OTP challenges, erase abuse counters, or overwrite leases
    // currently protecting the restore itself. Older archives can contain them, so import ignores
    // them as well as excluding them from newly-created archives.
    private static readonly HashSet<string> OperationalTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sessions",
        "OtpChallenges",
        "SignInRateLimits",
        "WebhookDeliveries",
        "WebhookRateLimits",
        "Leases"
    };

    private static readonly IReadOnlyList<TableDefinition> DurableTables =
        DatabaseSchema.Tables.Where(table => !OperationalTables.Contains(table.Name)).ToList();

    public async Task<DumpResult> ExportAsync(Stream output, CancellationToken cancellationToken = default)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        using var connection = connectionFactory.Create();

        if (connection.State != ConnectionState.Open) connection.Open();

        // A backup must describe one database point in time. Without a transaction, a build could
        // be inserted between scanning Jobs and Builds and produce a logically broken archive.
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        var header = new DumpHeader(
            FormatVersion,
            connectionFactory.Provider.ToString(),
            DateTime.UtcNow,
            DurableTables.Select(table => table.Name).ToList());

        await WriteLineAsync(writer, JsonSerializer.Serialize(header, SerializerOptions), cancellationToken);

        long rows = 0;

        foreach (var table in DurableTables)
        {
            var columns = table.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
            var dbConnection = connection as DbConnection
                               ?? throw new InvalidOperationException("資料庫連線不支援串流匯出。");
            var dbTransaction = transaction as DbTransaction
                                ?? throw new InvalidOperationException("資料庫交易不支援串流匯出。");

            // Dapper's ordinary QueryAsync buffers the complete table. A large Builds/Logs table
            // could therefore exhaust memory even though the dump format itself is streaming.
            await foreach (var record in dbConnection
                               .QueryUnbufferedAsync(
                                   $"SELECT * FROM {table.Name}",
                                   transaction: dbTransaction)
                               .WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var values = new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var pair in (IDictionary<string, object?>)record)
                {
                    // PostgreSQL folds column names to lowercase, so the names from the schema are always authoritative.
                    if (!columns.TryGetValue(pair.Key, out var column)) continue;

                    values[column.Name] = ToJson(column.Kind, pair.Value);
                }

                await WriteLineAsync(
                    writer,
                    JsonSerializer.Serialize(
                        new Dictionary<string, object?> { ["t"] = table.Name, ["r"] = values },
                        SerializerOptions),
                    cancellationToken);
                rows++;
            }
        }

        await writer.FlushAsync(cancellationToken);
        transaction.Commit();
        return new DumpResult(DurableTables.Count, rows);
    }

    /// <summary>
    ///     Writes the export back into the database: clear first (child tables first), then insert in the original order.
    ///     Everything runs in one transaction, so a failure midway leaves no half-written data.
    /// </summary>
    public async Task<DumpResult> ImportAsync(Stream input, CancellationToken cancellationToken = default)
    {
        return await ImportAsync(input, [], cancellationToken);
    }

    /// <summary>
    ///     Imports while fencing one or more already-acquired database leases. Ownership and expiry are
    ///     checked at both boundaries, and the final extension is part of the replacement transaction.
    /// </summary>
    public async Task<DumpResult> ImportAsync(
        Stream input,
        IReadOnlyList<ImportLeaseGuard> leaseGuards,
        CancellationToken cancellationToken = default)
    {
        using var reader = new BoundedUtf8LineReader(input, MaximumLineBytes);

        if (leaseGuards.Any(guard =>
                string.IsNullOrWhiteSpace(guard.Name) ||
                string.IsNullOrWhiteSpace(guard.Owner) ||
                guard.Extension <= TimeSpan.Zero) ||
            leaseGuards.Select(guard => guard.Name).Distinct(StringComparer.Ordinal).Count() != leaseGuards.Count)
            throw new ArgumentException("匯入租約必須有不重複的名稱、owner 與正數有效期。", nameof(leaseGuards));

        var headerLine = await reader.ReadLineAsync(cancellationToken)
                         ?? throw new InvalidDataException("備份檔是空的。");

        DumpHeader header;

        try
        {
            header = JsonSerializer.Deserialize<DumpHeader>(headerLine, SerializerOptions)
                     ?? throw new InvalidDataException("備份檔的標頭讀不出來。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("備份檔的標頭不是有效的 JSON。", exception);
        }

        if (header.Version != FormatVersion)
            throw new InvalidDataException(
                $"備份檔的格式版本是 {header.Version}，這個版本的 Walis 只認得 {FormatVersion}。");

        var tables = DurableTables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);

        using var connection = connectionFactory.Create();

        if (connection.State != ConnectionState.Open) connection.Open();

        using var transaction = connection.BeginTransaction();
        var databaseNow = await connection.ReadUtcNowAsync(connectionFactory.Dialect, transaction, cancellationToken);

        foreach (var guard in leaseGuards.OrderBy(guard => guard.Name, StringComparer.Ordinal))
        {
            var lease = await connection.QuerySingleOrDefaultAsync<LeaseRow>(new CommandDefinition(
                "SELECT Owner, ExpiresAt FROM Leases WHERE Name = @Name;",
                new { guard.Name },
                transaction,
                cancellationToken: cancellationToken));

            if (lease is null ||
                !string.Equals(lease.Owner, guard.Owner, StringComparison.Ordinal) ||
                lease.ExpiresAt <= databaseNow)
                throw new InvalidOperationException($"資料庫租約 {guard.Name} 已遺失，拒絕匯入。");
        }

        // Revoke the target system's current authentication challenges and distributed abuse
        // windows. They are deliberately never populated from an archive.
        foreach (var table in DatabaseSchema.Tables
                     .Where(table => OperationalTables.Contains(table.Name) && table.Name != "Leases")
                     .Reverse())
            await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table.Name}", transaction: transaction, cancellationToken: cancellationToken));

        // Delete child tables before parent tables: the schema is declared in dependency order, so walk it backwards.
        // The audit log is the one table that is never cleared: a restore must not be a way to erase what happened after
        // the backup was taken, so the archive's rows are merged in and the rows already present are kept.
        foreach (var table in DurableTables.Reverse())
        {
            if (string.Equals(table.Name, AuditTable, StringComparison.OrdinalIgnoreCase)) continue;

            await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table.Name}", transaction: transaction, cancellationToken: cancellationToken));
        }

        var existingAuditIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
                $"SELECT Id FROM {AuditTable}", transaction: transaction, cancellationToken: cancellationToken)))
            .ToHashSet();
        long auditRowsKept = existingAuditIds.Count;
        long auditRowsAdded = 0;

        var restoreStartedAt = DateTime.UtcNow;
        long rows = 0;
        long lineNumber = 1;
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line)) continue;

            TableDefinition table;
            DynamicParameters parameters;
            List<string> names;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var tableName = root.GetProperty("t").GetString() ?? string.Empty;

                // An unknown table means the backup comes from a newer version; skipping it beats failing the whole restore.
                if (!tables.TryGetValue(tableName, out table!)) continue;

                parameters = new DynamicParameters();
                names = [];
                var seenColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var property in root.GetProperty("r").EnumerateObject())
                {
                    var column = table.Columns.FirstOrDefault(candidate => string.Equals(
                        candidate.Name,
                        property.Name,
                        StringComparison.OrdinalIgnoreCase));

                    if (column is null) continue;

                    if (!seenColumns.Add(column.Name))
                        throw new InvalidDataException(
                            $"備份第 {lineNumber} 列重複指定欄位 {column.Name}。");

                    names.Add(column.Name);
                    parameters.Add(column.Name, FromJson(column.Kind, property.Value));
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                                                  JsonException or
                                                  KeyNotFoundException or
                                                  InvalidOperationException or
                                                  FormatException or
                                                  OverflowException)
            {
                throw new InvalidDataException($"備份第 {lineNumber} 列不是有效的資料列。", exception);
            }

            if (names.Count == 0) continue;

            if (string.Equals(table.Name, AuditTable, StringComparison.OrdinalIgnoreCase))
            {
                // Rows the database already holds (same Id) are kept as they are; only the archive's extra rows come in.
                if (names.Contains("Id") && parameters.Get<object?>("Id") is Guid auditId &&
                    !existingAuditIds.Add(auditId)) continue;

                // Rows that arrive with a restore must stay distinguishable from rows this server wrote: whoever can restore an
                // archive they built could otherwise plant entries in someone else's name. They are marked, and cannot be dated
                // after the restore itself (which would also sort them above everything genuine).
                if (names.Contains("Summary"))
                    parameters.Add("Summary", RestoredAuditMarker + (parameters.Get<object?>("Summary") as string ?? string.Empty));

                if (names.Contains("OccurredAt") && parameters.Get<object?>("OccurredAt") is DateTime occurredAt &&
                    occurredAt > restoreStartedAt)
                    parameters.Add("OccurredAt", restoreStartedAt);

                auditRowsAdded++;
            }

            // Users.Role defaults to the least privilege now, but an archive row without a valid role must not silently pick
            // whatever the column default is: refuse the archive instead.
            if (string.Equals(table.Name, "Users", StringComparison.OrdinalIgnoreCase) &&
                (!names.Contains("Role") || !UserRole.IsValid(parameters.Get<object?>("Role") as string)))
                throw new InvalidDataException($"備份第 {lineNumber} 列的帳號缺少有效的角色。");

            var sql = $"INSERT INTO {table.Name} ({string.Join(", ", names)}) " +
                      $"VALUES ({string.Join(", ", names.Select(name => "@" + name))})";

            await connection.ExecuteAsync(new CommandDefinition(
                sql, parameters, transaction, cancellationToken: cancellationToken));

            touched.Add(table.Name);
            rows++;
        }

        // Atomically fence the commit against the current database time. We intentionally do not
        // lock lease rows for the whole import: doing so blocks the heartbeat's separate connection
        // and makes every restore longer than a command timeout cancel itself. A stale importer may
        // do work in its uncommitted transaction, but it can never commit after ownership/expiry is
        // lost. Updating all guards in one transaction means a failure rolls every update back.
        databaseNow = await connection.ReadUtcNowAsync(connectionFactory.Dialect, transaction, cancellationToken);
        foreach (var guard in leaseGuards.OrderBy(guard => guard.Name, StringComparer.Ordinal))
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE Leases SET AcquiredAt = @Now, ExpiresAt = @Expires " +
                "WHERE Name = @Name AND Owner = @Owner AND ExpiresAt > @Now;",
                new
                {
                    guard.Name,
                    guard.Owner,
                    Now = databaseNow,
                    Expires = databaseNow.Add(guard.Extension)
                },
                transaction,
                cancellationToken: cancellationToken));

            if (affected != 1) throw new InvalidOperationException($"資料庫租約 {guard.Name} 已遺失，拒絕提交匯入。");
        }

        transaction.Commit();
        return new DumpResult(touched.Count, rows, auditRowsKept, auditRowsAdded);
    }

    private static async Task WriteLineAsync(
        StreamWriter writer,
        string line,
        CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaximumLineBytes)
            throw new InvalidDataException(
                $"資料庫列超過 {MaximumLineBytes} bytes 的備份格式上限。");

        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
    }

    /// <summary>
    ///     Converts to a JSON-friendly value based on the column type. The run-time type cannot be trusted: the same column
    ///     is a string in SQLite and a Guid in PostgreSQL, so only the schema decides.
    /// </summary>
    private static object? ToJson(ColumnKind kind, object? value)
    {
        if (value is null or DBNull) return null;

        return kind switch
        {
            ColumnKind.Guid => ToGuid(value).ToString("D"),
            ColumnKind.Timestamp => ToUtc(value).ToString("o", CultureInfo.InvariantCulture),
            ColumnKind.Bool => ToBool(value),
            ColumnKind.Int or ColumnKind.BigInt => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static object? FromJson(ColumnKind kind, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;

        return kind switch
        {
            ColumnKind.Guid => Guid.Parse(value.GetString()!),
            ColumnKind.Timestamp => DateTime.Parse(
                value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
            ColumnKind.Bool => value.ValueKind == JsonValueKind.True ||
                               (value.ValueKind == JsonValueKind.Number && value.GetInt64() != 0),
            ColumnKind.Int => value.GetInt32(),
            ColumnKind.BigInt => value.GetInt64(),
            _ => value.GetString()
        };
    }

    private static Guid ToGuid(object value)
    {
        return value switch
        {
            Guid guid => guid,
            string text => Guid.Parse(text),
            byte[] bytes => new Guid(bytes),
            _ => throw new InvalidDataException($"無法把 {value.GetType()} 當成識別碼。")
        };
    }

    private static DateTime ToUtc(object value)
    {
        var time = value switch
        {
            DateTime dateTime => dateTime,
            DateTimeOffset offset => offset.UtcDateTime,
            string text => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            _ => throw new InvalidDataException($"無法把 {value.GetType()} 當成時間。")
        };

        return time.Kind switch
        {
            DateTimeKind.Local => time.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
            _ => time
        };
    }

    private static bool ToBool(object value)
    {
        return value switch
        {
            bool flag => flag,
            string text => text is "1" or "true" or "True",
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0
        };
    }

    public sealed record DumpHeader(int Version, string Provider, DateTime ExportedAt, IReadOnlyList<string> Tables);

    /// <summary>
    ///     <paramref name="AuditRowsKept" /> is the number of audit rows that were already in the database and survived the import;
    ///     <paramref name="AuditRowsAdded" /> the archive's audit rows that were not present yet. Both are 0 for an export.
    /// </summary>
    public sealed record DumpResult(int Tables, long Rows, long AuditRowsKept = 0, long AuditRowsAdded = 0);

    private sealed record LeaseRow(string Owner, DateTime ExpiresAt);

    /// <summary>
    ///     Reads UTF-8 lines without the unbounded allocation performed by StreamReader.ReadLineAsync.
    ///     The limit is measured in source bytes, so multi-byte input cannot bypass it. Invalid UTF-8
    ///     is rejected instead of being silently replaced before JSON parsing.
    /// </summary>
    private sealed class BoundedUtf8LineReader : IDisposable
    {
        private const int ReadBufferSize = 64 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private readonly Stream _input;
        private readonly int _maximumBytes;
        private readonly byte[] _readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        private int _available;
        private bool _disposed;
        private bool _firstLine = true;
        private int _offset;

        public BoundedUtf8LineReader(Stream input, int maximumBytes)
        {
            ArgumentNullException.ThrowIfNull(input);

            if (!input.CanRead) throw new ArgumentException("輸入串流不可讀取。", nameof(input));

            if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));

            _input = input;
            _maximumBytes = maximumBytes;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            ArrayPool<byte>.Shared.Return(_readBuffer, true);
        }

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var line = new MemoryStream(Math.Min(_maximumBytes, 4096));

            while (true)
            {
                if (_offset == _available)
                {
                    _available = await _input.ReadAsync(_readBuffer, cancellationToken);
                    _offset = 0;

                    if (_available == 0) return line.Length == 0 ? null : Decode(line);
                }

                var unread = _readBuffer.AsSpan(_offset, _available - _offset);
                var newline = unread.IndexOf((byte)'\n');
                var count = newline >= 0 ? newline : unread.Length;

                if (count > _maximumBytes - line.Length)
                    throw new InvalidDataException(
                        $"備份包含超過 {_maximumBytes} bytes 的單列 JSON。");

                line.Write(unread[..count]);
                _offset += count;

                if (newline >= 0)
                {
                    _offset++;
                    return Decode(line);
                }
            }
        }

        private string Decode(MemoryStream line)
        {
            var length = checked((int)line.Length);
            var bytes = line.GetBuffer();

            if (length > 0 && bytes[length - 1] == (byte)'\r') length--;

            try
            {
                var result = StrictUtf8.GetString(bytes, 0, length);

                if (_firstLine)
                {
                    _firstLine = false;
                    return result.Length > 0 && result[0] == '\uFEFF' ? result[1..] : result;
                }

                return result;
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("備份包含無效的 UTF-8 資料。", exception);
            }
        }
    }
}