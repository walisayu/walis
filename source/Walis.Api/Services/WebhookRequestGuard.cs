using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Walis.Core.Database;

namespace Walis.Api.Services;

public enum WebhookRequestDecision
{
    Allowed,
    Replay,
    RateLimited
}

public sealed record WebhookRequestGuardResult(WebhookRequestDecision Decision, TimeSpan RetryAfter)
{
    public static readonly WebhookRequestGuardResult Allowed =
        new(WebhookRequestDecision.Allowed, TimeSpan.Zero);
}

/// <summary>
///     Replay and abuse protection for webhooks. The production service keeps the state in the shared database, so the same delivery
///     is accepted only once across nodes and restarts; the constructor used by pure-logic tests keeps a bounded in-memory implementation.
/// </summary>
public sealed class WebhookRequestGuard
{
    private const int DefaultRequestLimit = 60;
    private const int DefaultReplayCapacity = 10_000;
    private const int MaximumClientEntries = 2_048;
    private static readonly TimeSpan DefaultRateWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultReplayTtl = TimeSpan.FromMinutes(10);
    private readonly TimeProvider _clock;
    private readonly IDbConnectionFactory? _connectionFactory;
    private readonly Dictionary<string, DateTimeOffset> _deliveries = new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private readonly TimeSpan _rateWindow;
    private readonly int _replayCapacity;
    private readonly TimeSpan _replayTtl;
    private readonly int _requestLimit;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _requests = new(StringComparer.Ordinal);

    public WebhookRequestGuard(IDbConnectionFactory connectionFactory)
        : this(TimeProvider.System, DefaultRequestLimit, DefaultRateWindow, DefaultReplayTtl)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public WebhookRequestGuard()
        : this(TimeProvider.System, DefaultRequestLimit, DefaultRateWindow, DefaultReplayTtl)
    {
    }

    public WebhookRequestGuard(
        TimeProvider clock,
        int requestLimit,
        TimeSpan rateWindow,
        TimeSpan replayTtl,
        int replayCapacity = DefaultReplayCapacity)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rateWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(replayTtl, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(replayCapacity);

        _clock = clock;
        _requestLimit = requestLimit;
        _rateWindow = rateWindow;
        _replayTtl = replayTtl;
        _replayCapacity = replayCapacity;
    }

    private ISqlDialect Dialect => (_connectionFactory
                                    ?? throw new InvalidOperationException(
                                        "Persistent security state requires a database connection factory.")).Dialect;

    public WebhookRequestGuardResult Evaluate(HttpRequest request, string body)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);

        var now = _clock.GetUtcNow();
        var client = ClientKey(request.HttpContext.Connection.RemoteIpAddress);
        var deliveries = DeliveryKeys(request, body);

        lock (_gate)
        {
            PruneExpiredDeliveries(now);

            foreach (var delivery in deliveries)
                if (_deliveries.TryGetValue(delivery, out var expiresAt))
                    return new WebhookRequestGuardResult(
                        WebhookRequestDecision.Replay,
                        PositiveRetryAfter(expiresAt - now));

            if (!_requests.TryGetValue(client, out var attempts))
            {
                if (_requests.Count >= MaximumClientEntries) PruneIdleClients(now);

                attempts = new Queue<DateTimeOffset>();
                _requests[client] = attempts;
            }

            PruneAttempts(attempts, now);

            if (attempts.Count >= _requestLimit)
                return new WebhookRequestGuardResult(
                    WebhookRequestDecision.RateLimited,
                    PositiveRetryAfter(attempts.Peek() + _rateWindow - now));

            attempts.Enqueue(now);

            foreach (var delivery in deliveries)
            {
                EnsureReplayCapacity();
                _deliveries[delivery] = now + _replayTtl;
            }

            return WebhookRequestGuardResult.Allowed;
        }
    }

    public Task<WebhookRequestGuardResult> EvaluateAsync(
        HttpRequest request,
        string body,
        CancellationToken cancellationToken = default)
    {
        return _connectionFactory is null
            ? Task.FromResult(Evaluate(request, body))
            : EvaluatePersistentAsync(request, body, cancellationToken);
    }

    private void PruneAttempts(Queue<DateTimeOffset> attempts, DateTimeOffset now)
    {
        var threshold = now - _rateWindow;

        while (attempts.TryPeek(out var attempt) && attempt <= threshold) attempts.Dequeue();
    }

    private void PruneIdleClients(DateTimeOffset now)
    {
        foreach (var entry in _requests.ToArray())
        {
            PruneAttempts(entry.Value, now);

            if (entry.Value.Count == 0) _requests.Remove(entry.Key);
        }

        // Keeps a fixed cap even under a flood of forged sources; forwarded headers are only honored by the trusted-proxy middleware.
        if (_requests.Count >= MaximumClientEntries) _requests.Remove(_requests.Keys.First());
    }

    private void PruneExpiredDeliveries(DateTimeOffset now)
    {
        foreach (var entry in _deliveries.Where(entry => entry.Value <= now).ToArray()) _deliveries.Remove(entry.Key);
    }

    private void EnsureReplayCapacity()
    {
        if (_deliveries.Count < _replayCapacity) return;

        var oldest = _deliveries.MinBy(entry => entry.Value);
        _deliveries.Remove(oldest.Key);
    }

    private static string ClientKey(IPAddress? address)
    {
        return address?.ToString() ?? "unknown";
    }

    /// <summary>
    ///     The replay keys of a request: the body hash always, plus the delivery id when the caller sent one. A request is a replay
    ///     when either is already known. The signature does not cover the delivery header, so a captured request must not become
    ///     fresh again just by changing the id; and a provider re-sending the same id with a different body is still a re-delivery.
    /// </summary>
    public static IReadOnlyList<string> DeliveryKeys(HttpRequest request, string body)
    {
        var keys = new List<string>(2)
        {
            "body:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
        };

        foreach (var header in new[] { "X-GitHub-Delivery", "X-Gitlab-Event-UUID", "X-Walis-Delivery" })
        {
            var value = request.Headers[header].ToString().Trim();

            if (value.Length == 0) continue;

            // The externally controlled delivery id is hashed first, so an overly long header cannot bloat the in-memory replay cache.
            keys.Add("id:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{header}:{value}"))));
            break;
        }

        return keys;
    }

    private static TimeSpan PositiveRetryAfter(TimeSpan value)
    {
        return value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(1);
    }

    private async Task<WebhookRequestGuardResult> EvaluatePersistentAsync(
        HttpRequest request,
        string body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);

        var client = ClientKey(request.HttpContext.Connection.RemoteIpAddress);
        var deliveries = DeliveryKeys(request, body);
        using var connection = _connectionFactory!.Create();
        Open(connection);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var now = await connection.ReadUtcNowAsync(Dialect, transaction, cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM WebhookDeliveries WHERE ExpiresAt <= @Now;",
            new { Now = now },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM WebhookRateLimits WHERE WindowStartedAt <= @StaleBefore;",
            new { StaleBefore = now - _rateWindow },
            transaction,
            cancellationToken: cancellationToken));

        var rate = await connection.QuerySingleOrDefaultAsync<PersistentRate>(new CommandDefinition(
            "SELECT ClientKey, WindowStartedAt, RequestCount FROM WebhookRateLimits WHERE ClientKey = @ClientKey;",
            new { ClientKey = client },
            transaction,
            cancellationToken: cancellationToken));

        if (rate is not null && rate.WindowStartedAt + _rateWindow > now && rate.RequestCount >= _requestLimit)
        {
            transaction.Commit();
            return new WebhookRequestGuardResult(
                WebhookRequestDecision.RateLimited,
                PositiveRetryAfter(rate.WindowStartedAt + _rateWindow - now));
        }

        if (rate is null) await EnsureRateCapacityAsync(connection, transaction, cancellationToken);

        var windowStartedAt = rate is null || rate.WindowStartedAt + _rateWindow <= now
            ? now
            : rate.WindowStartedAt;
        var requestCount = rate is null || rate.WindowStartedAt + _rateWindow <= now
            ? 1
            : rate.RequestCount + 1;
        var rateSql = Dialect.Upsert(
            "WebhookRateLimits",
            [
                UpsertColumn.Parameter("ClientKey"),
                UpsertColumn.Parameter("WindowStartedAt"),
                UpsertColumn.Parameter("RequestCount")
            ],
            ["ClientKey"],
            ["WindowStartedAt", "RequestCount"]);
        await connection.ExecuteAsync(new CommandDefinition(
            rateSql,
            new { ClientKey = client, WindowStartedAt = windowStartedAt, RequestCount = requestCount },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var delivery in deliveries)
        {
            var expiresAt = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT ExpiresAt FROM WebhookDeliveries WHERE DeliveryHash = @DeliveryHash;",
                new { DeliveryHash = delivery },
                transaction,
                cancellationToken: cancellationToken));

            if (expiresAt is null) continue;

            transaction.Commit();
            return new WebhookRequestGuardResult(
                WebhookRequestDecision.Replay,
                PositiveRetryAfter(expiresAt.Value - now));
        }

        foreach (var delivery in deliveries)
        {
            await EnsureDeliveryCapacityAsync(connection, transaction, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO WebhookDeliveries (DeliveryHash, ExpiresAt) VALUES (@DeliveryHash, @ExpiresAt);",
                new { DeliveryHash = delivery, ExpiresAt = now + _replayTtl },
                transaction,
                cancellationToken: cancellationToken));
        }

        transaction.Commit();
        return WebhookRequestGuardResult.Allowed;
    }

    private async Task EnsureRateCapacityAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM WebhookRateLimits;",
            transaction: transaction,
            cancellationToken: cancellationToken));
        if (count < MaximumClientEntries) return;

        var oldest = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT ClientKey FROM WebhookRateLimits ORDER BY WindowStartedAt {Dialect.Limit("1")};",
            transaction: transaction,
            cancellationToken: cancellationToken));
        if (oldest is not null)
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM WebhookRateLimits WHERE ClientKey = @ClientKey;",
                new { ClientKey = oldest },
                transaction,
                cancellationToken: cancellationToken));
    }

    private async Task EnsureDeliveryCapacityAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM WebhookDeliveries;",
            transaction: transaction,
            cancellationToken: cancellationToken));
        if (count < _replayCapacity) return;

        var oldest = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT DeliveryHash FROM WebhookDeliveries ORDER BY ExpiresAt {Dialect.Limit("1")};",
            transaction: transaction,
            cancellationToken: cancellationToken));
        if (oldest is not null)
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM WebhookDeliveries WHERE DeliveryHash = @DeliveryHash;",
                new { DeliveryHash = oldest },
                transaction,
                cancellationToken: cancellationToken));
    }

    private async Task LockAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string name = "webhook-request-guard";
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
            new { Name = name, Owner = "shared-webhook-guard", ClaimedAt = _clock.GetUtcNow().UtcDateTime },
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

    private sealed class PersistentRate
    {
        public string ClientKey { get; init; } = string.Empty;
        public DateTime WindowStartedAt { get; init; }
        public int RequestCount { get; init; }
    }
}