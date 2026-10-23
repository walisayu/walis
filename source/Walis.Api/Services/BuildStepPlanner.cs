using System.Text.Json;
using Walis.Core.Actions;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Services;

/// <summary>
///     Expands a job into build steps. Each action of an action flow is one step; a job with a directly edited script has one step.
///     Steps are derived at build time and the job itself stores none, so existing jobs need no migration.
/// </summary>
public sealed class BuildStepPlanner(
    IActionRepository actions,
    PluginActionRendererFactory renderers,
    ILogger<BuildStepPlanner> logger)
{
    private const int MaximumActionsJsonLength = 262_144;
    private const int MaximumSteps = 100;
    private const int MaximumParametersPerStep = 64;
    private const int MaximumGeneratedScriptLength = 20_000;

    public const string SingleStepName = "腳本";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16
    };

    public async Task<IReadOnlyList<BuildStepPlan>> PlanAsync(Job job, CancellationToken cancellationToken = default)
    {
        if (job.ScriptMode == ScriptModes.Source) return [new BuildStepPlan(SingleStepName, job.Command, false)];

        if (job.ScriptMode != ScriptModes.Actions) throw new BuildPlanningException("任務的腳本模式無效，已拒絕執行。");

        if (string.IsNullOrWhiteSpace(job.ActionsJson) || job.ActionsJson.Length > MaximumActionsJsonLength)
            throw new BuildPlanningException("動作流程內容遺失或超過上限，已拒絕執行舊的編譯腳本。");

        try
        {
            var steps = JsonSerializer.Deserialize<List<ActionStep>>(job.ActionsJson, SerializerOptions) ?? [];
            if (!ValidateSteps(steps, out var inputError)) throw new BuildPlanningException(inputError!);

            var definitions = await actions.MapAsync(cancellationToken);
            var renderer = await renderers.CreateAsync(cancellationToken: cancellationToken);
            var result = ActionScriptCompiler.Plan(steps, definitions, renderer);

            if (result.IsValid && result.Steps.Count != 0 &&
                result.Steps.Sum(step => (long)step.Command.Length) <= MaximumGeneratedScriptLength)
                return result.Steps;

            var reason = result.Errors.Count == 0
                ? result.Steps.Count == 0
                    ? "動作流程沒有任何可執行的步驟。"
                    : $"動作產生的命令不可超過 {MaximumGeneratedScriptLength:N0} 個字元。"
                : string.Join("；", result.Errors);
            logger.LogWarning(
                "任務 {Job} 的動作流程展不開（{Errors}），已拒絕執行舊的編譯腳本",
                job.Name,
                reason);
            throw new BuildPlanningException($"動作流程無法安全展開：{Truncate(reason)}");
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "任務 {Job} 的動作流程解析失敗，已拒絕執行舊的編譯腳本", job.Name);
            throw new BuildPlanningException("動作流程格式無效，已拒絕執行舊的編譯腳本。", exception);
        }
    }

    private static bool ValidateSteps(IReadOnlyList<ActionStep> steps, out string? error)
    {
        if (steps.Count is 0 or > MaximumSteps)
        {
            error = $"動作流程必須有 1 到 {MaximumSteps} 個步驟。";
            return false;
        }

        long characters = 0;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step is null || string.IsNullOrWhiteSpace(step.ActionId) ||
                (step.Id?.Length ?? 0) > 120 || step.ActionId.Length > 120 ||
                (step.Name?.Length ?? 0) > 200 ||
                (step.Key?.Length ?? 0) > StepExpressions.MaximumKeyLength ||
                (step.Condition?.Length ?? 0) > StepCondition.MaximumLength ||
                (step.DependsOn?.Count ?? 0) > MaximumSteps || (step.DependsOn?.Any(id => id is null || id.Length > 120) ?? false) ||
                step.Parameters is null || step.Parameters.Count > MaximumParametersPerStep)
            {
                error = $"第 {index + 1} 個動作的格式、識別碼或參數數量無效。";
                return false;
            }

            if ((step.RunWhen is not null && !StepRunCondition.IsValid(step.RunWhen)) ||
                step.Retries is < 0 or > BuildStepPlan.MaximumRetries)
            {
                error = $"第 {index + 1} 個動作的執行條件或重試次數無效。";
                return false;
            }

            characters += (step.Id?.Length ?? 0) + step.ActionId.Length + (step.Name?.Length ?? 0);
            foreach (var (name, value) in step.Parameters)
            {
                if (name.Length > 128 || (value?.Length ?? 0) > 4_096)
                {
                    error = $"第 {index + 1} 個動作的參數名稱或內容過長。";
                    return false;
                }

                characters += name.Length + (value?.Length ?? 0);
            }

            if (characters > MaximumActionsJsonLength)
            {
                error = "動作流程內容超過資源上限。";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static string Truncate(string value)
    {
        return value.Length <= 2_000 ? value : value[..2_000] + "…";
    }
}

/// <summary>The action flow cannot be re-validated at run time; the caller must mark the build failed instead of running the stale output.</summary>
public sealed class BuildPlanningException(string message, Exception? inner = null) : Exception(message, inner);