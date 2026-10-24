using System.Net;
using System.Net.Sockets;

namespace Walis.Core.Security;

/// <summary>
///     A single IPv4/IPv6 address or a CIDR range, used for source-address allowlists (MCP, API keys). An IPv4 address
///     that arrives as an IPv4-mapped IPv6 address (::ffff:203.0.113.9) is compared as IPv4, so a list written in IPv4
///     notation matches the same peer no matter how Kestrel reports it.
/// </summary>
public sealed class IpAddressRange
{
    public const int MaximumEntries = 64;

    private readonly byte[] _network;
    private readonly int _prefixLength;

    private IpAddressRange(IPAddress network, int prefixLength)
    {
        _network = network.GetAddressBytes();
        _prefixLength = prefixLength;
        Family = network.AddressFamily;
        Text = prefixLength == _network.Length * 8 ? network.ToString() : $"{network}/{prefixLength}";
    }

    public AddressFamily Family { get; }

    /// <summary>The normalized notation (host bits cleared, IPv4-mapped addresses unwrapped).</summary>
    public string Text { get; }

    public static bool TryParse(string? value, out IpAddressRange? range)
    {
        range = null;

        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (text.Length > 64 || text.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            return false;

        var slash = text.IndexOf('/');
        var addressText = slash < 0 ? text : text[..slash];
        int? requestedPrefix = null;

        if (slash >= 0)
        {
            var prefixText = text[(slash + 1)..];

            if (prefixText.Length is 0 or > 3 || !prefixText.All(char.IsAsciiDigit) ||
                !int.TryParse(prefixText, out var parsedPrefix))
                return false;

            requestedPrefix = parsedPrefix;
        }

        // Only plain literals: no zone ids (fe80::1%en0), no ports, no brackets.
        if (addressText.Length == 0 || addressText.Contains('%') || addressText.Contains('[') ||
            !IPAddress.TryParse(addressText, out var address))
            return false;

        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return false;

        // "1" or "10.1" parse as IPv4 shorthand; require the dotted-quad form the administrator meant to write.
        if (address.AddressFamily == AddressFamily.InterNetwork && addressText.Count(character => character == '.') != 3)
            return false;

        if (address.IsIPv4MappedToIPv6)
        {
            // ::ffff:10.0.0.0/104 means the IPv4 /8; a prefix shorter than 96 would cover unrelated IPv6 space.
            if (requestedPrefix is { } mappedPrefix && mappedPrefix < 96) return false;

            address = address.MapToIPv4();
            if (requestedPrefix is not null) requestedPrefix -= 96;
        }

        var bits = address.GetAddressBytes().Length * 8;
        var prefix = requestedPrefix ?? bits;
        if (prefix < 0 || prefix > bits) return false;

        range = new IpAddressRange(ClearHostBits(address, prefix), prefix);
        return true;
    }

    public bool Contains(IPAddress? address)
    {
        if (address is null) return false;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily != Family) return false;

        var bytes = address.GetAddressBytes();
        var fullBytes = _prefixLength / 8;

        for (var index = 0; index < fullBytes; index++)
            if (bytes[index] != _network[index])
                return false;

        var remaining = _prefixLength % 8;
        if (remaining == 0) return true;

        var mask = (byte)(0xff << (8 - remaining));
        return (bytes[fullBytes] & mask) == (_network[fullBytes] & mask);
    }

    /// <summary>
    ///     Parses an allowlist written one entry per line (commas and semicolons also separate). Returns false, and the
    ///     offending entry, when any line is not an address or CIDR range or when the list is too long.
    /// </summary>
    public static bool TryParseList(string? configured, out IReadOnlyList<IpAddressRange> ranges, out string? invalidEntry)
    {
        invalidEntry = null;
        var entries = Split(configured);
        var parsed = new List<IpAddressRange>(entries.Length);

        if (entries.Length > MaximumEntries)
        {
            invalidEntry = entries[MaximumEntries];
            ranges = [];
            return false;
        }

        foreach (var entry in entries)
        {
            if (!TryParse(entry, out var range))
            {
                invalidEntry = entry;
                ranges = [];
                return false;
            }

            parsed.Add(range!);
        }

        ranges = parsed;
        return true;
    }

    public static bool AnyContains(IReadOnlyList<IpAddressRange> ranges, IPAddress? address)
    {
        foreach (var range in ranges)
            if (range.Contains(address))
                return true;

        return false;
    }

    public static string[] Split(string? configured)
    {
        return (configured ?? string.Empty).Split(
            ['\n', '\r', ',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public override string ToString()
    {
        return Text;
    }

    private static IPAddress ClearHostBits(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();

        for (var bit = prefix; bit < bytes.Length * 8; bit++)
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));

        return new IPAddress(bytes);
    }
}
