using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Walis.Api.Services;
using Walis.Core.Models;

namespace Walis.Tests;

public class McpAccessTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void StoreIsUnrestrictedWhenEmptyAndFailsClosedOnGarbage()
    {
        var store = new McpAccessStore();

        Assert.False(store.IsRestricted);
        Assert.True(store.IsAllowed(IPAddress.Parse("198.51.100.1")));
        Assert.True(store.IsAllowed(null));

        store.Update("203.0.113.0/24\n::1");
        Assert.True(store.IsRestricted);
        Assert.True(store.IsAllowed(IPAddress.Parse("203.0.113.9")));
        Assert.True(store.IsAllowed(IPAddress.Parse("::ffff:203.0.113.9")));
        Assert.True(store.IsAllowed(IPAddress.IPv6Loopback));
        Assert.False(store.IsAllowed(IPAddress.Loopback));
        Assert.False(store.IsAllowed(null));

        // A restored archive with an unparseable entry must not turn into "everyone".
        store.Update("not-an-address");
        Assert.True(store.IsRestricted);
        Assert.False(store.IsAllowed(IPAddress.Loopback));

        store.Update("  \n");
        Assert.False(store.IsRestricted);
    }

    [Fact]
    public async Task EmptyListLeavesMcpUnrestricted()
    {
        await using var factory = new WalisApiFactory();

        var response = await CallMcpAsync(factory.CreateRemoteClient("198.51.100.77"), null);

        response.EnsureSuccessStatusCode();
        Assert.Contains("list_jobs", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConfiguredListRefusesUnlistedAddressesBeforeAuthentication()
    {
        await using var factory = new WalisApiFactory();
        var admin = factory.CreateClient();
        await SetMcpAllowedAddressesAsync(admin, "203.0.113.0/24\n2001:db8::/32");

        // Listed: IPv4 range, IPv6 range, and the IPv4-mapped form Kestrel reports on dual-stack sockets.
        foreach (var address in new[] { "203.0.113.10", "2001:db8::42", "::ffff:203.0.113.200" })
            (await CallMcpAsync(factory.CreateRemoteClient(address), null)).EnsureSuccessStatusCode();

        // Loopback is not implied: the administrator's own machine is refused unless listed.
        var local = await CallMcpAsync(admin, null);
        Assert.Equal(HttpStatusCode.Forbidden, local.StatusCode);
        Assert.Contains("此來源位址不允許使用 MCP。", await local.Content.ReadAsStringAsync());

        // Refused on the address alone: a wrong key from an unlisted address gets the same 403, not a 401 that confirms the key is wrong.
        var probe = await CallMcpAsync(factory.CreateRemoteAnonymousClient("198.51.100.5"), "walis_abcdef_abcdefghijklmnopqrstuvwx");
        Assert.Equal(HttpStatusCode.Forbidden, probe.StatusCode);
        Assert.Contains("此來源位址不允許使用 MCP。", await probe.Content.ReadAsStringAsync());

        // GET and DELETE on the transport are covered too, and the rest of the API is untouched.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/mcp")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.DeleteAsync("/mcp")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/jobs")).StatusCode);

        // Clearing the list restores access right away on this node.
        await SetMcpAllowedAddressesAsync(admin, string.Empty);
        (await CallMcpAsync(admin, null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SettingsRejectInvalidOrOversizedAddressLists()
    {
        await using var factory = new WalisApiFactory();
        var admin = factory.CreateClient();
        var settings = await ReadSettingsAsync(admin);

        settings.McpAllowedAddresses = "127.0.0.1/32\nwalis.example.test";
        var invalid = await admin.PutAsJsonAsync("/api/settings", settings);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var body = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("mcpAllowedAddresses", body);
        Assert.Contains("walis.example.test", body);

        settings.McpAllowedAddresses = string.Join('\n', Enumerable.Range(0, 65).Select(index => $"10.0.{index}.0/24"));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings", settings)).StatusCode);

        // Valid entries are kept, one per line, after commas and blank lines are normalized away.
        settings.McpAllowedAddresses = "127.0.0.1/32, ::1/128\n\n203.0.113.0/24";
        var saved = await admin.PutAsJsonAsync("/api/settings", settings);
        saved.EnsureSuccessStatusCode();
        Assert.Equal("127.0.0.1/32\n::1/128\n203.0.113.0/24",
            (await saved.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("mcpAllowedAddresses").GetString());
    }

    [Fact]
    public async Task ApiKeyCanBePinnedToSourceAddresses()
    {
        await using var factory = new WalisApiFactory();
        var admin = factory.CreateClient();

        var created = await admin.PostAsJsonAsync("/api/api-keys", new
        {
            name = "部署機器人",
            role = "operator",
            scope = "api",
            allowedAddresses = "203.0.113.10/32; 2001:db8::/64"
        });
        created.EnsureSuccessStatusCode();
        var issued = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var token = issued.GetProperty("token").GetString()!;
        Assert.Equal("203.0.113.10\n2001:db8::/64", issued.GetProperty("key").GetProperty("allowedAddresses").GetString());

        // From a listed address the key works as usual.
        Assert.Equal(HttpStatusCode.OK,
            (await SendWithKeyAsync(factory.CreateRemoteAnonymousClient("203.0.113.10"), token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await SendWithKeyAsync(factory.CreateRemoteAnonymousClient("2001:db8::9"), token)).StatusCode);

        // From anywhere else, including this machine, it is refused outright.
        var elsewhere = await SendWithKeyAsync(factory.CreateAnonymousClient(), token);
        Assert.Equal(HttpStatusCode.Forbidden, elsewhere.StatusCode);
        Assert.Contains("此來源位址不允許使用這把金鑰。", await elsewhere.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendWithKeyAsync(factory.CreateRemoteAnonymousClient("203.0.113.11"), token)).StatusCode);

        // The list shows the pin, and an unpinned key is still usable from anywhere.
        var listed = await admin.GetFromJsonAsync<List<ApiKey>>("/api/api-keys", Json);
        Assert.Equal("203.0.113.10\n2001:db8::/64", listed!.Single(key => key.Name == "部署機器人").AllowedAddresses);

        var unpinned = await admin.PostAsJsonAsync("/api/api-keys", new { name = "anywhere", role = "viewer", scope = "api" });
        var anywhereToken = (await unpinned.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK,
            (await SendWithKeyAsync(factory.CreateRemoteAnonymousClient("198.51.100.1"), anywhereToken)).StatusCode);
    }

    [Fact]
    public async Task RunnerKeyPinnedToAnAddressCannotClaimFromElsewhere()
    {
        await using var factory = new WalisApiFactory();
        var admin = factory.CreateClient();

        var created = await admin.PostAsJsonAsync("/api/api-keys", new
        {
            name = "builder-01 key",
            role = "operator",
            runnerName = "builder-01",
            allowedAddresses = "10.20.0.7"
        });
        created.EnsureSuccessStatusCode();
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;

        async Task<HttpStatusCode> ClaimAsync(string address)
        {
            using var client = factory.CreateRemoteAnonymousClient(address);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (await client.PostAsJsonAsync("/api/runners/builder-01/claim", new { })).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Forbidden, await ClaimAsync("10.20.0.8"));
        Assert.NotEqual(HttpStatusCode.Forbidden, await ClaimAsync("10.20.0.7"));
    }

    [Fact]
    public async Task ApiKeyAddressListIsValidated()
    {
        await using var factory = new WalisApiFactory();
        var admin = factory.CreateClient();

        var response = await admin.PostAsJsonAsync("/api/api-keys", new
        {
            name = "bad",
            role = "viewer",
            allowedAddresses = "10.0.0.0/8\nrunner.example.test"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("allowedAddresses", body);
        Assert.Contains("runner.example.test", body);
    }

    [Fact]
    public void KeyWithOnlyUnparseableEntriesIsRefusedEverywhere()
    {
        var anywhere = new ApiKey { AllowedAddresses = null };
        Assert.True(AuthenticationMiddleware.IsKeyAllowedFrom(anywhere, IPAddress.Loopback));
        Assert.True(AuthenticationMiddleware.IsKeyAllowedFrom(anywhere, null));

        var corrupt = new ApiKey { AllowedAddresses = "garbage" };
        Assert.False(AuthenticationMiddleware.IsKeyAllowedFrom(corrupt, IPAddress.Loopback));

        var pinned = new ApiKey { AllowedAddresses = "127.0.0.1" };
        Assert.True(AuthenticationMiddleware.IsKeyAllowedFrom(pinned, IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(AuthenticationMiddleware.IsKeyAllowedFrom(pinned, null));
    }

    private static async Task<HttpResponseMessage> SendWithKeyAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/jobs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<WalisSettings> ReadSettingsAsync(HttpClient admin)
    {
        var current = await admin.GetFromJsonAsync<JsonElement>("/api/settings");
        return current.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
    }

    private static async Task SetMcpAllowedAddressesAsync(HttpClient admin, string addresses)
    {
        var settings = await ReadSettingsAsync(admin);
        settings.McpAllowedAddresses = addresses;
        (await admin.PutAsJsonAsync("/api/settings", settings)).EnsureSuccessStatusCode();
    }

    private static async Task<HttpResponseMessage> CallMcpAsync(HttpClient client, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }, options: Json)
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }
}
