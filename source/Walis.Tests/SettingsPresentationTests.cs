using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Walis.Tests;

public class SettingsPresentationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task DefaultWorkingDirectoryIsShownToOperatorsAndAboveOnly()
    {
        await using var factory = new WalisApiFactory();
        var admin = factory.CreateClient();

        var current = await admin.GetFromJsonAsync<JsonElement>("/api/settings", Json);
        var settings = current.GetProperty("settings").Deserialize<Dictionary<string, JsonElement>>(Json)!;
        settings["defaultWorkingDirectory"] = JsonSerializer.SerializeToElement("/srv/walis/workspaces");
        (await admin.PutAsJsonAsync("/api/settings", settings)).EnsureSuccessStatusCode();

        var viewer = await ReadSettingsAsAsync(factory, admin, "viewer");
        var operatorView = await ReadSettingsAsAsync(factory, admin, "operator");

        Assert.False(viewer.TryGetProperty("defaultWorkingDirectory", out _));
        Assert.True(viewer.TryGetProperty("defaultTimeoutMinutes", out _));
        Assert.Equal("/srv/walis/workspaces", operatorView.GetProperty("defaultWorkingDirectory").GetString());

        // Neither non-admin shape leaks the admin-only topology either.
        Assert.False(viewer.TryGetProperty("backupDirectory", out _));
        Assert.False(operatorView.TryGetProperty("smtpHost", out _));
    }

    private static async Task<JsonElement> ReadSettingsAsAsync(WalisApiFactory factory, HttpClient admin, string role)
    {
        var created = await admin.PostAsJsonAsync("/api/api-keys", new { name = $"{role} key", role, scope = "api" });
        created.EnsureSuccessStatusCode();
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;

        using var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetFromJsonAsync<JsonElement>("/api/settings", Json);
        return response.GetProperty("settings");
    }
}
