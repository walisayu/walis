using Walis.Core.Execution;
using Walis.Core.Models;

namespace Walis.Core.Repositories;

public sealed record BuildCounts(
    long QueuedBuilds,
    long RunningBuilds,
    long SuccessfulBuilds,
    long FailedBuilds,
    long BuildsLast7Days,
    long FailuresLast7Days,
    long SucceededLast7Days,
    double AverageDurationSeconds,
    double AverageQueueSeconds);

public interface IJobRepository
{
    Task<IReadOnlyList<Job>> ListJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>Filtering, sorting and paging all happen in the database; with thousands of jobs the list page fetches one page only.</summary>
    Task<JobPage> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default);

    Task<Job?> GetJobAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Finds a job by name (for downstream triggers and import); with duplicates the earliest created wins.</summary>
    Task<Job?> GetJobByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<Job> CreateJobAsync(JobDraft draft, CancellationToken cancellationToken = default);

    Task<Job?> UpdateJobAsync(Guid id, JobDraft draft, CancellationToken cancellationToken = default);

    Task<Job?> SetJobEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);

    Task<bool> DeleteJobAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Build> QueueBuildAsync(Guid jobId, string triggeredBy, string? runnerName,
        CancellationToken cancellationToken = default);

    /// <summary>Matrix builds: one trigger produces several rows, each carrying its own combination.</summary>
    Task<Build> QueueBuildAsync(
        Guid jobId,
        string triggeredBy,
        string? runnerName,
        string? matrixLabel,
        string? matrixValues,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Checks the global/per-job capacity and enqueues the whole batch in a single transaction; returns null when capacity is short and never enqueues
    ///     only part of a matrix. Every normal trigger entry point should use this to avoid check-then-insert races.
    /// </summary>
    Task<IReadOnlyList<Build>?> TryQueueBuildsAsync(
        Guid jobId,
        string triggeredBy,
        string? runnerName,
        IReadOnlyList<BuildQueueItem> items,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Same as <see cref="TryQueueBuildsAsync" />, plus idempotency: with <paramref name="externalReference" /> the job's existing builds
    ///     for that reference are looked up inside the same locked transaction, and returned (same trigger hash) or reported as a conflict
    ///     instead of inserting new rows. The check happens before the capacity check so a retry still succeeds once the queue is full.
    /// </summary>
    Task<BuildQueueResult> QueueBuildsAsync(
        Guid jobId,
        string triggeredBy,
        string? runnerName,
        IReadOnlyList<BuildQueueItem> items,
        BuildExternalReference? externalReference,
        CancellationToken cancellationToken = default);

    /// <summary>The job's builds carrying this external reference, ordered by number; empty once they have been purged.</summary>
    Task<IReadOnlyList<Build>> FindBuildsByExternalRefAsync(
        Guid jobId,
        string externalRef,
        CancellationToken cancellationToken = default);

    Task<BuildContext?> GetBuildContextAsync(Guid buildId, CancellationToken cancellationToken = default);

    Task<bool> MarkBuildRunningAsync(
        Guid buildId,
        string runnerName,
        string? node = null,
        CancellationToken cancellationToken = default);

    /// <summary>Updates the output while running so the admin UI shows live progress; only affects builds in progress.</summary>
    Task UpdateBuildLogAsync(Guid buildId, string log, CancellationToken cancellationToken = default);

    /// <summary>Writes the planned steps when the build starts.</summary>
    Task CreateBuildStepsAsync(
        Guid buildId,
        IReadOnlyList<BuildStepPlan> plans,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BuildStep>> ListBuildStepsAsync(Guid buildId, CancellationToken cancellationToken = default);

    Task MarkBuildStepRunningAsync(Guid stepId, CancellationToken cancellationToken = default);

    /// <summary>Updates a step's output while running; only affects steps in progress.</summary>
    Task UpdateBuildStepLogAsync(Guid stepId, string log, CancellationToken cancellationToken = default);

    /// <param name="outputsJson">The step outputs as a JSON object; null when there are none.</param>
    Task CompleteBuildStepAsync(
        Guid stepId,
        string status,
        int? exitCode,
        string log,
        string? outputsJson = null,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the steps that never started as skipped after an earlier step failed.</summary>
    Task SkipPendingBuildStepsAsync(Guid buildId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Moves a Running build to its final state. Returns the number of rows changed: 0 means someone else already
    ///     completed it, and the caller must then skip notifications and downstream triggers so they are not sent twice.
    /// </summary>
    Task<int> CompleteBuildAsync(Guid buildId, BuildCompletion completion, CancellationToken cancellationToken = default);

    /// <summary>Cancels a queued build outright, or flags a running one so its executor stops it.</summary>
    Task<BuildCancellation> CancelBuildAsync(Guid buildId, CancellationToken cancellationToken = default);

    /// <summary>Whether a cancel has been requested for a running build; polled by the local executor.</summary>
    Task<bool> IsCancelRequestedAsync(Guid buildId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Build>> ListBuildsAsync(BuildQuery query, CancellationToken cancellationToken = default);

    /// <summary>The paged build list: filtering, permissions and paging all happen in the database, and the per-status tally comes back with it.</summary>
    Task<BuildPage> QueryBuildsAsync(BuildQuery query, CancellationToken cancellationToken = default);

    Task<Build?> GetBuildAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> ListQueuedBuildIdsAsync(
        string? runnerName = null,
        CancellationToken cancellationToken = default);

    Task<int> CountRunningBuildsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Build>> ListRunningBuildsAsync(CancellationToken cancellationToken = default);

    Task<string?> GetPreviousBuildStatusAsync(Guid jobId, int beforeNumber,
        CancellationToken cancellationToken = default);

    Task<bool> HasBuildQueuedSinceAsync(Guid jobId, DateTime since, CancellationToken cancellationToken = default);

    Task<Build?> ClaimNextBuildAsync(
        string runnerName,
        string? node = null,
        CancellationToken cancellationToken = default);

    /// <summary>The API node picking up its own work: queued builds with no runner assigned or assigned to the built-in runner.</summary>
    Task<Build?> ClaimNextLocalBuildAsync(
        string runnerName,
        string node,
        CancellationToken cancellationToken = default);

    Task<BuildCounts> GetBuildCountsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DailyBuildStat>> GetTrendAsync(int days, CancellationToken cancellationToken = default);

    /// <summary>Builds enqueued within the time window (without logs); the timeline uses them to draw the blocks that actually ran.</summary>
    Task<IReadOnlyList<Build>> ListBuildsBetweenAsync(DateTime from, DateTime to, int limit = 5_000, CancellationToken cancellationToken = default);

    /// <summary>Each job's average duration (seconds) over recent successful builds; the timeline uses it for lengths. Jobs without builds do not appear.</summary>
    Task<IReadOnlyDictionary<Guid, double>> GetAverageDurationsAsync(DateTime since, CancellationToken cancellationToken = default);

    Task AddArtifactsAsync(Guid buildId, IReadOnlyList<CollectedArtifact> artifacts,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BuildArtifact>> ListArtifactsAsync(Guid buildId, CancellationToken cancellationToken = default);

    Task<BuildArtifact?> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default);

    /// <summary>Deletes builds older than the retention days and returns the deleted build ids so the caller can clean up artifact directories.</summary>
    Task<IReadOnlyList<Guid>> PurgeBuildsAsync(int retentionDays, CancellationToken cancellationToken = default);

    Task<int> ResetStuckBuildsAsync(string? node = null, CancellationToken cancellationToken = default);
}