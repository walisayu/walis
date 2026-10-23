using Walis.Api.Services;

namespace Walis.Tests;

public class BuildQueueTests
{
    [Fact]
    public async Task SameBuildIsNotEnqueuedTwice()
    {
        // With several nodes the worker sweeps the database every few seconds and the same build is submitted repeatedly;
        // without deduplication a build whose turn never comes would be pushed again every round and the queue would grow without bound.
        var queue = new BuildQueue();
        var buildId = Guid.NewGuid();

        Assert.True(queue.TryEnqueue(buildId));
        Assert.False(queue.TryEnqueue(buildId));
        Assert.False(queue.TryEnqueue(buildId));

        using var cancellation = new CancellationTokenSource();
        var taken = new List<Guid>();

        await foreach (var item in queue.DequeueAllAsync(cancellation.Token))
        {
            taken.Add(item);
            cancellation.Cancel();
            break;
        }

        Assert.Equal([buildId], taken);
    }

    [Fact]
    public async Task CanBeEnqueuedAgainAfterDequeue()
    {
        // It was not its turn this round (e.g. assigned to a remote runner); the next poll must be able to submit it again.
        var queue = new BuildQueue();
        var buildId = Guid.NewGuid();

        queue.TryEnqueue(buildId);

        using var cancellation = new CancellationTokenSource();

        await foreach (var _ in queue.DequeueAllAsync(cancellation.Token))
        {
            cancellation.Cancel();
            break;
        }

        Assert.True(queue.TryEnqueue(buildId));
    }
}