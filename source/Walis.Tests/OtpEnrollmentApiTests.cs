using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Walis.Core.Security;

namespace Walis.Tests;

public class OtpEnrollmentApiTests
{
    private const string Password = "walis-password-for-otp";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task StartingAndEnablingOneTimePasswordsRequireTheCurrentPassword()
    {
        await using var factory = new WalisApiFactory();
        using var client = factory.CreateAnonymousClient();

        var setup = await client.PostAsJsonAsync("/api/auth/setup?bearer=true",
            new { username = "leo", displayName = "Leo", password = Password });
        setup.EnsureSuccessStatusCode();
        var token = (await setup.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // A session alone is not enough to start: no password, wrong password.
        var missing = await client.PostAsJsonAsync("/api/auth/otp/setup", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("currentPassword", await missing.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/otp/setup", new { currentPassword = "wrong" })).StatusCode);

        var started = await client.PostAsJsonAsync("/api/auth/otp/setup", new { currentPassword = Password });
        started.EnsureSuccessStatusCode();
        var secret = (await started.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("secret").GetString()!;
        var code = Totp.Compute(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Totp.PeriodSeconds);

        // A valid authenticator code without the password must not enable anything.
        var withoutPassword = await client.PostAsJsonAsync("/api/auth/otp/enable", new { code });
        Assert.Equal(HttpStatusCode.BadRequest, withoutPassword.StatusCode);
        Assert.Contains("currentPassword", await withoutPassword.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/otp/enable", new { code, currentPassword = "wrong" })).StatusCode);
        Assert.False(await HasOtpAsync(client));

        var enabled = await client.PostAsJsonAsync("/api/auth/otp/enable", new { code, currentPassword = Password });
        enabled.EnsureSuccessStatusCode();
        Assert.NotEmpty((await enabled.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("recoveryCodes")
            .EnumerateArray());
        Assert.True(await HasOtpAsync(client));
    }

    private static async Task<bool> HasOtpAsync(HttpClient client)
    {
        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status", Json);
        return status.GetProperty("user").GetProperty("hasOtp").GetBoolean();
    }
}
