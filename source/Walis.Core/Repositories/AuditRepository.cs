using Dapper;
using Walis.Core.Database;
using Walis.Core.Models;

namespace Walis.Core.Repositories;

public sealed class AuditRepository(IDbConnectionFactory connectionFactory) : IAuditRepository
{
    private const string Columns =
        "Id, OccurredAt, Actor, Action, EntityType, EntityId, Summary, RemoteAddress, Outcome, RequestId, JobId";

    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
             INSERT INTO AuditLogs ({Columns})
             VALUES (@Id, @OccurredAt, @Actor, @Action, @EntityType, @EntityId, @Summary, @RemoteAddress, @Outcome, @RequestId, @JobId);
             """,
            new
            {
                Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
                OccurredAt = entry.OccurredAt == default ? DateTime.UtcNow : entry.OccurredAt,

                // The last line of defence against a value longer than the column or carrying a newline: callers already clean, but a
                // varchar(n) overflow would fail the INSERT on PostgreSQL/MySQL/SQL Server and lose the record entirely.
                Actor = AuditText.Clean(entry.Actor, AuditText.MaximumActorLength),
                entry.Action,
                entry.EntityType,
                EntityId = AuditText.CleanOrNull(entry.EntityId, AuditText.MaximumEntityIdLength),
                Summary = AuditText.Clean(entry.Summary, AuditText.MaximumSummaryLength),
                RemoteAddress = AuditText.CleanOrNull(entry.RemoteAddress, AuditText.MaximumAddressLength),
                Outcome = string.IsNullOrWhiteSpace(entry.Outcome) ? AuditOutcome.Success : entry.Outcome,
                RequestId = AuditText.CleanOrNull(entry.RequestId, AuditText.MaximumRequestIdLength),
                entry.JobId
            },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var entries = await connection.QueryAsync<AuditEntry>(new CommandDefinition(
            $"""
             SELECT {Columns}
             FROM AuditLogs
             ORDER BY OccurredAt DESC, Id ASC
             {_dialect.Limit("@Limit")};
             """,
            new { Limit = Math.Clamp(limit, 1, 500) },
            cancellationToken: cancellationToken));
        return entries.AsList();
    }

    public async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, AuditQuery.MaximumPageSize);

        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            conditions.Add("Actor = @Actor");
            parameters.Add("Actor", query.Actor.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            conditions.Add("EntityType = @EntityType");
            parameters.Add("EntityType", query.EntityType.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.EntityId))
        {
            conditions.Add("EntityId = @EntityId");
            parameters.Add("EntityId", query.EntityId.Trim());
        }

        if (query.From is { } from)
        {
            conditions.Add("OccurredAt >= @From");
            parameters.Add("From", DateTime.SpecifyKind(from, DateTimeKind.Utc));
        }

        if (query.To is { } to)
        {
            conditions.Add("OccurredAt < @To");
            parameters.Add("To", DateTime.SpecifyKind(to, DateTimeKind.Utc));
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            conditions.Add("Action = @Action");
            parameters.Add("Action", query.Action.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            conditions.Add("Outcome = @Outcome");
            parameters.Add("Outcome", query.Outcome.Trim());
        }

        var where = conditions.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", conditions)}";
        parameters.Add("Take", pageSize);
        parameters.Add("Skip", (page - 1) * pageSize);

        // OccurredAt is not unique, so Id joins the ordering; otherwise two rows written in the same instant could repeat or vanish between pages.
        using var connection = connectionFactory.Create();
        var items = (await connection.QueryAsync<AuditEntry>(new CommandDefinition(
            $"""
             SELECT {Columns}
             FROM AuditLogs
             {where}
             ORDER BY OccurredAt DESC, Id ASC
             {_dialect.Page("@Take", "@Skip")};
             """,
            parameters,
            cancellationToken: cancellationToken))).AsList();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT {_dialect.CountAll} FROM AuditLogs {where};",
            parameters,
            cancellationToken: cancellationToken));

        return new AuditPage(items, (int)total, page, pageSize);
    }

    public async Task<SecurityAuditSummary> GetSecuritySummaryAsync(
        DateTime sinceUtc,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var since = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc);
        var cappedLimit = Math.Clamp(limit, 1, 200);

        using var connection = connectionFactory.Create();
        var signInFailures = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT {_dialect.CountAll} FROM AuditLogs WHERE OccurredAt >= @Since AND Action = @Action;",
            new { Since = since, Action = AuditAction.SignInFailed },
            cancellationToken: cancellationToken));
        var lockouts = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT {_dialect.CountAll} FROM AuditLogs WHERE OccurredAt >= @Since AND Action = @Action;",
            new { Since = since, Action = AuditAction.LockedOut },
            cancellationToken: cancellationToken));
        var denied = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT {_dialect.CountAll} FROM AuditLogs WHERE OccurredAt >= @Since AND Outcome = @Outcome;",
            new { Since = since, Outcome = AuditOutcome.Denied },
            cancellationToken: cancellationToken));
        var secretReveals = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT {_dialect.CountAll} FROM AuditLogs WHERE OccurredAt >= @Since AND Action = @Action;",
            new { Since = since, Action = AuditAction.Reveal },
            cancellationToken: cancellationToken));

        var recentEvents = (await connection.QueryAsync<AuditEntry>(new CommandDefinition(
            $"""
             SELECT {Columns}
             FROM AuditLogs
             WHERE Action IN ('sign-in-failed', 'locked-out', 'reveal')
                OR Outcome IN ('denied', 'failed')
                OR EntityType IN ('settings', 'user', 'api-key', 'plugin', 'security')
             ORDER BY OccurredAt DESC, Id ASC
             {_dialect.Limit("@Limit")};
             """,
            new { Limit = cappedLimit },
            cancellationToken: cancellationToken))).AsList();

        return new SecurityAuditSummary(
            new SecurityAuditMetrics((int)signInFailures, (int)lockouts, (int)denied, (int)secretReveals),
            recentEvents);
    }

    public async Task<int> PurgeAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0) return 0;

        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM AuditLogs WHERE OccurredAt < @Threshold;",
            new { Threshold = DateTime.UtcNow.AddDays(-retentionDays) },
            cancellationToken: cancellationToken));
    }
}