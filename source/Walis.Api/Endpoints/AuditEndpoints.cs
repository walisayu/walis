using System.Globalization;
using System.Text;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Endpoints;

/// <summary>Read-only; there is deliberately no endpoint that edits or deletes audit rows. The whole group is admin-only in the middleware.</summary>
public static class AuditEndpoints
{
    /// <summary>The export is bounded so one request cannot stream a year of rows; narrow the filters for more.</summary>
    public const int MaximumExportRows = 10_000;

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var audit = app.MapGroup("/api/audit").WithTags("Audit");

        audit.MapGet("/", async (int? limit, IAuditRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListAsync(limit ?? 50, cancellationToken)));

        // Database paging with filters, so a job's or an account's whole history is reachable instead of only the last 500 rows.
        audit.MapGet("/page", async (
            string? actor,
            string? entityType,
            string? entityId,
            string? action,
            string? outcome,
            DateTime? from,
            DateTime? to,
            int? page,
            int? pageSize,
            IAuditRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (ValidateFilters(actor, entityType, entityId, action, outcome, from, to) is { } error) return error;

            return Results.Ok(await repository.QueryAsync(
                new AuditQuery(actor, entityType, entityId, from, to, page ?? 1, pageSize ?? AuditQuery.DefaultPageSize, action, outcome),
                cancellationToken));
        });

        audit.MapGet("/export.csv", async (
            string? actor,
            string? entityType,
            string? entityId,
            string? action,
            string? outcome,
            DateTime? from,
            DateTime? to,
            IAuditRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (ValidateFilters(actor, entityType, entityId, action, outcome, from, to) is { } error) return error;

            var rows = new List<AuditEntry>();

            for (var page = 1; rows.Count < MaximumExportRows; page++)
            {
                var result = await repository.QueryAsync(
                    new AuditQuery(actor, entityType, entityId, from, to, page, AuditQuery.MaximumPageSize, action, outcome),
                    cancellationToken);
                rows.AddRange(result.Items);

                if (result.Items.Count < AuditQuery.MaximumPageSize) break;
            }

            if (rows.Count > MaximumExportRows) rows.RemoveRange(MaximumExportRows, rows.Count - MaximumExportRows);

            var csv = ToCsv(rows);
            var fileName = $"walis-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";

            return Results.File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", fileName);
        });

        return app;
    }

    private static IResult? ValidateFilters(
        string? actor,
        string? entityType,
        string? entityId,
        string? action,
        string? outcome,
        DateTime? from,
        DateTime? to)
    {
        var errors = new Dictionary<string, string[]>();

        if (actor?.Length > AuditText.MaximumActorLength) errors["actor"] = ["操作者篩選過長。"];
        if (entityType?.Length > 80) errors["entityType"] = ["對象類型篩選過長。"];
        if (entityId?.Length > AuditText.MaximumEntityIdLength) errors["entityId"] = ["對象識別碼篩選過長。"];
        if (action?.Length > 80) errors["action"] = ["動作篩選過長。"];
        if (outcome?.Length > 40) errors["outcome"] = ["結果篩選過長。"];
        if (from is { } start && to is { } end && end <= start) errors["to"] = ["結束時間必須晚於開始時間。"];

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    /// <summary>
    ///     RFC 4180 with a UTF-8 BOM so spreadsheets open it correctly. Every cell is quoted; control characters are removed (a
    ///     newline inside a quoted cell is legal CSV, but the point of the export is to be read by eye and by naive tools); and a cell
    ///     that starts with a formula trigger gets a leading apostrophe so a spreadsheet shows it as text instead of evaluating it.
    /// </summary>
    public static string ToCsv(IReadOnlyList<AuditEntry> rows)
    {
        var builder = new StringBuilder();
        builder.Append('﻿');
        builder.AppendLine("occurredAt,actor,action,entityType,entityId,jobId,outcome,remoteAddress,requestId,summary");

        foreach (var row in rows)
        {
            builder.AppendJoin(',',
                Cell(row.OccurredAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
                Cell(row.Actor),
                Cell(row.Action),
                Cell(row.EntityType),
                Cell(row.EntityId),
                Cell(row.JobId?.ToString()),
                Cell(row.Outcome),
                Cell(row.RemoteAddress),
                Cell(row.RequestId),
                Cell(row.Summary));
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";

        // The same character set the audit sink rejects, so a bidirectional override that slipped into an older row cannot
        // reorder the cell in a spreadsheet either.
        var text = new string(value.Where(character => !AuditText.IsDisallowed(character)).ToArray());

        // Formula injection guard: =, +, -, @ (and the tab/CR variants already removed above) must not reach a spreadsheet as a formula.
        if (text.Length != 0 && text[0] is '=' or '+' or '-' or '@') text = "'" + text;

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}