using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Walis.Api.Services;

namespace Walis.Tests;

public class BootstrapPolicyTests
{
    private const string Password = "Correct-Horse-Battery-Staple-123!";

    [Theory]
    [InlineData(false, "http://localhost:11120")]
    [InlineData(false, "http://127.0.0.1:11120", "http://[::1]:11120")]
    [InlineData(true, "http://0.0.0.0:11120")]
    [InlineData(true, "http://[::]:11120")]
    [InlineData(true, "http://*:5000")]
    [InlineData(true, "http://+:5000")]
    [InlineData(true, "http://127.0.0.1:11120", "https://walis.example.test:8443")]
    [InlineData(true, "http://10.0.0.5:11120")]
    public void RecognizesAddressesReachableFromOtherMachines(bool expected, params string[] addresses)
    {
        var policy = Policy(Environments.Production, false);

        policy.Observe(addresses);

        Assert.Equal(expected, policy.ListensOnNonLoopback);
        Assert.Equal(expected, policy.IsStrict);
    }

    [Fact]
    public void CollectsListeningPorts()
    {
        var policy = Policy(Environments.Production, false);

        policy.Observe(["http://0.0.0.0:11120", "https://0.0.0.0:11443", "garbage"]);

        Assert.Equal(new HashSet<int> { 11120, 11443 }, policy.ListeningPorts);
    }

    [Fact]
    public void RequireSetupCodeTurnsTheShortcutOffEverywhere()
    {
        var policy = Policy(Environments.Development, true);
        policy.Observe(["http://localhost:11120"]);

        Assert.True(policy.RequireSetupCode);
        Assert.False(policy.AllowsLocalShortcut(Request("localhost:11120")));
    }

    [Fact]
    public void StrictModeDemandsNoForwardingHeadersAndTheListeningPort()
    {
        var policy = Policy(Environments.Production, false);
        policy.Observe(["http://0.0.0.0:11120"]);

        Assert.True(policy.AllowsLocalShortcut(Request("localhost:11120")));
        Assert.True(policy.AllowsLocalShortcut(Request("127.0.0.1:11120")));

        // nginx's default $proxy_host rewrite keeps the port, but a proxy that forwarded anything at all reveals itself.
        Assert.False(policy.AllowsLocalShortcut(Request("localhost:11120", ("X-Forwarded-Host", "walis.example.test"))));
        Assert.False(policy.AllowsLocalShortcut(Request("localhost:11120", ("X-Forwarded-Proto", "https"))));
        Assert.False(policy.AllowsLocalShortcut(Request("localhost:11120", ("X-Original-For", "203.0.113.7"))));
        Assert.False(policy.AllowsLocalShortcut(Request("localhost:11120", ("Forwarded", "for=203.0.113.7"))));

        // A Host port Walis does not listen on came from something in between (a proxy on 80/443 or another port).
        Assert.False(policy.AllowsLocalShortcut(Request("localhost")));
        Assert.False(policy.AllowsLocalShortcut(Request("localhost:8080")));
    }

    [Fact]
    public void LoopbackOnlyListeningAndNonProductionStayLenient()
    {
        var loopbackOnly = Policy(Environments.Production, false);
        loopbackOnly.Observe(["http://127.0.0.1:11120"]);
        Assert.False(loopbackOnly.IsStrict);
        Assert.True(loopbackOnly.AllowsLocalShortcut(Request("localhost:9999", ("X-Forwarded-Host", "x"))));

        var development = Policy(Environments.Development, false);
        development.Observe(["http://0.0.0.0:11126"]);
        Assert.False(development.IsStrict);
        Assert.True(development.AllowsLocalShortcut(Request("127.0.0.1:11124")));
    }

    [Fact]
    public async Task RequireSetupCodeMakesEvenThisMachineEnterTheCode()
    {
        await using var factory = new WalisApiFactory { RequireSetupCode = true };
        var local = factory.CreateAnonymousClient();
        var account = new { username = "local-admin", displayName = "Local", password = Password };

        Assert.True((await local.GetFromJsonAsync<JsonElement>("/api/auth/status"))
            .GetProperty("setupCodeRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await local.PostAsJsonAsync("/api/auth/setup", account)).StatusCode);

        var code = Assert.IsType<string>(factory.Services.GetRequiredService<SetupCode>().Current);
        var withCode = new HttpRequestMessage(HttpMethod.Post, "/api/auth/setup") { Content = JsonContent.Create(account) };
        withCode.Headers.Add("X-Walis-Bootstrap-Token", code);
        Assert.Equal(HttpStatusCode.OK, (await local.SendAsync(withCode)).StatusCode);
    }

    [Fact]
    public async Task ProductionServerReachableFromOutsideOnlyTrustsAPlainLocalRequestOnItsOwnPort()
    {
        await using var factory = new WalisApiFactory { HostEnvironment = Environments.Production, RequireSetupCode = false };
        factory.Services.GetRequiredService<BootstrapPolicy>().Observe(["http://0.0.0.0:11120"]);
        var account = new { username = "local-admin", displayName = "Local", password = Password };

        // Loopback peer, Host localhost: the TBD-1 shape of a same-machine proxy that rewrote Host but still forwarded something.
        using var proxied = factory.CreateAnonymousClient();
        proxied.DefaultRequestHeaders.Host = "localhost:11120";
        proxied.DefaultRequestHeaders.Add("X-Forwarded-Host", "walis.example.test");
        Assert.True((await proxied.GetFromJsonAsync<JsonElement>("/api/auth/status"))
            .GetProperty("setupCodeRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await proxied.PostAsJsonAsync("/api/auth/setup", account)).StatusCode);

        // A proxy listening on another port (80 here: no port in Host) is not the browser on this machine either.
        using var otherPort = factory.CreateAnonymousClient();
        otherPort.DefaultRequestHeaders.Host = "localhost";
        Assert.Equal(HttpStatusCode.Forbidden, (await otherPort.PostAsJsonAsync("/api/auth/setup", account)).StatusCode);

        // The plain browser flow on the server itself keeps working.
        using var browser = factory.CreateAnonymousClient();
        browser.DefaultRequestHeaders.Host = "localhost:11120";
        Assert.False((await browser.GetFromJsonAsync<JsonElement>("/api/auth/status"))
            .GetProperty("setupCodeRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsJsonAsync("/api/auth/setup", account)).StatusCode);
    }

    [Fact]
    public async Task ProductionRequiresTheSetupCodeByDefaultEvenBehindADefaultNginx()
    {
        await using var factory = new WalisApiFactory { HostEnvironment = Environments.Production };
        factory.Services.GetRequiredService<BootstrapPolicy>().Observe(["http://127.0.0.1:11120"]);

        // nginx with only proxy_pass http://127.0.0.1:11120: loopback peer, Host rewritten to the upstream, nothing forwarded.
        using var proxied = factory.CreateAnonymousClient();
        proxied.DefaultRequestHeaders.Host = "127.0.0.1:11120";

        Assert.True((await proxied.GetFromJsonAsync<JsonElement>("/api/auth/status"))
            .GetProperty("setupCodeRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await proxied.PostAsJsonAsync("/api/auth/setup",
            new { username = "remote-admin", displayName = "Remote", password = Password })).StatusCode);
    }

    [Fact]
    public async Task ProductionServerOnLoopbackOnlyKeepsTheOldLocalRule()
    {
        await using var factory = new WalisApiFactory { HostEnvironment = Environments.Production, RequireSetupCode = false };
        factory.Services.GetRequiredService<BootstrapPolicy>().Observe(["http://127.0.0.1:11120"]);

        using var local = factory.CreateAnonymousClient();
        local.DefaultRequestHeaders.Host = "localhost:9999";

        Assert.Equal(HttpStatusCode.OK, (await local.PostAsJsonAsync("/api/auth/setup",
            new { username = "local-admin", displayName = "Local", password = Password })).StatusCode);
    }

    private static BootstrapPolicy Policy(string environment, bool requireSetupCode)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:RequireSetupCode"] = requireSetupCode ? "true" : "false"
            })
            .Build();

        return new BootstrapPolicy(configuration, new HostingEnvironment { EnvironmentName = environment });
    }

    private static HttpRequest Request(string host, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = HostString.FromUriComponent(host);

        foreach (var (name, value) in headers) context.Request.Headers[name] = value;

        return context.Request;
    }
}
