using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Walis.Api.Services;

/// <summary>
///     The in-memory queue of pending builds. With several nodes the worker regularly sweeps the database,
///     and the same build gets submitted repeatedly, so this remembers what is already queued; without deduplication
///     a build whose turn never comes would be pushed again on every poll and the queue would grow without bound.
/// </summary>
public sealed class BuildQueue
{
    private readonly Channel<Guid> _items = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private readonly HashSet<Guid> _pending = [];

    public bool TryEnqueue(Guid buildId)
    {
        lock (_pending)
        {
            if (!_pending.Add(buildId)) return false;
        }

        if (_items.Writer.TryWrite(buildId)) return true;

        lock (_pending)
        {
            _pending.Remove(buildId);
        }

        return false;
    }

    public async IAsyncEnumerable<Guid> DequeueAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var buildId in _items.Reader.ReadAllAsync(cancellationToken))
        {
            // Once dequeued it no longer counts as queued: if it was not its turn this round (e.g. assigned to a remote runner),
            // the next poll may submit it again.
            lock (_pending)
            {
                _pending.Remove(buildId);
            }

            yield return buildId;
        }
    }
}