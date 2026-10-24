using Walis.Api.Services;
using Walis.Core.Actions;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Endpoints;

public static class ActionEndpoints
{
    public static IEndpointRouteBuilder MapActionEndpoints(this IEndpointRouteBuilder app)
    {
        var actions = app.MapGroup("/api/actions").WithTags("Actions");

        actions.MapGet("/", async (IActionRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                categories = ActionCategory.All,
                definitions = await repository.ListAsync(cancellationToken)
            }));

        actions.MapPost("/preview", async (
            ActionPreviewRequest request,
            IActionRepository repository,
            CancellationToken cancellationToken) =>
        {
            var definitions = await repository.MapAsync(cancellationToken);
            var result = ActionScriptCompiler.Compile(request.Actions ?? [], definitions);

            return Results.Ok(new { script = result.Script, errors = result.Errors });
        });

        actions.MapPost("/install", async (
            ActionInstallRequest request,
            IActionRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Manifest))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["manifest"] = ["請貼上動作定義的 JSON。"]
                });

            if (!ActionManifest.TryParse(request.Manifest, out var definition, out var errors))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["manifest"] = [.. errors]
                });

            await repository.InstallAsync(definition!, cancellationToken);
            await audit.RecordAsync(
                AuditAction.Create,
                "action",
                definition!.Id,
                $"安裝動作 {definition.Name}（{definition.Id}）",
                cancellationToken);

            return Results.Ok(definition);
        });

        // Action packs: the JSON in the directory is managed by git and reviewed by people; this only reads and reloads.
        actions.MapGet("/packs", (ActionPackProvider packs) => Results.Ok(new
        {
            roots = packs.Roots,
            packs = packs.Packs.Select(Describe)
        }));

        actions.MapPost("/packs/reload", async (ActionPackProvider packs, AuditService audit, CancellationToken cancellationToken) =>
        {
            var reloaded = packs.Reload();
            await audit.RecordAsync(AuditAction.Update, "action-pack", "all", $"重新載入動作套件（{reloaded.Count} 個）", cancellationToken);
            return Results.Ok(new { roots = packs.Roots, packs = reloaded.Select(Describe) });
        });

        actions.MapDelete("/{id}", async (
            string id,
            IActionRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (BuiltInActions.All.Any(action => action.Id == id))
                return Results.Conflict(new { message = "內建動作不可移除。" });

            if (!await repository.UninstallAsync(id, cancellationToken)) return Results.NotFound();

            await audit.RecordAsync(AuditAction.Delete, "action", id, $"移除動作 {id}", cancellationToken);
            return Results.NoContent();
        });

        return app;
    }

    private static object Describe(ActionPackInfo pack) => new
    {
        pack.Name,
        pack.Version,
        pack.Description,
        pack.Directory,
        Count = pack.Definitions.Count,
        Ids = pack.Definitions.Select(definition => definition.Id),
        pack.Errors
    };
}

public sealed record ActionPreviewRequest(List<ActionStep>? Actions);

public sealed record ActionInstallRequest(string? Manifest);