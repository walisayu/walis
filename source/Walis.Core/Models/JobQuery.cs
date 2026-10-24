namespace Walis.Core.Models;

/// <summary>Sort keys for the job list; the names match the frontend table headers, and unknown values fall back to <see cref="Name" />.</summary>
public static class JobSort
{
    public const string Name = "name";
    public const string Project = "project";
    public const string LastBuild = "lastBuild";
    public const string SuccessRate = "successRate";
    public const string Updated = "updated";

    public static readonly IReadOnlyList<string> All = [Name, Project, LastBuild, SuccessRate, Updated];

    public static string Normalize(string? sort)
    {
        return All.FirstOrDefault(value => string.Equals(value, sort, StringComparison.OrdinalIgnoreCase)) ?? Name;
    }
}

/// <summary>Status filter for the job list.</summary>
public static class JobStatusFilter
{
    public const string Enabled = "enabled";
    public const string Paused = "paused";
}

/// <summary>
///     A paged job query. There may be thousands of jobs, so filtering, sorting and paging happen in the database and the frontend fetches one page at a time.
///     <paramref name="ViewRoles" /> is the set of ViewRoles the caller can see; null means unrestricted (internal use only).
/// </summary>
public sealed record JobQuery(
    string? Search = null,
    string? Status = null,
    string? TriggerKind = null,
    string? Project = null,
    IReadOnlyList<string>? ViewRoles = null,
    string Sort = JobSort.Name,
    bool Descending = false,
    int Page = 1,
    int PageSize = JobQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 200;
}

/// <summary>
///     One page of jobs. <see cref="TotalAll" />, <see cref="EnabledAll" /> and <see cref="Projects" /> are limited only by permissions, not by the filter,
///     for the header statistics and the project dropdown.
/// </summary>
public sealed record JobPage(
    IReadOnlyList<Job> Items,
    int Total,
    int Page,
    int PageSize,
    int TotalAll,
    int EnabledAll,
    IReadOnlyList<string> Projects);