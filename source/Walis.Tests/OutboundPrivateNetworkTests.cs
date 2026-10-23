using Walis.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Walis.Api.Services;
using Walis.Core.Models;

namespace Walis.Tests;

public class OutboundPrivateNetworkTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SwitchDefaultsToBlockedAndOnlyTrueOpensIt()
    {
        Assert.False(OutboundHttpHandlers.AllowsPrivateNetwork(Configuration(null)));
        Assert.False(OutboundHttpHandlers.AllowsPrivateNetwork(Configuration("yes")));
        Assert.False(OutboundHttpHandlers.AllowsPrivateNetwork(Configuration("false")));
        Assert.True(OutboundHttpHandlers.AllowsPrivateNetwork(Configuration("true")));

        using var guarded = OutboundHttpHandlers.CreateGuarded(3, false);
        Assert.False(guarded.UseProxy);
        Assert.NotNull(guarded.ConnectCallback);
        Assert.False(guarded.AllowAutoRedirect);

        using var open = OutboundHttpHandlers.CreateGuarded(3, true);
        Assert.Null(open.ConnectCallback);
        Assert.False(open.AllowAutoRedirect);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9/hook")]
    [InlineData("http://localhost:9/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://[::1]:9/")]
    public async Task GuardedHandlerRefusesPrivateDestinationsBeforeConnecting(string url)
    {
        using var client = new HttpClient(OutboundHttpHandlers.CreateGuarded(1, false))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));

        Assert.StartsWith("不允許連線到私有網段：", exception.Message);
        Assert.Contains(new Uri(url).Host.Trim('[', ']'), exception.Message);
    }

    [Fact]
    public async Task NotificationTestToAPrivateAddressReportsTheRefusal()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/notifications/channels", new
        {
            kind = "webhook",
            name = "internal hook",
            target = "http://169.254.169.254/latest/meta-data/",
            isEnabled = true
        });
        created.EnsureSuccessStatusCode();
        var channel = await created.Content.ReadFromJsonAsync<NotificationChannel>(Json);

        var tested = await client.PostAsync($"/api/notifications/channels/{channel!.Id}/test", null);
        tested.EnsureSuccessStatusCode();
        var entry = await tested.Content.ReadFromJsonAsync<NotificationLogEntry>(Json);

        Assert.False(entry!.Succeeded);
        Assert.Contains("不允許連線到私有網段", entry.Detail);
    }

    [Fact]
    public async Task SmtpToAPrivateAddressIsRefusedLikeHttpDestinations()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();
        var settings = factory.Services.GetRequiredService<ISettingsRepository>();
        var current = await settings.GetAsync();
        current.SmtpHost = "127.0.0.1";
        current.SmtpPort = 25;
        current.SmtpFrom = "walis@example.test";
        await settings.SaveAsync(current);

        var created = await client.PostAsJsonAsync("/api/notifications/channels", new
        {
            kind = "email", name = "internal smtp", target = "ops@example.test", isEnabled = true
        });
        created.EnsureSuccessStatusCode();
        var channel = await created.Content.ReadFromJsonAsync<NotificationChannel>(Json);

        var tested = await client.PostAsync($"/api/notifications/channels/{channel!.Id}/test", null);
        tested.EnsureSuccessStatusCode();
        var entry = await tested.Content.ReadFromJsonAsync<NotificationLogEntry>(Json);

        Assert.False(entry!.Succeeded);
        Assert.Contains("不允許連線到私有網段", entry.Detail);
    }

    private static IConfiguration Configuration(string? value)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:OutboundAllowPrivateNetwork"] = value })
            .Build();
    }
}
