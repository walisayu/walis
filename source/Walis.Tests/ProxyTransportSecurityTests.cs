using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Walis.Api.Services;

namespace Walis.Tests;

public sealed class ProxyTransportSecurityTests
{
    [Fact]
    public async Task UntrustedSourceCannotFakeTlsWithForwardedProto()
    {
        await using var factory = new ProxyFactory(
            IPAddress.Parse("203.0.113.10"),
            "198.51.100.10",
            true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        Assert.Equal(
            HttpStatusCode.UpgradeRequired,
            (await client.GetAsync("/api/auth/status")).StatusCode);
    }

    [Fact]
    public async Task UntrustedForwardedProtoIsNotTreatedAsTlsEvenWhenPlainHttpIsAllowed()
    {
        await using var factory = new ProxyFactory(
            IPAddress.Parse("203.0.113.10"),
            "198.51.100.10");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await client.GetAsync("/api/auth/status");

        // The request is served, but as the plain HTTP it really is: no HSTS on the word of an unlisted source.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task LoopbackIsNotATrustedProxyUnlessListed()
    {
        // A build script on this machine reaches the API from loopback; it must not be able to declare TLS or another origin.
        await using var factory = new ProxyFactory(IPAddress.Loopback, "");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");

        var response = await client.GetAsync("/api/auth/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task LoopbackPeerUnderAnotherHostNameIsNotTheLocalMachine()
    {
        // A reverse proxy that forgot to forward the client address, or a page that rebound its name to 127.0.0.1,
        // both arrive from loopback. Only loopback addressed as localhost may set up without the code.
        await using var factory = new ProxyFactory(IPAddress.Loopback, "198.51.100.10");
        var account = new
        {
            username = "rebound",
            displayName = "Rebound",
            password = "Correct-Horse-Battery-Staple-123!"
        };

        using var foreignHost = factory.CreateClient();
        foreignHost.DefaultRequestHeaders.Host = "walis.example.test";
        Assert.True((await foreignHost.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/status"))
            .GetProperty("setupCodeRequired").GetBoolean());
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await foreignHost.PostAsJsonAsync("/api/auth/setup", account)).StatusCode);

        using var localhost = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.OK,
            (await localhost.PostAsJsonAsync("/api/auth/setup", account)).StatusCode);
    }

    [Fact]
    public async Task OnlyExplicitlyListedProxiesCanDeclareExternalTls()
    {
        await using var factory = new ProxyFactory(
            IPAddress.Parse("198.51.100.10"),
            "198.51.100.10");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await client.GetAsync("/api/auth/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "max-age=31536000",
            response.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public async Task SingleProxyTrustsOnlyNearestSourceIpAndLeftValuesCannotFakeLoopback()
    {
        await using var factory = new ProxyFactory(
            IPAddress.Parse("198.51.100.10"),
            "198.51.100.10");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1, 203.0.113.7");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await client.PostAsJsonAsync("/api/auth/setup", new
        {
            username = "spoofed-loopback",
            displayName = "Spoofed Loopback",
            password = "Correct-Horse-Battery-Staple-123!"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ArtifactEndpointRaisesKestrelOneMiBLimitExactlyToStreamingBudget()
    {
        await using var factory = new ProxyFactory(
            IPAddress.Parse("198.51.100.10"),
            "198.51.100.10");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/builds/{Guid.NewGuid()}/artifacts")
        {
            Content = new ByteArrayContent([0])
        };
        request.Headers.Add("X-Forwarded-Proto", "https");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(
            RemoteArtifactUploadService.MaximumRequestBytes,
            factory.RequestBodyFeature.MaxRequestBodySize);
    }

    private sealed class ProxyFactory(IPAddress remoteAddress, string trustedProxies, bool requireHttps = false)
        : WebApplicationFactory<Program>
    {
        private readonly string _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"walis-proxy-{Guid.NewGuid():N}.db");

        public ProbeRequestBodySizeFeature RequestBodyFeature { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Walis"] = $"Data Source={_databasePath}",
                    ["Security:TrustedProxies"] = trustedProxies,
                    ["Security:RequireHttps"] = requireHttps ? "true" : null,
                    // These tests are about which peer counts as local; Production's default setup-code requirement is
                    // covered in BootstrapPolicyTests.
                    ["Security:RequireSetupCode"] = "false"
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(
                    new RemoteAddressStartupFilter(remoteAddress, RequestBodyFeature)));

            return base.CreateHost(builder);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            using (var connection = (SqliteConnection)new Walis.Core.Database.SqliteConnectionFactory($"Data Source={_databasePath}").Create())
                SqliteConnection.ClearPool(connection);

            if (File.Exists(_databasePath)) File.Delete(_databasePath);
        }
    }

    private sealed class RemoteAddressStartupFilter(
        IPAddress remoteAddress,
        ProbeRequestBodySizeFeature requestBodyFeature) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, continuePipeline) =>
                {
                    context.Connection.RemoteIpAddress = remoteAddress;
                    context.Features.Set<IHttpMaxRequestBodySizeFeature>(requestBodyFeature);
                    await continuePipeline();
                });

                next(app);
            };
        }
    }

    public sealed class ProbeRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; } = 1024 * 1024;
    }
}