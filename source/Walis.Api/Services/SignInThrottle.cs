using System.Collections.Concurrent;
using System.Data;
using Dapper;
using Walis.Core.Database;

namespace Walis.Api.Services;

/// <summary>
///     Brute-force protection for sign-in and one-time passwords. A six-digit TOTP has only a million combinations
///     and can be exhausted in hours without a limit; the password side is slowed by PBKDF2 but should not be unlimited either.
///     Counted per account and per source IP: the account counter blocks guessing against one person, the IP counter blocks one machine sweeping every account.
///     The production service keeps the state in the shared database so nodes and restarts do not reset the counts; the constructor taking only a
///     TimeProvider keeps an in-memory implementation for pure-logic tests that need no database.
/// </summary>
public sealed class SignInThrottle
{
    /// <summary>How many consecutive failures lock an account.</summary>
    public const int AccountLimit = 5;

    /// <summary>
    ///     How many failures within the window lock a source. Much looser than the account limit: an office sharing one egress IP is normal,
    ///     and behind a reverse proxy every request may even show the same address; too strict and others lock the door for you.
    /// </summary>
    public const int AddressLimit = 100;

    private const int MaximumPersistentEntries = 50_000;

    /// <summary>Extra rows dropped per eviction so a flood does not run the eviction query on every single request.</summary>
    private const int EvictionBatch = 500;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _clock;
    private readonly IDbConnectionFactory? _connectionFactory;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public SignInThrottle(IDbConnectionFactory connectionFactory)
        : this(TimeProvider.System, connectionFactory)
    {
    }

    public SignInThrottle() : this(TimeProvider.System)
    {
    }

    public SignInThrottle(TimeProvider clock)
    {
        _clock = clock;
    }

    public SignInThrottle(TimeProvider clock, IDbConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _clock = clock;
        _connectionFactory = connectionFactory;
    }

    private ISqlDialect Dialect => (_connectionFactory
                                    ?? throw new InvalidOperationException(
                                        "Persistent security state requires a database connection factory.")).Dialect;

    /// <summary>
    ///     Where lockouts are reported (audit, server log, notification). Optional so the pure-logic tests and the stdio tooling can
    ///     use the throttle on its own; the API registers <see cref="SecurityEventService" />.
    /// </summary>
    public ISignInLockoutSink? LockoutSink { get; set; }

    public async Task<TimeSpan?> TryBeginAttemptAsync(
        string account,
        string? address,
        CancellationToken cancellationToken = default)
    {
        return (await BeginAttemptAsync(account, address, cancellationToken)).RetryAfter;
    }

    /// <summary>
    ///     Reserves one attempt and says whether this very reservation reached the limit. The limit is reached while the password is
    ///     still unverified; whether the lock sticks is only known once the caller reports the outcome, so the lockout event is raised by
    ///     <see cref="ConfirmFailureAsync" /> and not here. A correct password hands the reservation back and no lockout is reported.
    /// </summary>
    public Task<SignInAttempt> BeginAttemptAsync(
        string account,
        string? address,
        CancellationToken cancellationToken = default)
    {
        return _connectionFactory is null
            ? Task.FromResult(BeginAttempt(account, address))
            : TryBeginPersistentAsync(account, address, cancellationToken);
    }

    /// <summary>
    ///     The verification behind <paramref name="attempt" /> failed. The reservation stays charged (that is the failure count), and if it
    ///     was the one that reached the limit the lockout is reported to <see cref="LockoutSink" />.
    /// </summary>
    public async Task ConfirmFailureAsync(
        SignInAttempt attempt,
        string account,
        string? address,
        CancellationToken cancellationToken = default)
    {
        if (LockoutSink is null || !(attempt.AccountLocked || attempt.AddressLocked)) return;

        await LockoutSink.LockedOutAsync(
            new SignInLockout(account, address, attempt.AddressLocked && !attempt.AccountLocked, Lockout),
            cancellationToken);
    }

    public Task RecordSuccessAsync(
        string account,
        string? address,
        CancellationToken cancellationToken = default)
    {
        if (_connectionFactory is null)
        {
            RecordSuccess(account, address);
            return Task.CompletedTask;
        }

        return MutatePersistentReservationAsync(account, address, true, cancellationToken);
    }

    public Task CancelAttemptAsync(
        string account,
        string? address,
        CancellationToken cancellationToken = default)
    {
        if (_connectionFactory is null)
        {
            CancelAttempt(account, address);
            return Task.CompletedTask;
        }

        return MutatePersistentReservationAsync(account, address, false, cancellationToken);
    }

    /// <summary>Lists all currently active account and source-address lockouts for the Security Center.</summary>
    public async Task<IReadOnlyList<ActiveSignInLockout>> ListActiveLockoutsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_connectionFactory is null)
        {
            lock (_gate)
            {
                Prune();
                var now = _clock.GetUtcNow().UtcDateTime;
                return _entries
                    .Where(pair => pair.Value.LockedUntil.UtcDateTime > now)
                    .OrderByDescending(pair => pair.Value.LockedUntil)
                    .Select(pair => ToActiveLockout(
                        pair.Key,
                        pair.Value.Failures,
                        pair.Value.FirstFailureAt.UtcDateTime,
                        pair.Value.LockedUntil.UtcDateTime,
                        now))
                    .ToArray();
            }
        }

        using var connection = _connectionFactory.Create();
        Open(connection);
        var dbNow = await connection.ReadUtcNowAsync(Dialect, cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<PersistentEntry>(new CommandDefinition(
            $"""
             SELECT BucketKey, Failures, FirstFailureAt, LockedUntil
             FROM SignInRateLimits
             WHERE LockedUntil > @Now
             ORDER BY LockedUntil DESC, BucketKey ASC
             {Dialect.Limit("200")};
             """,
            new { Now = dbNow },
            cancellationToken: cancellationToken));

        return rows
            .Select(row => ToActiveLockout(row.BucketKey, row.Failures, row.FirstFailureAt, row.LockedUntil, dbNow))
            .ToArray();
    }

    /// <summary>Manually clears a lockout bucket so an administrator can unlock a user or IP immediately.</summary>
    public async Task<bool> UnlockAsync(string bucketKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucketKey)) return false;

        var trimmed = bucketKey.Trim();

        if (_connectionFactory is null)
        {
            lock (_gate)
            {
                return _entries.TryRemove(trimmed, out _);
            }
        }

        using var connection = _connectionFactory.Create();
        Open(connection);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await LockAsync(connection, transaction, "sign-in-throttle", cancellationToken);
        var affected = await DeletePersistentAsync(connection, transaction, trimmed, cancellationToken);
        transaction.Commit();
        return affected > 0;
    }

    private static ActiveSignInLockout ToActiveLockout(
        string bucketKey,
        int failures,
        DateTime firstFailureAt,
        DateTime lockedUntil,
        DateTime now)
    {
        var isAccount = bucketKey.StartsWith("account:", StringComparison.Ordinal);
        var kind = isAccount ? "account" : "address";
        var target = isAccount
            ? bucketKey["account:".Length..]
            : bucketKey.StartsWith("address:", StringComparison.Ordinal)
                ? bucketKey["address:".Length..]
                : bucketKey;
        var limit = isAccount ? AccountLimit : AddressLimit;
        var remainingSeconds = Math.Max(0, (int)Math.Ceiling((lockedUntil - now).TotalSeconds));

        return new ActiveSignInLockout(
            bucketKey,
            kind,
            target,
            failures,
            limit,
            firstFailureAt,
            lockedUntil,
            remainingSeconds);
    }

    /// <summary>How long to wait before trying again; null means now.</summary>
    public TimeSpan? RetryAfter(string account, string? address)
    {
        lock (_gate)
        {
            Prune();
            return RetryAfterCore(account, address);
        }
    }

    /// <summary>
    ///     Atomically reserves one verification attempt. Charge first, then verify the password/OTP, so concurrent requests cannot slip past the check together.
    ///     On success the caller must call <see cref="RecordSuccess(string,string?)" /> to hand back this successful attempt.
    /// </summary>
    public TimeSpan? TryBeginAttempt(string account, string? address)
    {
        return BeginAttempt(account, address).RetryAfter;
    }

    /// <summary>In-memory counterpart of <see cref="BeginAttemptAsync" />.</summary>
    public SignInAttempt BeginAttempt(string account, string? address)
    {
        lock (_gate)
        {
            Prune();
            var wait = RetryAfterCore(account, address);

            if (wait is not null) return new SignInAttempt(wait, false, false);

            var now = _clock.GetUtcNow();
            var accountLocked = Record(AccountKey(account), AccountLimit, now);
            var addressLocked = !string.IsNullOrWhiteSpace(address) && Record(AddressKey(address), AddressLimit, now);

            return new SignInAttempt(null, accountLocked, addressLocked);
        }
    }

    private TimeSpan? RetryAfterCore(string account, string? address)
    {
        var now = _clock.GetUtcNow();
        TimeSpan? longest = null;

        foreach (var key in Keys(account, address))
            if (_entries.TryGetValue(key, out var entry) && entry.LockedUntil > now)
            {
                var remaining = entry.LockedUntil - now;
                longest = longest is null || remaining > longest ? remaining : longest;
            }

        return longest;
    }

    public void RecordFailure(string account, string? address)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Record(AccountKey(account), AccountLimit, now);

            if (!string.IsNullOrWhiteSpace(address)) Record(AddressKey(address), AddressLimit, now);
        }
    }

    /// <summary>Clears the account's record after success; the IP record stays, so someone sweeping accounts is not whitewashed by one hit.</summary>
    public void RecordSuccess(string account)
    {
        lock (_gate)
        {
            _entries.TryRemove(AccountKey(account), out _);
        }
    }

    /// <summary>Verification succeeded: clear the account record and hand the reserved successful attempt back to the IP count.</summary>
    public void RecordSuccess(string account, string? address)
    {
        lock (_gate)
        {
            _entries.TryRemove(AccountKey(account), out _);
            ReturnReservation(AddressKey(address ?? string.Empty), AddressLimit, address is not null);
        }
    }

    /// <summary>This check was not a failure (e.g. the password passed and OTP is next); only hands back the reserved quota.</summary>
    public void CancelAttempt(string account, string? address)
    {
        lock (_gate)
        {
            ReturnReservation(AccountKey(account), AccountLimit, true);
            ReturnReservation(AddressKey(address ?? string.Empty), AddressLimit, address is not null);
        }
    }

    private void ReturnReservation(string key, int limit, bool enabled)
    {
        if (!enabled || !_entries.TryGetValue(key, out var entry)) return;

        if (entry.Failures <= 1)
        {
            _entries.TryRemove(key, out _);
            return;
        }

        var failures = entry.Failures - 1;
        _entries[key] = entry with
        {
            Failures = failures,
            LockedUntil = failures >= limit ? entry.LockedUntil : DateTimeOffset.MinValue
        };
    }

    /// <summary>Charges one failure; true when this charge is the one that reaches the limit (the bucket was not locked before).</summary>
    private bool Record(string key, int limit, DateTimeOffset now)
    {
        var newlyLocked = false;
        _entries.AddOrUpdate(
            key,
            _ => new Entry(1, now, DateTimeOffset.MinValue),
            (_, current) =>
            {
                var failures = current.FirstFailureAt + Window < now ? 1 : current.Failures + 1;
                var lockedUntil = failures >= limit ? now + Lockout : current.LockedUntil;
                var firstFailure = failures == 1 ? now : current.FirstFailureAt;
                newlyLocked = failures >= limit && current.LockedUntil <= now;
                return new Entry(failures, firstFailure, lockedUntil);
            });
        return newlyLocked;
    }

    private void Prune()
    {
        var now = _clock.GetUtcNow();

        foreach (var pair in _entries)
            if (pair.Value.LockedUntil < now && pair.Value.FirstFailureAt + Window < now)
                _entries.TryRemove(pair.Key, out _);
    }

    private static IEnumerable<string> Keys(string account, string? address)
    {
        yield return AccountKey(account);

        if (!string.IsNullOrWhiteSpace(address)) yield return AddressKey(address);
    }

    private static string AccountKey(string account)
    {
        return $"account:{account.Trim().ToLowerInvariant()}";
    }

    /// <summary>IPv6 sources are counted per /64 (see <see cref="ClientAddressKey" />), so rotating addresses inside one does not help.</summary>
    private static string AddressKey(string address)
    {
        return $"address:{ClientAddressKey.For(address)}";
    }

    private async Task<SignInAttempt> TryBeginPersistentAsync(
        string account,
        string? address,
        CancellationToken cancellationToken)
    {
        var keys = Keys(account, address).ToArray();
        using var connection = _connectionFactory!.Create();
        Open(connection);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var now = await connection.ReadUtcNowAsync(Dialect, transaction, cancellationToken);
        await LockAsync(connection, transaction, "sign-in-throttle", cancellationToken);
        await PrunePersistentAsync(connection, transaction, now, cancellationToken);

        var rows = new Dictionary<string, PersistentEntry>(StringComparer.Ordinal);
        TimeSpan? longest = null;

        foreach (var key in keys)
        {
            var row = await ReadPersistentAsync(connection, transaction, key, cancellationToken);
            if (row is null) continue;

            rows[key] = row;
            if (row.LockedUntil > now)
            {
                var remaining = row.LockedUntil - now;
                longest = longest is null || remaining > longest ? remaining : longest;
            }
        }

        if (longest is not null)
        {
            transaction.Commit();
            return new SignInAttempt(longest, false, false);
        }

        var entryCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM SignInRateLimits;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        // A distributed botnet must not grow the table without bound, but refusing every identity the table has not seen
        // would lock all real users out (anyone could fill it with random names). The oldest buckets that are not locked
        // are dropped to make room; only when every row is an active lock do unseen identities wait.
        var needed = entryCount + keys.Count(key => !rows.ContainsKey(key)) - MaximumPersistentEntries;
        if (needed > 0)
        {
            var evictable = (await connection.QueryAsync<string>(new CommandDefinition(
                $"""
                 SELECT BucketKey FROM SignInRateLimits
                 WHERE LockedUntil IS NULL OR LockedUntil <= @Now
                 ORDER BY FirstFailureAt ASC, BucketKey ASC
                 {Dialect.Limit("@Take")};
                 """,
                new { Now = now, Take = (int)Math.Min(needed + EvictionBatch, int.MaxValue) },
                transaction,
                cancellationToken: cancellationToken))).Where(key => !keys.Contains(key)).ToList();

            if (evictable.Count < needed)
            {
                transaction.Commit();
                return new SignInAttempt(Lockout, false, false);
            }

            foreach (var chunk in evictable.Chunk(500))
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM SignInRateLimits WHERE BucketKey IN @Keys;",
                    new { Keys = chunk },
                    transaction,
                    cancellationToken: cancellationToken));
        }

        var accountLocked = false;
        var addressLocked = false;

        foreach (var key in keys)
        {
            var isAccount = key.StartsWith("account:", StringComparison.Ordinal);
            var locked = await RecordPersistentAsync(
                connection,
                transaction,
                key,
                isAccount ? AccountLimit : AddressLimit,
                now,
                rows.GetValueOrDefault(key),
                cancellationToken);

            if (isAccount) accountLocked = locked;
            else addressLocked = locked;
        }

        transaction.Commit();
        return new SignInAttempt(null, accountLocked, addressLocked);
    }

    private async Task MutatePersistentReservationAsync(
        string account,
        string? address,
        bool success,
        CancellationToken cancellationToken)
    {
        using var connection = _connectionFactory!.Create();
        Open(connection);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var now = await connection.ReadUtcNowAsync(Dialect, transaction, cancellationToken);
        await LockAsync(connection, transaction, "sign-in-throttle", cancellationToken);

        var accountKey = AccountKey(account);
        if (success)
            await DeletePersistentAsync(connection, transaction, accountKey, cancellationToken);
        else
            await ReturnPersistentReservationAsync(
                connection, transaction, accountKey, AccountLimit, now, cancellationToken);

        if (!string.IsNullOrWhiteSpace(address))
            await ReturnPersistentReservationAsync(
                connection, transaction, AddressKey(address), AddressLimit, now, cancellationToken);

        transaction.Commit();
    }

    /// <summary>Charges one failure; true when this charge is the one that reaches the limit.</summary>
    private async Task<bool> RecordPersistentAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string key,
        int limit,
        DateTime now,
        PersistentEntry? current,
        CancellationToken cancellationToken)
    {
        var failures = current is null || current.FirstFailureAt + Window < now
            ? 1
            : current.Failures + 1;
        var firstFailureAt = failures == 1 ? now : current!.FirstFailureAt;
        var lockedUntil = failures >= limit ? now + Lockout : current?.LockedUntil ?? now;
        var newlyLocked = failures >= limit && (current is null || current.LockedUntil <= now);
        var sql = Dialect.Upsert(
            "SignInRateLimits",
            [
                UpsertColumn.Parameter("BucketKey"),
                UpsertColumn.Parameter("Failures"),
                UpsertColumn.Parameter("FirstFailureAt"),
                UpsertColumn.Parameter("LockedUntil")
            ],
            ["BucketKey"],
            ["Failures", "FirstFailureAt", "LockedUntil"]);

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { BucketKey = key, Failures = failures, FirstFailureAt = firstFailureAt, LockedUntil = lockedUntil },
            transaction,
            cancellationToken: cancellationToken));

        return newlyLocked;
    }

    private async Task ReturnPersistentReservationAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string key,
        int limit,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var current = await ReadPersistentAsync(connection, transaction, key, cancellationToken);
        if (current is null) return;

        if (current.Failures <= 1)
        {
            await DeletePersistentAsync(connection, transaction, key, cancellationToken);
            return;
        }

        var failures = current.Failures - 1;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE SignInRateLimits SET Failures = @Failures, LockedUntil = @LockedUntil WHERE BucketKey = @BucketKey;",
            new
            {
                BucketKey = key,
                Failures = failures,
                LockedUntil = failures >= limit ? current.LockedUntil : now
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static Task<PersistentEntry?> ReadPersistentAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        return connection.QuerySingleOrDefaultAsync<PersistentEntry>(new CommandDefinition(
            "SELECT BucketKey, Failures, FirstFailureAt, LockedUntil FROM SignInRateLimits WHERE BucketKey = @BucketKey;",
            new { BucketKey = key },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static Task<int> DeletePersistentAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM SignInRateLimits WHERE BucketKey = @BucketKey;",
            new { BucketKey = key },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static Task<int> PrunePersistentAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        DateTime now,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM SignInRateLimits WHERE LockedUntil <= @Now AND FirstFailureAt <= @StaleBefore;",
            new { Now = now, StaleBefore = now - Window },
            transaction,
            cancellationToken: cancellationToken));
    }

    private async Task LockAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string name,
        CancellationToken cancellationToken)
    {
        var sql = Dialect.Upsert(
            "SecurityClaims",
            [
                UpsertColumn.Parameter("Name"),
                UpsertColumn.Parameter("Owner"),
                UpsertColumn.Parameter("ClaimedAt")
            ],
            ["Name"],
            null);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { Name = name, Owner = "shared-rate-limit", ClaimedAt = _clock.GetUtcNow().UtcDateTime },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT Owner FROM SecurityClaims{Dialect.ForUpdateHint} WHERE Name = @Name{Dialect.ForUpdateSuffix};",
            new { Name = name },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static void Open(IDbConnection connection)
    {
        if (connection.State != ConnectionState.Open) connection.Open();
    }

    private sealed record Entry(int Failures, DateTimeOffset FirstFailureAt, DateTimeOffset LockedUntil);

    /// <summary>
    ///     The outcome of reserving an attempt: <see cref="RetryAfter" /> is set when the caller is already locked out and must stop;
    ///     otherwise the two flags say whether this reservation is the one that reached the account or the address limit.
    /// </summary>
    public sealed record SignInAttempt(TimeSpan? RetryAfter, bool AccountLocked, bool AddressLocked);

    public sealed record ActiveSignInLockout(
        string BucketKey,
        string Kind,
        string Target,
        int Failures,
        int Limit,
        DateTime FirstFailureAt,
        DateTime LockedUntil,
        int RemainingSeconds);

    private sealed class PersistentEntry
    {
        public string BucketKey { get; init; } = string.Empty;
        public int Failures { get; init; }
        public DateTime FirstFailureAt { get; init; }
        public DateTime LockedUntil { get; init; }
    }
}