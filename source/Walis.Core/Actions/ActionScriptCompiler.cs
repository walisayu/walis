using System.Text;
using Walis.Core.Models;

namespace Walis.Core.Actions;

/// <summary>One step in the flow.</summary>
public sealed class ActionStep
{
    public string Id { get; init; } = string.Empty;
    public string ActionId { get; init; } = string.Empty;

    /// <summary>Display name; falls back to the action's own name when empty.</summary>
    public string? Name { get; init; }

    /// <summary>
    ///     Key (lowercase alphanumerics and underscores); later steps reference this step's outputs with <c>${{ steps.&lt;key&gt;.&lt;output&gt; }}</c>.
    ///     Outputs of a step without a key only show in the build record and cannot be referenced.
    /// </summary>
    public string? Key { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Whether to keep going after a failure.</summary>
    public bool ContinueOnError { get; init; }

    /// <summary>Runs together with adjacent steps also marked parallel; ignored when the flow has explicit dependencies (<see cref="DependsOn" />).</summary>
    public bool Parallel { get; init; }

    /// <summary>
    ///     The steps this one depends on (step ids). When none in the flow is set, derived from order and <see cref="Parallel" /> (old data);
    ///     once any step sets it the graph is authoritative and steps without dependencies are entry points.
    /// </summary>
    public IReadOnlyList<string>? DependsOn { get; init; }

    /// <summary>Run condition: success (default), always or failure.</summary>
    public string? RunWhen { get; init; }

    /// <summary>How many times to retry after failure, 0 to 10.</summary>
    public int Retries { get; init; }

    /// <summary>A condition evaluated at run time (<see cref="StepCondition" />), e.g. <c>steps.check.changed == 'true'</c>; the step is skipped when false.</summary>
    public string? Condition { get; init; }

    public Dictionary<string, string?> Parameters { get; init; } = [];
}

public sealed record ActionCompileResult(string Script, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record ActionPlanResult(IReadOnlyList<BuildStepPlan> Steps, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
///     Compiles an action flow into a shell script. The result is stored back into the job's command field,
///     so neither the executor nor the remote agent needs to know actions exist.
/// </summary>
public static class ActionScriptCompiler
{
    public const string GeneratedHeader = "# 這段腳本由 Walis 的動作流程產生，請在流程編輯器調整。";

    public static ActionCompileResult Compile(
        IReadOnlyList<ActionStep> steps,
        IReadOnlyDictionary<string, ActionDefinition> definitions,
        IActionRenderer? renderer = null)
    {
        renderer ??= TemplateActionRenderer.Instance;

        var errors = new List<string>();
        var builder = new StringBuilder()
            .AppendLine(GeneratedHeader)
            .AppendLine("set -e")
            .AppendLine();

        var graph = ResolveGraph(steps, errors);

        // A single script can only be sequential: order topologically (preferring the earlier original position when there is a choice), matching the result of real parallel execution.
        var sequence = graph.Order ?? [.. Enumerable.Range(0, steps.Count)];

        foreach (var index in sequence)
        {
            var step = steps[index];
            var order = index + 1;

            if (!definitions.TryGetValue(step.ActionId, out var definition))
            {
                errors.Add($"第 {order} 步使用了不存在的動作「{step.ActionId}」。");
                continue;
            }

            if (!step.Enabled) continue;

            var values = Resolve(step, definition);

            foreach (var parameter in definition.Parameters.Where(parameter => parameter.Required))
                if (!ActionTemplate.IsTruthy(values.GetValueOrDefault(parameter.Name)))
                    errors.Add($"第 {order} 步「{step.Name ?? definition.Name}」缺少必填欄位：{parameter.Label}。");

            var rendered = Render(renderer, definition, values, order, errors);

            if (rendered.Length == 0) continue;

            builder.AppendLine($"# {order}. {step.Name ?? definition.Name}");

            if (graph.Explicit && graph.Dependencies[index].Count != 0)
                builder.AppendLine($"# 依賴：{string.Join("、", graph.Dependencies[index].Select(position => steps[position].Name ?? definitions.GetValueOrDefault(steps[position].ActionId)?.Name ?? steps[position].ActionId))}");

            // A single script cannot express "run only after failure"; only a note is left here, and real execution handles it per step.
            if (StepRunCondition.Normalize(step.RunWhen) != StepRunCondition.Success)
                builder.AppendLine($"# 執行條件：{StepRunCondition.Normalize(step.RunWhen)}");

            if (step.Retries > 0)
                builder.AppendLine($"# 失敗後重試 {Math.Clamp(step.Retries, 0, BuildStepPlan.MaximumRetries)} 次");

            if (!string.IsNullOrWhiteSpace(step.Condition))
                builder.AppendLine($"# 條件：{step.Condition.Trim()}");

            if (step.ContinueOnError)
                // Wrap in a subshell so a failing multi-line action only affects this step.
                builder.AppendLine("(").AppendLine(Indent(rendered)).AppendLine(") || true");
            else
                builder.AppendLine(rendered);

            builder.AppendLine();
        }

        return new ActionCompileResult(builder.ToString().TrimEnd() + Environment.NewLine, errors);
    }

    /// <summary>
    ///     Expands an action flow into build steps: one action per step, each with its own name and script.
    ///     Shares the parameter resolution and template rendering with <see cref="Compile" />; the only difference is granularity.
    ///     The former produces a single human-readable, compatibility script, the latter the units that actually run.
    /// </summary>
    public static ActionPlanResult Plan(
        IReadOnlyList<ActionStep> steps,
        IReadOnlyDictionary<string, ActionDefinition> definitions,
        IActionRenderer? renderer = null)
    {
        renderer ??= TemplateActionRenderer.Instance;

        var errors = new List<string>();
        var plans = new List<BuildStepPlan>();
        var graph = ResolveGraph(steps, errors);

        // Disabled steps are not in the plan, so numbers are recomputed; dependencies on a disabled step become dependencies on its predecessors (transitively).
        var planNumbers = new Dictionary<int, int>();
        var order = 0;

        foreach (var step in steps)
        {
            order++;

            if (!definitions.TryGetValue(step.ActionId, out var definition))
            {
                errors.Add($"第 {order} 步使用了不存在的動作「{step.ActionId}」。");
                continue;
            }

            if (!step.Enabled) continue;

            var values = Resolve(step, definition);

            foreach (var parameter in definition.Parameters.Where(parameter => parameter.Required))
                if (!ActionTemplate.IsTruthy(values.GetValueOrDefault(parameter.Name)))
                    errors.Add($"第 {order} 步「{step.Name ?? definition.Name}」缺少必填欄位：{parameter.Label}。");

            var rendered = Render(renderer, definition, values, order, errors);

            if (rendered.Length == 0) continue;

            var dependencies = graph.Explicit
                ? EffectiveDependencies(order - 1, steps, graph.Dependencies)
                    .Select(position => planNumbers.GetValueOrDefault(position))
                    .Where(number => number != 0)
                    .Distinct()
                    .OrderBy(number => number)
                    .ToList()
                : [];

            plans.Add(new BuildStepPlan(
                step.Name ?? definition.Name,
                rendered,
                step.ContinueOnError,
                step.Parallel,
                StepRunCondition.Normalize(step.RunWhen),
                Math.Clamp(step.Retries, 0, BuildStepPlan.MaximumRetries),
                string.IsNullOrWhiteSpace(step.Key) ? null : step.Key.Trim(),
                dependencies,
                string.IsNullOrWhiteSpace(step.Condition) ? null : step.Condition.Trim()));
            planNumbers[order - 1] = plans.Count;
        }

        return new ActionPlanResult(plans, errors);
    }

    /// <summary>The enabled step positions a step really depends on: disabled dependencies are followed upstream until an enabled one is found.</summary>
    private static IEnumerable<int> EffectiveDependencies(int position, IReadOnlyList<ActionStep> steps,
        IReadOnlyList<IReadOnlyList<int>> dependencies)
    {
        var visited = new HashSet<int>();
        var stack = new Stack<int>(dependencies[position]);

        while (stack.Count != 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current)) continue;

            if (steps[current].Enabled) yield return current;
            else foreach (var upstream in dependencies[current]) stack.Push(upstream);
        }
    }

    public sealed record StepGraph(
        bool Explicit,
        IReadOnlyList<IReadOnlyList<int>> Dependencies,
        IReadOnlyList<int>? Order,
        IReadOnlyList<IReadOnlySet<int>> Ancestors);

    /// <summary>
    ///     Resolves the flow's dependency graph (0-based positions). When no DependsOn is set, derives it from order and parallel flags;
    ///     otherwise checks that referenced steps exist and there is no cycle, and runs the key-reference rules.
    /// </summary>
    public static StepGraph ResolveGraph(IReadOnlyList<ActionStep> steps, List<string> errors)
    {
        var isExplicit = steps.Any(step => step.DependsOn is not null);
        var numbers = Enumerable.Range(0, steps.Count).ToList();
        IReadOnlyList<IReadOnlyList<int>> dependencies;

        if (!isExplicit)
        {
            dependencies = Execution.BuildStepGraph.DeriveLinear([.. steps.Select(step => step.Parallel)])
                .Select(list => (IReadOnlyList<int>)list.Select(number => number - 1).ToList())
                .ToList();
        }
        else
        {
            var byId = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < steps.Count; index++)
                if (!string.IsNullOrEmpty(steps[index].Id)) byId.TryAdd(steps[index].Id, index);

            var resolved = new List<IReadOnlyList<int>>(steps.Count);
            for (var index = 0; index < steps.Count; index++)
            {
                var list = new List<int>();
                foreach (var id in steps[index].DependsOn ?? [])
                {
                    if (!byId.TryGetValue(id, out var target))
                    {
                        errors.Add($"第 {index + 1} 步依賴的步驟不存在。");
                        continue;
                    }

                    if (target == index)
                    {
                        errors.Add($"第 {index + 1} 步不能依賴自己。");
                        continue;
                    }

                    if (!list.Contains(target)) list.Add(target);
                }

                resolved.Add(list);
            }

            dependencies = resolved;
        }

        var order = Execution.BuildStepGraph.TopologicalOrder(numbers, dependencies);
        if (order is null) errors.Add("步驟的依賴有循環，請拿掉其中一條。");

        var ancestors = Execution.BuildStepGraph.Ancestors(numbers, dependencies);
        ValidateReferences(steps, ancestors, errors);

        return new StepGraph(isExplicit, dependencies, order, ancestors);
    }

    /// <summary>
    ///     Keys must be valid and unique; references may only point at this step's predecessors (upstream in the dependency chain, transitively).
    ///     Steps parallel to it or after it have outputs that are not injected yet or never will be.
    /// </summary>
    public static void ValidateReferences(IReadOnlyList<ActionStep> steps, IReadOnlyList<IReadOnlySet<int>> ancestors, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keyAt = new Dictionary<int, string>();

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (!step.Enabled) continue;

            var key = step.Key?.Trim();
            if (string.IsNullOrEmpty(key)) continue;

            if (!StepExpressions.IsValidKey(key))
                errors.Add($"第 {index + 1} 步的識別字「{key}」不合法：小寫字母開頭，只能用小寫英數與底線，最多 {StepExpressions.MaximumKeyLength} 個字元。");
            else if (!seen.Add(key))
                errors.Add($"第 {index + 1} 步的識別字「{key}」與其他步驟重複。");
            else
                keyAt[index] = key;
        }

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (!step.Enabled) continue;

            var available = ancestors[index].Where(keyAt.ContainsKey).Select(position => keyAt[position]).ToHashSet(StringComparer.Ordinal);

            foreach (var value in step.Parameters.Values)
            foreach (var (referenced, name) in StepExpressions.References(value))
                if (!available.Contains(referenced))
                    errors.Add($"第 {index + 1} 步引用了 steps.{referenced}.{name}，但它的前置步驟裡沒有識別字為「{referenced}」的步驟（平行或後面的步驟不能引用）。");

            if (string.IsNullOrWhiteSpace(step.Condition)) continue;

            var condition = StepCondition.Parse(step.Condition);
            if (!condition.IsValid)
            {
                errors.Add($"第 {index + 1} 步的條件有錯：{condition.Error}");
                continue;
            }

            foreach (var (referenced, name) in StepCondition.StepReferences(condition.Root!))
                if (!available.Contains(referenced))
                    errors.Add($"第 {index + 1} 步的條件引用了 steps.{referenced}.{name}，但它的前置步驟裡沒有識別字為「{referenced}」的步驟。");
        }
    }

    /// <summary>Records a render failure (e.g. a plugin throwing) as a compile error instead of letting it abort the whole flow.</summary>
    private static string Render(
        IActionRenderer renderer,
        ActionDefinition definition,
        Dictionary<string, string?> values,
        int order,
        List<string> errors)
    {
        try
        {
            return renderer.Render(definition, values).Trim();
        }
        catch (Exception exception)
        {
            errors.Add($"第 {order} 步「{definition.Name}」產生腳本失敗：{exception.Message}");
            return string.Empty;
        }
    }

    /// <summary>Applies defaults to unset parameters and normalizes booleans to true or the empty string.</summary>
    public static Dictionary<string, string?> Resolve(ActionStep step, ActionDefinition definition)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var parameter in definition.Parameters)
        {
            var provided = step.Parameters.GetValueOrDefault(parameter.Name);
            var value = string.IsNullOrWhiteSpace(provided) ? parameter.Default : provided;

            values[parameter.Name] = parameter.Type == ActionParameterType.Boolean
                ? ActionTemplate.IsTruthy(value) ? "true" : string.Empty
                : value;
        }

        return values;
    }

    private static string Indent(string value)
    {
        return string.Join(Environment.NewLine, value.Split('\n').Select(line => "  " + line.TrimEnd('\r')));
    }
}