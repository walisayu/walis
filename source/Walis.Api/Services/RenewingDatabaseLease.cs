using System.Diagnostics;
using Walis.Core.Repositories;

namespace Walis.Api.Services;

/// <summary>
///     Holds a database lease for the full lifetime of an operation. A unique owner value prevents a
///     delayed cleanup from releasing a lease that has already been taken over by another process.
///     Losing the lease cancels <see cref="Token" />, so callers fail closed instead of continuing an
///     uncoordinated backup or restore.
/// </summary>
internal sealed class RenewingDatabaseLease : IAsyncDisposable
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(30);
    private readonly Task _heartbeat;

    private readonly ILeaseRepository _leases;
    private readonly CancellationTokenSource _operation;
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;
    private long _lastConfirmedTimestamp = Stopwatch.GetTimestamp();

    private RenewingDatabaseLease(
        ILeaseRepository leases,
        string name,
        string owner,
        long confirmedTimestamp,
        CancellationToken cancellationToken)
    {
        _leases = leases;
        Name = name;
        Owner = owner;
        _lastConfirmedTimestamp = confirmedTimestamp;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _heartbeat = RenewAsync();
    }

    public string Name { get; }

    public string Owner { get; }

    public TimeSpan Duration => LeaseDuration;

    public CancellationToken Token => _operation.Token;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _stop.CancelAsync();

        try
        {
            await _heartbeat;
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await _leases.ReleaseAsync(Name, Owner, CancellationToken.None);
        }
        finally
        {
            _operation.Dispose();
            _stop.Dispose();
        }
    }

    public static async Task<RenewingDatabaseLease?> TryAcquireAsync(
        ILeaseRepository leases,
        string name,
        string owner,
        CancellationToken cancellationToken)
    {
        // Start the monotonic validity window before the database round trip. The database expiry
        // is assigned during that call, so this timestamp is conservatively no later than it.
        var attemptStarted = Stopwatch.GetTimestamp();
        if (!await leases.TryAcquireAsync(name, owner, LeaseDuration, cancellationToken)) return null;

        return new RenewingDatabaseLease(leases, name, owner, attemptStarted, cancellationToken);
    }

    /// <summary>
    ///     Renews synchronously at a destructive boundary. This is also an ownership check: if another
    ///     node took over after expiry, the current operation must not proceed.
    /// </summary>
    public async Task EnsureOwnedAsync(CancellationToken cancellationToken)
    {
        var attemptStarted = Stopwatch.GetTimestamp();

        try
        {
            if (await _leases.RenewAsync(Name, Owner, LeaseDuration, cancellationToken))
            {
                ConfirmAt(attemptStarted);
                return;
            }
        }
        catch
        {
            _operation.Cancel();
            throw;
        }

        _operation.Cancel();
        throw new InvalidOperationException($"資料庫租約 {Name} 已遺失，已中止操作。");
    }

    private async Task RenewAsync()
    {
        try
        {
            while (true)
            {
                var lastConfirmed = Interlocked.Read(ref _lastConfirmedTimestamp);
                var remaining = LeaseDuration - Stopwatch.GetElapsedTime(lastConfirmed);

                if (remaining <= TimeSpan.Zero)
                {
                    _operation.Cancel();
                    return;
                }

                await Task.Delay(
                    remaining < RenewalInterval ? remaining : RenewalInterval,
                    _stop.Token);

                var attemptStarted = Stopwatch.GetTimestamp();
                remaining = LeaseDuration - Stopwatch.GetElapsedTime(lastConfirmed, attemptStarted);

                if (remaining <= TimeSpan.Zero)
                {
                    _operation.Cancel();
                    return;
                }

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(remaining);
                using var cancelOperationAtExpiry = deadline.Token.Register(() =>
                {
                    // A synchronous boundary check can renew while this heartbeat request is
                    // blocked. Do not let the older attempt's timer cancel that newer proof.
                    if (!_stop.IsCancellationRequested &&
                        Interlocked.Read(ref _lastConfirmedTimestamp) <= lastConfirmed)
                        _operation.Cancel();
                });

                try
                {
                    if (await _leases.RenewAsync(Name, Owner, LeaseDuration, deadline.Token))
                    {
                        // If the conservative deadline fired while the database response was in
                        // flight, remain cancelled: continuity can no longer be proven.
                        if (_operation.IsCancellationRequested) return;

                        ConfirmAt(attemptStarted);
                        continue;
                    }

                    _operation.Cancel();
                    return;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    if (_operation.IsCancellationRequested) return;

                    // Another successful boundary renewal superseded this attempt.
                    continue;
                }
                catch
                {
                    // One transient timeout does not prove that ownership was lost, and SQLite can
                    // legitimately block this connection while the import transaction writes. We
                    // still fail closed no later than the last lease duration we positively proved.
                    if (!_operation.IsCancellationRequested) continue;
                }

                _operation.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private void ConfirmAt(long timestamp)
    {
        var current = Interlocked.Read(ref _lastConfirmedTimestamp);

        while (timestamp > current)
        {
            var observed = Interlocked.CompareExchange(ref _lastConfirmedTimestamp, timestamp, current);

            if (observed == current) return;

            current = observed;
        }
    }
}