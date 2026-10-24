using System.Net;

namespace Walis.Api.Services;

/// <summary>
///     Decides when the first-administrator setup may skip the setup code because the request comes from this machine.
///     The shortcut exists so that opening http://localhost:11120 on the server itself just works; this class narrows it:
///     <list type="bullet">
///         <item><c>Security:RequireSetupCode=true</c> (the Production default) turns the shortcut off entirely, the code (or
///         BootstrapToken) is always needed.</item>
///         <item>
///             In Production, once the server listens on any non-loopback address, a loopback request is only accepted when it carries
///             no forwarded headers at all and its Host port is a port Walis actually listens on. A same-machine reverse proxy that
///             did not forward the client address, or rewrote Host to localhost, fails one of those.
///         </item>
///     </list>
/// </summary>
public sealed class BootstrapPolicy(IConfiguration configuration, IHostEnvironment environment)
{
    private static readonly string[] ForwardingHeaderPrefixes = ["X-Forwarded-", "X-Original-", "Forwarded"];

    private volatile Listening _listening = new(false, new HashSet<int>());

    /// <summary>
    ///     Whether the deployment demands the setup code even from this machine. Production defaults to yes: a same-machine
    ///     reverse proxy that rewrites Host to 127.0.0.1:port (nginx's default) and forwards nothing looks exactly like a local
    ///     browser, so the code-free shortcut there would let anyone on the network create the first administrator. The code is
    ///     printed to the console and written to setup-code.txt, so the installer still has it. <c>Security:RequireSetupCode=false</c>
    ///     restores the shortcut (under the strict rule below); Development and Test keep it unless the setting says true.
    /// </summary>
    public bool RequireSetupCode =>
        bool.TryParse(configuration["Security:RequireSetupCode"], out var required)
            ? required
            : environment.IsProduction();

    /// <summary>Whether the setup code is required only because Production defaults to it (for the startup message).</summary>
    public bool RequireSetupCodeByDefault =>
        !bool.TryParse(configuration["Security:RequireSetupCode"], out _) && environment.IsProduction();

    /// <summary>Whether any bound address is reachable from another machine (0.0.0.0, [::], a specific interface or a host name).</summary>
    public bool ListensOnNonLoopback => _listening.NonLoopback;

    public IReadOnlySet<int> ListeningPorts => _listening.Ports;

    /// <summary>Whether the local shortcut is under the strict rule (forwarded headers and Host port are checked).</summary>
    public bool IsStrict => environment.IsProduction() && ListensOnNonLoopback;

    /// <summary>Records the server's bound addresses once the host has started; before that nothing is known and nothing is strict.</summary>
    public void Observe(IEnumerable<string> addresses)
    {
        var ports = new HashSet<int>();
        var nonLoopback = false;

        foreach (var address in addresses)
        {
            if (!Uri.TryCreate(address.Replace("*", "0.0.0.0").Replace("+", "0.0.0.0"), UriKind.Absolute, out var uri))
                continue;

            if (uri.Port > 0) ports.Add(uri.Port);

            var host = uri.Host.Trim('[', ']');
            var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                           (IPAddress.TryParse(host, out var literal) && IPAddress.IsLoopback(literal));

            if (!loopback) nonLoopback = true;
        }

        _listening = new Listening(nonLoopback, ports);
    }

    /// <summary>
    ///     Whether a request already known to come from a loopback peer under a local host name may use the shortcut.
    ///     The caller has checked the peer address and Host name; this adds the deployment-level rules.
    /// </summary>
    public bool AllowsLocalShortcut(HttpRequest request)
    {
        if (RequireSetupCode) return false;

        if (!IsStrict) return true;

        // Any forwarding header means something sat between the client and Walis; X-Original-* is what the forwarded-headers
        // middleware leaves behind after consuming X-Forwarded-For/-Proto from a trusted proxy.
        foreach (var header in request.Headers.Keys)
            if (ForwardingHeaderPrefixes.Any(prefix => header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return false;

        var ports = _listening.Ports;
        if (ports.Count == 0) return true;

        var hostPort = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        return ports.Contains(hostPort);
    }

    private sealed record Listening(bool NonLoopback, IReadOnlySet<int> Ports);
}
