using System.Net;
using System.Net.Sockets;

namespace Walis.Api.Services;

/// <summary>
///     The key that per-source limits (the global rate limiter, the webhook limiter, the sign-in throttle's address bucket) are
///     counted under. An IPv4 address is its own key; an IPv6 address is reduced to its /64, because a single subscriber gets a
///     whole /64 and could otherwise start every request from a fresh address and never meet a per-address limit. IPv4-mapped
///     IPv6 is treated as the IPv4 address it carries. Audit rows keep the full address; only the counting is coarser.
/// </summary>
public static class ClientAddressKey
{
    public const int Ipv6PrefixLength = 64;

    public static string For(IPAddress? address)
    {
        if (address is null) return "unknown";

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, Ipv6PrefixLength / 8, bytes.Length - Ipv6PrefixLength / 8);
        return $"{new IPAddress(bytes)}/{Ipv6PrefixLength}";
    }

    /// <summary>For callers that only have the textual address; anything unparseable is used as is.</summary>
    public static string For(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "unknown";

        return IPAddress.TryParse(address.Trim(), out var parsed) ? For(parsed) : address.Trim();
    }
}
