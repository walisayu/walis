using Microsoft.AspNetCore.Mvc;
using Walis.Api.Services;
using Walis.Api.Services.Ai;
using Walis.Core.Actions;
using Walis.Core.Models;
using Walis.Core.Plugins;
using Walis.Core.Repositories;

namespace Walis.Api.Endpoints;

public static class PluginEndpoints
{
    public static IEndpointRouteBuilder MapPluginEndpoints(this IEndpointRouteBuilder app)
    {
        var plugins = app.MapGroup("/api/plugins").WithTags("Plugins");

        plugins.MapGet("/", async (IPluginRepository repository, CancellationToken cancellationToken) =>
            Results.Ok((await repository.ListAsync(cancellationToken)).Select(Present)));

        plugins.MapGet("/{pluginId}", async (
            string pluginId,
            IPluginRepository repository,
            CancellationToken cancellationToken) =>
        {
            var plugin = await repository.GetAsync(pluginId, cancellationToken);
            return plugin is null ? Results.NotFound() : Results.Ok(plugin);
        });

        plugins.MapPost("/", async (
            PluginRequest request,
            IPluginRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var errors = Validate(request);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            var plugin = await repository.InstallAsync(
                new PluginDraft(request.Manifest!, request.Code!, request.IsTrusted),
                cancellationToken);

            // Trust is the flag that lets the plugin reach the network with secrets; it belongs in the record of who installed it.
            await audit.RecordAsync(
                AuditAction.Create,
                "plugin",
                plugin.PluginId,
                $"安裝外掛 {plugin.Manifest.Name}（信任：{(plugin.IsTrusted ? "是" : "否")}）",
                cancellationToken);

            return Results.Created($"/api/plugins/{plugin.PluginId}", plugin);
        }).WithMetadata(new RequestSizeLimitAttribute(512 * 1024));

        plugins.MapPost("/{pluginId}/disabled", async (
            string pluginId,
            DisablePluginRequest request,
            IPluginRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var plugin = await repository.SetDisabledAsync(pluginId, request.IsDisabled, cancellationToken);

            if (plugin is null) return Results.NotFound();

            await audit.RecordAsync(
                AuditAction.Update,
                "plugin",
                pluginId,
                $"{(request.IsDisabled ? "停用" : "啟用")}外掛 {plugin.Manifest.Name}",
                cancellationToken);

            return Results.Ok(plugin);
        });

        plugins.MapPost("/{pluginId}/trusted", async (
            string pluginId,
            TrustPluginRequest request,
            IPluginRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var plugin = await repository.SetTrustedAsync(pluginId, request.IsTrusted, cancellationToken);

            if (plugin is null) return Results.NotFound();

            await audit.RecordAsync(
                AuditAction.Update,
                "plugin",
                pluginId,
                $"{(request.IsTrusted ? "信任" : "撤銷信任")}外掛 {plugin.Manifest.Name}",
                cancellationToken);

            return Results.Ok(plugin);
        });

        plugins.MapDelete("/{pluginId}", async (
            string pluginId,
            IPluginRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (!await repository.UninstallAsync(pluginId, cancellationToken)) return Results.NotFound();

            await audit.RecordAsync(AuditAction.Delete, "plugin", pluginId, "移除外掛", cancellationToken);
            return Results.NoContent();
        });

        // Generates a draft with AI. Deliberately returns only a draft: not installed, not enabled, and carrying no permissions.
        plugins.MapPost("/generate", async (
            GenerateRequest request,
            IAiRepository ai,
            AiService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Requirement))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["requirement"] = ["請描述這個外掛要做什麼。"]
                });

            if (request.Requirement.Length > 4_000)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["requirement"] = ["需求描述請控制在 4,000 個字元以內。"]
                });

            var provider = await ai.ResolveProviderAsync(request.ProviderId, cancellationToken);

            if (provider is null) return Results.Conflict(new { message = "尚未設定可用的 AI 供應商，請先到 AI 助理頁面填寫金鑰。" });

            var answer = await service.CompleteAsync(
                provider,
                PluginGenerator.SystemPrompt,
                PluginGenerator.BuildUserPrompt(request.Requirement),
                AiPurpose.GeneratePlugin,
                null,
                null,
                cancellationToken);

            try
            {
                var generated = PluginGenerator.Parse(answer.Text);

                return Results.Ok(new
                {
                    ok = true,
                    generated.Manifest,
                    generated.Code,
                    generated.Warnings
                });
            }
            catch (Exception exception)
            {
                // When parsing fails, return the raw text so the user can see what the model said.
                return Results.Ok(new { ok = false, message = exception.Message, raw = answer.Text });
            }
        }).WithMetadata(new RequestSizeLimitAttribute(16 * 1024));

        // Look at the script it produces before installing: a plugin's output is data, and only what a person can read is safe to use.
        plugins.MapPost("/preview", (PreviewRequest request, PluginHost host) =>
        {
            var errors = Validate(new PluginRequest(request.Manifest, request.Code, request.IsTrusted));
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            if (request.Inputs is { Count: > 128 } ||
                request.Inputs?.Sum(pair => pair.Key.Length + (pair.Value?.Length ?? 0)) > 256_000)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["inputs"] = ["預覽輸入的數量或總長度超過上限。"]
                });

            var plugin = new Plugin
            {
                PluginId = request.Manifest!.Id,
                Manifest = request.Manifest,
                Code = request.Code!,

                // The preview runs with the trust state that would be chosen at install time, so an untrusted plugin's outbound access is seen blocked right here.
                IsTrusted = request.IsTrusted
            };

            try
            {
                var result = host.Render(new PluginRenderRequest(
                    plugin,
                    request.Inputs ?? new Dictionary<string, string?>(),
                    new Dictionary<string, string>()));

                return Results.Ok(new { ok = true, script = result.Script });
            }
            catch (PluginException exception)
            {
                return Results.Ok(new { ok = false, message = exception.Message });
            }
        }).WithMetadata(new RequestSizeLimitAttribute(512 * 1024));

        return app;
    }

    /// <summary>The list omits the code: it is usually long and the list page does not need it.</summary>
    private static object Present(Plugin plugin)
    {
        return new
        {
            plugin.PluginId,
            plugin.Manifest,
            plugin.IsDisabled,
            plugin.IsTrusted,
            plugin.InstalledAt,
            plugin.UpdatedAt,
            codeLength = plugin.Code.Length
        };
    }

    private static Dictionary<string, string[]> Validate(PluginRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var manifest = request.Manifest;

        if (manifest is null)
        {
            errors["manifest"] = ["請提供 manifest。"];
            return errors;
        }

        manifest.Permissions ??= new PluginPermissions();
        manifest.Permissions.Http ??= [];
        manifest.Permissions.Secrets ??= [];
        manifest.Parameters ??= [];

        if (manifest.Id?.Length > 200 || !ActionManifest.IsValidId(manifest.Id))
            errors["id"] = ["識別字請用小寫英數與點，例如 acme.deploy。"];

        if (string.IsNullOrWhiteSpace(manifest.Name))
            errors["name"] = ["請輸入名稱。"];
        else if (manifest.Name.Length > 200) errors["name"] = ["名稱不可超過 200 個字元。"];
        else if (AuditText.HasControlCharacters(manifest.Name)) errors["name"] = ["名稱不可包含控制字元。"];

        if (!PluginKinds.IsValid(manifest.Kind)) errors["kind"] = ["目前只支援 action 類型的外掛。"];

        if (manifest.ApiVersion != PluginHost.CurrentApiVersion)
            errors["apiVersion"] = [$"這個版本的 Walis 提供 host API {PluginHost.CurrentApiVersion}。"];

        if (string.IsNullOrWhiteSpace(request.Code))
            errors["code"] = ["請提供外掛程式碼。"];
        else if (request.Code.Length > PluginHost.MaximumCodeLength)
            errors["code"] = [$"外掛程式碼不可超過 {PluginHost.MaximumCodeLength:N0} 個字元。"];

        if (manifest.Description?.Length > 4000 || manifest.Version?.Length > 100 ||
            manifest.Author?.Length > 200 || manifest.Category?.Length > 50 || manifest.Icon?.Length > 100)
            errors["manifest"] = ["外掛說明欄位過長。"];

        if (manifest.Permissions.Http.Count > 64 || manifest.Permissions.Secrets.Count > 64 ||
            manifest.Permissions.Http.Any(value => string.IsNullOrWhiteSpace(value) ||
                                                   value.Length > PluginHost.MaximumHttpUrlLength ||
                                                   !PluginHost.IsAllowed(value, [value])) ||
            manifest.Permissions.Secrets.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
                                                      !IsSafeSecretName(value)))
            errors["permissions"] = ["外掛權限的數量、網址或機密名稱格式無效。"];

        if (manifest.Parameters.Count > 64 || manifest.Parameters.Any(InvalidParameter))
            errors["parameters"] = ["外掛參數的數量或欄位長度超過上限。"];

        return errors;
    }

    private static bool InvalidParameter(ActionParameter parameter)
    {
        return parameter is null ||
               parameter.Name?.Length > 128 || parameter.Label?.Length > 200 || parameter.Type?.Length > 32 ||
               parameter.Default?.Length > 4096 || parameter.Placeholder?.Length > 1000 ||
               parameter.Help?.Length > 4000 ||
               parameter.Options is null || parameter.Options.Count > 64 ||
               parameter.Options.Any(option =>
                   option is null || option.Value?.Length > 1000 || option.Label?.Length > 1000);
    }

    private static bool IsSafeSecretName(string value)
    {
        return (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
               value.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }
}

public sealed record PluginRequest(PluginManifest? Manifest, string? Code, bool IsTrusted);

public sealed record DisablePluginRequest(bool IsDisabled);

public sealed record TrustPluginRequest(bool IsTrusted);

public sealed record GenerateRequest(string? Requirement, Guid? ProviderId);

public sealed record PreviewRequest(
    PluginManifest? Manifest,
    string? Code,
    Dictionary<string, string?>? Inputs,
    bool IsTrusted = false);