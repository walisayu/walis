using System.Net;
using System.Net.Sockets;

namespace Walis.Api.Services;

/// <summary>
///     Shared hardening for HTTP clients that talk to administrator-configured or provider-controlled
///     endpoints. In particular, a pooled handler must not carry cookies learned from one request into
///     another channel/plugin/provider request.
/// </summary>
public static class OutboundHttpHandlers
{
    public static readonly HttpRequestOptionsKey<bool> AllowPrivateNetwork =
        new("Walis.AllowPrivatePluginNetwork");

    public static SocketsHttpHandler Create(int maximumConnectionsPerServer)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = maximumConnectionsPerServer,
            MaxResponseHeadersLength = 32,
            MaxResponseDrainSize = 64 * 1024,
            ResponseDrainTimeout = TimeSpan.FromSeconds(2)
        };
    }

    public static SocketsHttpHandler CreatePlugin(int maximumConnectionsPerServer)
    {
        var handler = Create(maximumConnectionsPerServer);

        // A proxy could resolve a public allowlisted hostname to an internal destination on the
        // plugin's behalf, bypassing the address check below.
        handler.UseProxy = false;
        handler.ConnectCallback = ConnectPluginAsync;
        return handler;
    }

    /// <summary>
    ///     The handler for administrator-configured destinations (notification webhooks, AI base URLs, backup endpoints).
    ///     Unless the deployment sets <c>Security:OutboundAllowPrivateNetwork=true</c>, every destination is resolved and
    ///     only public addresses are connected: a literal private IP or <c>localhost</c> in the settings is refused as well,
    ///     so a leaked admin credential cannot turn the server into a probe for the internal network or cloud metadata.
    /// </summary>
    public static SocketsHttpHandler CreateGuarded(int maximumConnectionsPerServer, bool allowPrivateNetwork)
    {
        var handler = Create(maximumConnectionsPerServer);

        if (allowPrivateNetwork) return handler;

        handler.UseProxy = false;
        handler.ConnectCallback = ConnectPublicOnlyAsync;
        return handler;
    }

    /// <summary>Reads the deployment switch; absent or unparseable means the private network stays blocked.</summary>
    public static bool AllowsPrivateNetwork(IConfiguration configuration)
    {
        return bool.TryParse(configuration["Security:OutboundAllowPrivateNetwork"], out var allowed) && allowed;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return false;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 0 && bytes[2] is 0 or 2 => false,
                192 when bytes[1] == 88 && bytes[2] == 99 => false,
                192 when bytes[1] == 168 => false,
                198 when bytes[1] is 18 or 19 => false,
                198 when bytes[1] == 51 && bytes[2] == 100 => false,
                203 when bytes[1] == 0 && bytes[2] == 113 => false,
                >= 224 => false,
                _ => true
            };

        if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
            address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None) ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
            (bytes[0] & 0xfe) == 0xfc)
            return false;

        // Documentation, discard-only, ORCHID and tunnel/translation ranges must not become a
        // route around the IPv4 private-address checks.
        if ((bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) ||
            (bytes[0] == 0x01 && bytes.Skip(1).Take(7).All(value => value == 0)) ||
            (bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] == 0 || (bytes[2] & 0xf0) == 0x20)) ||
            (bytes[0] == 0x20 && bytes[1] == 0x02) ||
            (bytes[0] == 0x64 && bytes[1] == 0xff && bytes[2] == 0x9b && bytes[3] == 0x01))
            return false;

        // Well-known NAT64 prefix: apply the same policy to the embedded IPv4 address.
        if (bytes.AsSpan(0, 12).SequenceEqual(
                new byte[] { 0, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0 }))
            return IsPublicAddress(new IPAddress(bytes.AsSpan(12, 4)));

        return true;
    }

    private static ValueTask<Stream> ConnectPluginAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var allowPrivate = context.InitialRequestMessage.Options.TryGetValue(
            AllowPrivateNetwork,
            out var explicitlyAllowed) && explicitlyAllowed;

        return ConnectFilteredAsync(
            context,
            allowPrivate,
            host => $"外掛目的地主機 {host} 沒有允許的公開 IP 位址。",
            cancellationToken);
    }

    private static ValueTask<Stream> ConnectPublicOnlyAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        return ConnectFilteredAsync(
            context,
            false,
            host => $"不允許連線到私有網段：{host}",
            cancellationToken);
    }

    /// <summary>Resolves at connect time and skips non-public addresses, so a DNS answer cannot be swapped between check and use.</summary>
    private static async ValueTask<Stream> ConnectFilteredAsync(
        SocketsHttpConnectionContext context,
        bool allowPrivate,
        Func<string, string> refusedMessage,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        Exception? lastError = null;

        foreach (var address in addresses.Where(address => allowPrivate || IsPublicAddress(address)))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = exception;

                if (exception is OperationCanceledException) throw;
            }
        }

        throw new HttpRequestException(refusedMessage(context.DnsEndPoint.Host), lastError);
    }
}