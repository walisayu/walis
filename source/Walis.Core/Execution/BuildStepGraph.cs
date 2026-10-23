using Walis.Core.Models;

namespace Walis.Core.Execution;

/// <summary>
///     The dependency graph between steps. Builds without explicit dependencies (old data, script mode, old runners) derive it from order and the "parallel" flag:
///     each step depends on every step of the previous group, and consecutive steps marked parallel form one group. With explicit dependencies the graph is followed as given;
///     steps without dependencies are entry points, and whatever can run concurrently does (capped by <see cref="MaximumConcurrentSteps" />).
/// </summary>
public static class BuildStepGraph
{
    public const int MaximumConcurrentSteps = 8;

    /// <summary>Derives dependencies from order and parallel flags: returns, for each position (0-based), the step numbers (1-based) it depends on.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> DeriveLinear(IReadOnlyList<bool> parallelFlags)
    {
        var result = new List<IReadOnlyList<int>>(parallelFlags.Count);
        var previousGroup = new List<int>();
        var currentGroup = new List<int>();

        for (var index = 0; index < parallelFlags.Count; index++)
        {
            if (index > 0 && !parallelFlags[index])
            {
                previousGroup = currentGroup;
                currentGroup = [];
            }

            result.Add([.. previousGroup]);
            currentGroup.Add(index + 1);
        }

        return result;
    }

    /// <summary>
    ///     Gets the dependencies of the build steps (the step numbers each position depends on). Derives from order when none are set;
    ///     otherwise checks the number range, self-references and cycles.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Resolve(IReadOnlyList<BuildStep> steps, out string? error)
    {
        error = null;

        if (steps.All(step => step.DependsOn.Count == 0))
            return DeriveLinear([.. steps.Select(step => step.Parallel)]);

        var byNumber = new HashSet<int>(steps.Select(step => step.Number));
        var result = new List<IReadOnlyList<int>>(steps.Count);

        foreach (var step in steps)
        {
            var dependencies = step.DependsOn.Distinct().ToList();
            if (dependencies.Any(number => number == step.Number || !byNumber.Contains(number)))
            {
                error = $"步驟 {step.Number} 的依賴指向不存在的步驟或自己。";
                return result;
            }

            result.Add(dependencies);
        }

        if (!IsAcyclic(steps.Select(step => step.Number).ToList(), result))
            error = "步驟的依賴有循環，沒有辦法決定執行順序。";

        return result;
    }

    /// <summary>numbers[i] is the number of node i; dependencies[i] holds the numbers it depends on.</summary>
    public static bool IsAcyclic(IReadOnlyList<int> numbers, IReadOnlyList<IReadOnlyList<int>> dependencies)
    {
        return TopologicalOrder(numbers, dependencies) is not null;
    }

    /// <summary>Topological sort (stable: picks the smallest number when there is a choice); returns null on a cycle.</summary>
    public static IReadOnlyList<int>? TopologicalOrder(IReadOnlyList<int> numbers, IReadOnlyList<IReadOnlyList<int>> dependencies)
    {
        var index = numbers.Select((number, position) => (number, position)).ToDictionary(pair => pair.number, pair => pair.position);
        var remaining = new Dictionary<int, HashSet<int>>();
        foreach (var (number, position) in index)
            remaining[number] = [.. dependencies[position].Where(index.ContainsKey)];

        var order = new List<int>(numbers.Count);
        while (remaining.Count != 0)
        {
            var ready = remaining.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).OrderBy(number => number).ToList();
            if (ready.Count == 0) return null;

            foreach (var number in ready)
            {
                order.Add(number);
                remaining.Remove(number);
            }

            foreach (var set in remaining.Values) set.ExceptWith(ready);
        }

        return order;
    }

    /// <summary>All predecessors of each position (transitive closure), used to decide which step outputs may be referenced.</summary>
    public static IReadOnlyList<IReadOnlySet<int>> Ancestors(IReadOnlyList<int> numbers, IReadOnlyList<IReadOnlyList<int>> dependencies)
    {
        var position = numbers.Select((number, at) => (number, at)).ToDictionary(pair => pair.number, pair => pair.at);
        var memo = new Dictionary<int, HashSet<int>>();

        HashSet<int> Visit(int number, HashSet<int> path)
        {
            if (memo.TryGetValue(number, out var cached)) return cached;
            var set = new HashSet<int>();
            if (!path.Add(number)) return set; // Cycle: reported elsewhere; here we only avoid infinite recursion
            foreach (var dependency in dependencies[position[number]])
            {
                if (!position.ContainsKey(dependency)) continue;
                set.Add(dependency);
                set.UnionWith(Visit(dependency, path));
            }

            path.Remove(number);
            memo[number] = set;
            return set;
        }

        return [.. numbers.Select(number => (IReadOnlySet<int>)Visit(number, []))];
    }
}
