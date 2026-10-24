using System.Net;
using Walis.Core.Security;

namespace Walis.Api.Services;

/// <summary>
///     The source-address allowlist for /mcp, read from the system settings. Like <see cref="CorsOriginStore" /> it is a
///     per-node snapshot: refreshed at startup, when this node saves the settings, after a restore, and by the scheduler
///     every minute so other nodes catch up. Empty means unrestricted; a configured list is exact, loopback is not implied.
/// </summary>
public sealed class McpAccessStore
{
    private volatile Snapshot _current = new(false, []);

    public bool IsRestricted => _current.Restricted;

    public IReadOnlyList<IpAddressRange> Ranges => _current.Ranges;

    public void Update(string? configured)
    {
        var text = configured ?? string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            _current = new Snapshot(false, []);
            return;
        }

        // Settings PUT validates this too, but restore writes the Settings row directly. An entry that fails to parse
        // (an old or crafted archive) is dropped, never turned into "allow everyone": the list stays fail-closed.
        var ranges = IpAddressRange.Split(text)
            .Select(entry => IpAddressRange.TryParse(entry, out var range) ? range : null)
            .OfType<IpAddressRange>()
            .Take(IpAddressRange.MaximumEntries)
            .ToArray();

        _current = new Snapshot(true, ranges);
    }

    /// <summary>Whether a peer may reach /mcp. With no list configured every address passes; with one, only listed addresses do.</summary>
    public bool IsAllowed(IPAddress? address)
    {
        var snapshot = _current;
        return !snapshot.Restricted || IpAddressRange.AnyContains(snapshot.Ranges, address);
    }

    private sealed record Snapshot(bool Restricted, IReadOnlyList<IpAddressRange> Ranges);
}
