using Walis.Core.Database;

namespace Walis.Api.Services;

/// <summary>
///     The identity of this API node. On a single-node deployment it is just the machine name and does little;
///     when several nodes share one database it decides "which builds are mine" and "who runs this scheduling round".
/// </summary>
public sealed class NodeIdentity
{
    public NodeIdentity(string name, DatabaseProvider provider)
    {
        Name = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
        Provider = provider;
    }

    public string Name { get; }

    public DatabaseProvider Provider { get; }

    /// <summary>SQLite is a single file and inherently one node; only the other databases need lease and claim contention.</summary>
    public bool IsClustered => DatabaseProviders.SupportsMultipleNodes(Provider);

    public override string ToString()
    {
        return Name;
    }
}