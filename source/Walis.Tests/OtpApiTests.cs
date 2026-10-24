using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Tests;

public class OtpApiTests
{
    private const string Username = "leo";
    private const string Password = "walis-password";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string CurrentCode(string secret)
    {
        return Totp.Compute(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Totp.PeriodSeconds);
    }

    private static async Task<(HttpClient Client, string Secret, string[] RecoveryCodes)> EnableOtpAsync(
        WalisApiFactory factory)
    {
        var client = factory.CreateClient();

        var setup = await client.PostAsJsonAsync("/api/auth/setup?bearer=true", new
        {
            username = Username,
            displayName = "Leo",
            password = Password
        });
        setup.EnsureSuccessStatusCode();

        var token = (await setup.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Remove("X-Walis-Reauth");
        client.DefaultRequestHeaders.Add("X-Walis-Reauth", "walis-password");

        var enrollment = await client.PostAsJsonAsync("/api/auth/otp/setup", new { currentPassword = Password });
        enrollment.EnsureSuccessStatusCode();

        var payload = await enrollment.Content.ReadFromJsonAsync<JsonElement>(Json);
        var secret = payload.GetProperty("secret").GetString()!;

        Assert.StartsWith("otpauth://totp/", payload.GetProperty("uri").GetString());

        // Enabling consumes the code of the previous time window, and the sign-in cases use the current one, so the test never waits 30 seconds.
        var enrollmentCode = Totp.Compute(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Totp.PeriodSeconds - 1);
        var enable = await client.PostAsJsonAsync("/api/auth/otp/enable", new { code = enrollmentCode, currentPassword = Password });
        enable.EnsureSuccessStatusCode();

        var codes = (await enable.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("recoveryCodes")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();

        return (client, secret, codes);
    }

    [Fact]
    public async Task SignInRequiresSecondStepAfterEnablingOtp()
    {
        await using var factory = new WalisApiFactory();
        var (_, secret, _) = await EnableOtpAsync(factory);
        var anonymous = factory.CreateAnonymousClient();

        var login = await anonymous.PostAsJsonAsync("/api/auth/login?bearer=true",
            new { username = Username, password = Password });
        login.EnsureSuccessStatusCode();

        var payload = await login.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(payload.GetProperty("otpRequired").GetBoolean());

        var challenge = payload.GetProperty("challenge").GetString();

        var verified =
            await anonymous.PostAsJsonAsync("/api/auth/login/otp?bearer=true", new { challenge, code = CurrentCode(secret) });
        verified.EnsureSuccessStatusCode();

        var session = await verified.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(string.IsNullOrWhiteSpace(session.GetProperty("token").GetString()));
        Assert.True(session.GetProperty("user").GetProperty("hasOtp").GetBoolean());
    }

    [Fact]
    public async Task WrongCodeReturnsNewChallenge()
    {
        await using var factory = new WalisApiFactory();
        await EnableOtpAsync(factory);
        var anonymous = factory.CreateAnonymousClient();

        var login = await anonymous.PostAsJsonAsync("/api/auth/login?bearer=true",
            new { username = Username, password = Password });
        var challenge = (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("challenge").GetString();

        var wrong = await anonymous.PostAsJsonAsync("/api/auth/login/otp?bearer=true", new { challenge, code = "000000" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(
            (await wrong.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("challenge").GetString()));
    }

    [Fact]
    public async Task ChallengeCanBeUsedOnlyOnce()
    {
        await using var factory = new WalisApiFactory();
        var (_, secret, _) = await EnableOtpAsync(factory);
        var anonymous = factory.CreateAnonymousClient();

        var login = await anonymous.PostAsJsonAsync("/api/auth/login?bearer=true",
            new { username = Username, password = Password });
        var challenge = (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("challenge").GetString();

        (await anonymous.PostAsJsonAsync("/api/auth/login/otp?bearer=true", new { challenge, code = CurrentCode(secret) }))
            .EnsureSuccessStatusCode();

        var replay =
            await anonymous.PostAsJsonAsync("/api/auth/login/otp?bearer=true", new { challenge, code = CurrentCode(secret) });

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task BackupCodeSignsInAndExpiresAfterUse()
    {
        await using var factory = new WalisApiFactory();
        var (_, _, codes) = await EnableOtpAsync(factory);
        var anonymous = factory.CreateAnonymousClient();

        async Task<string?> ChallengeAsync()
        {
            var login = await anonymous.PostAsJsonAsync("/api/auth/login?bearer=true",
                new { username = Username, password = Password });
            return (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("challenge").GetString();
        }

        var first = await anonymous.PostAsJsonAsync("/api/auth/login/otp?bearer=true",
            new { challenge = await ChallengeAsync(), code = codes[0] });
        first.EnsureSuccessStatusCode();
        Assert.True((await first.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("usedRecoveryCode")
            .GetBoolean());

        var reuse = await anonymous.PostAsJsonAsync("/api/auth/login/otp?bearer=true",
            new { challenge = await ChallengeAsync(), code = codes[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [Fact]
    public async Task EnabledAccountCannotGenerateSetupAgain()
    {
        await using var factory = new WalisApiFactory();
        var (client, _, _) = await EnableOtpAsync(factory);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/otp/setup", new { currentPassword = Password })).StatusCode);
    }

    [Fact]
    public async Task EnabledAccountCannotResetBackupCodesThroughEnableEndpoint()
    {
        await using var factory = new WalisApiFactory();
        var (client, secret, _) = await EnableOtpAsync(factory);
        var security = factory.Services.GetRequiredService<ISecurityRepository>();
        var before = await security.FindUserAsync(Username);

        var response = await client.PostAsJsonAsync("/api/auth/otp/enable", new { code = CurrentCode(secret), currentPassword = Password });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before!.RecoveryCodes, (await security.FindUserAsync(Username))!.RecoveryCodes);
    }

    [Fact]
    public async Task CodeUsedForEnablingCannotBeReusedForSignIn()
    {
        await using var factory = new WalisApiFactory();
        var client = await RequireOtpAsync(factory);
        var enrollment = await client.PostAsJsonAsync("/api/auth/otp/setup", new { currentPassword = Password });
        enrollment.EnsureSuccessStatusCode();
        var secret = (await enrollment.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("secret").GetString()!;
        var code = CurrentCode(secret);
        (await client.PostAsJsonAsync("/api/auth/otp/enable", new { code, currentPassword = Password })).EnsureSuccessStatusCode();

        var anonymous = factory.CreateAnonymousClient();
        var login = await anonymous.PostAsJsonAsync("/api/auth/login?bearer=true",
            new { username = Username, password = Password });
        var challenge = (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("challenge").GetString();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/login/otp?bearer=true", new { challenge, code })).StatusCode);
    }

    [Fact]
    public async Task DisablingOtpRequiresCorrectPassword()
    {
        await using var factory = new WalisApiFactory();
        var (client, _, _) = await EnableOtpAsync(factory);

        var wrong = await client.PostAsJsonAsync("/api/auth/otp/disable", new { currentPassword = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var correct = await client.PostAsJsonAsync("/api/auth/otp/disable", new { currentPassword = Password });
        Assert.Equal(HttpStatusCode.NoContent, correct.StatusCode);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.False(status.GetProperty("user").GetProperty("hasOtp").GetBoolean());
    }

    [Fact]
    public async Task AdminCanClearOtpOfOtherAccounts()
    {
        await using var factory = new WalisApiFactory();
        var (client, _, _) = await EnableOtpAsync(factory);

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");
        var id = accounts.EnumerateArray().First().GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/accounts/{id}/otp/reset", null)).StatusCode);

        // Resetting OTP revokes every session of the target account; this case resets its own, so the old token must be invalid too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/accounts")).StatusCode);
        var after = await factory.Services.GetRequiredService<ISecurityRepository>()
            .GetUserAsync(Guid.Parse(id!));
        Assert.NotNull(after);
        Assert.False(after!.HasOtp);
    }

    /// <summary>Turns on requireSignIn and requireOtp together and returns the client of the account that still has no OTP set up.</summary>
    private static async Task<HttpClient> RequireOtpAsync(WalisApiFactory factory)
    {
        var client = factory.CreateClient();

        var setup = await client.PostAsJsonAsync("/api/auth/setup?bearer=true", new
        {
            username = Username,
            displayName = "Leo",
            password = Password
        });
        setup.EnsureSuccessStatusCode();

        var token = (await setup.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Remove("X-Walis-Reauth");
        client.DefaultRequestHeaders.Add("X-Walis-Reauth", "walis-password");

        var payload = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        var settings = payload.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
        settings.RequireSignIn = true;
        settings.RequireOtp = true;

        (await client.PutAsJsonAsync("/api/settings", settings)).EnsureSuccessStatusCode();

        return client;
    }

    [Fact]
    public async Task AccountsWithoutOtpAreBlockedWhenOtpRequired()
    {
        await using var factory = new WalisApiFactory();
        var client = await RequireOtpAsync(factory);

        var blocked = await client.GetAsync("/api/jobs/");

        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        var body = await blocked.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(body.GetProperty("otpSetupRequired").GetBoolean());
    }

    [Fact]
    public async Task BlockedAccountCanStillCompleteSetup()
    {
        await using var factory = new WalisApiFactory();
        var client = await RequireOtpAsync(factory);

        // /api/auth/* is not affected by the gate, otherwise the user could never finish setup.
        var enrollment = await client.PostAsJsonAsync("/api/auth/otp/setup", new { currentPassword = Password });
        enrollment.EnsureSuccessStatusCode();

        var secret = (await enrollment.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("secret").GetString()!;
        var enable = await client.PostAsJsonAsync("/api/auth/otp/enable", new { code = CurrentCode(secret), currentPassword = Password });
        enable.EnsureSuccessStatusCode();

        // No re-sign-in needed after enabling; the same token passes immediately.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/jobs/")).StatusCode);
    }
}

public class AccountApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<HttpClient> SignedInAsync(WalisApiFactory factory)
    {
        var client = factory.CreateClient();
        var setup = await client.PostAsJsonAsync("/api/auth/setup?bearer=true", new
        {
            username = "leo",
            displayName = "Leo",
            password = "walis-password"
        });
        setup.EnsureSuccessStatusCode();

        var token = (await setup.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Remove("X-Walis-Reauth");
        client.DefaultRequestHeaders.Add("X-Walis-Reauth", "walis-password");
        return client;
    }

    [Fact]
    public async Task FirstAccountIsAdmin()
    {
        await using var factory = new WalisApiFactory();
        var client = await SignedInAsync(factory);

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");

        Assert.Equal("admin", accounts.EnumerateArray().First().GetProperty("role").GetString());
    }

    [Fact]
    public async Task CanCreateAccountsWithDifferentRoles()
    {
        await using var factory = new WalisApiFactory();
        var client = await SignedInAsync(factory);

        var created = await client.PostAsJsonAsync("/api/accounts", new
        {
            username = "bob",
            displayName = "Bob",
            password = "walis-password",
            role = "operator"
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("operator",
            (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task InvalidRoleIsRejected()
    {
        await using var factory = new WalisApiFactory();
        var client = await SignedInAsync(factory);

        var response = await client.PostAsJsonAsync("/api/accounts", new
        {
            username = "bob",
            displayName = "Bob",
            password = "walis-password",
            role = "superuser"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LastAdminCannotBeDemotedOrDisabled()
    {
        await using var factory = new WalisApiFactory();
        var client = await SignedInAsync(factory);

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");
        var id = accounts.EnumerateArray().First().GetProperty("id").GetString();

        var demote = await client.PutAsJsonAsync($"/api/accounts/{id}", new
        {
            displayName = "Leo",
            role = "viewer",
            isDisabled = false
        });

        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);

        var disable = await client.PutAsJsonAsync($"/api/accounts/{id}", new
        {
            displayName = "Leo",
            role = "admin",
            isDisabled = true
        });

        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);
    }

    [Fact]
    public async Task CannotDeleteCurrentlySignedInAccount()
    {
        await using var factory = new WalisApiFactory();
        var client = await SignedInAsync(factory);

        await client.PostAsJsonAsync("/api/accounts", new
        {
            username = "bob",
            displayName = "Bob",
            password = "walis-password",
            role = "admin"
        });

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");
        var me = accounts.EnumerateArray().First(item => item.GetProperty("username").GetString() == "leo");

        var response = await client.DeleteAsync($"/api/accounts/{me.GetProperty("id").GetString()}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AdminCanResetOtherAccountsPassword()
    {
        await using var factory = new WalisApiFactory();
        var client = await SignedInAsync(factory);

        await client.PostAsJsonAsync("/api/accounts", new
        {
            username = "bob",
            displayName = "Bob",
            password = "walis-password",
            role = "viewer"
        });

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");
        var bob = accounts.EnumerateArray().First(item => item.GetProperty("username").GetString() == "bob");

        var reset = await client.PostAsJsonAsync(
            $"/api/accounts/{bob.GetProperty("id").GetString()}/password",
            new { newPassword = "another-password" });

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var login = await factory.CreateClient()
            .PostAsJsonAsync("/api/auth/login?bearer=true", new { username = "bob", password = "another-password" });

        login.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task DisabledAccountCannotSignIn()
    {
        await using var factory = new WalisApiFactory();
        var client = await SignedInAsync(factory);

        await client.PostAsJsonAsync("/api/accounts", new
        {
            username = "bob",
            displayName = "Bob",
            password = "walis-password",
            role = "operator"
        });

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");
        var bob = accounts.EnumerateArray().First(item => item.GetProperty("username").GetString() == "bob");

        await client.PutAsJsonAsync($"/api/accounts/{bob.GetProperty("id").GetString()}", new
        {
            displayName = "Bob",
            role = "operator",
            isDisabled = true
        });

        var login = await factory.CreateClient()
            .PostAsJsonAsync("/api/auth/login?bearer=true", new { username = "bob", password = "walis-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task RoleUpgradeRevokesOldSessionInsteadOfElevatingIt()
    {
        await using var factory = new WalisApiFactory();
        var admin = await SignedInAsync(factory);

        var created = await admin.PostAsJsonAsync("/api/accounts", new
        {
            username = "bob",
            displayName = "Bob",
            password = "walis-password",
            role = "operator"
        });
        created.EnsureSuccessStatusCode();
        var bobId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();

        var bob = factory.CreateAnonymousClient();
        var login = await bob.PostAsJsonAsync(
            "/api/auth/login?bearer=true",
            new { username = "bob", password = "walis-password" });
        login.EnsureSuccessStatusCode();
        bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString());
        bob.DefaultRequestHeaders.Remove("X-Walis-Reauth");
        bob.DefaultRequestHeaders.Add("X-Walis-Reauth", "walis-password");

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/accounts/")).StatusCode);

        var promote = await admin.PutAsJsonAsync($"/api/accounts/{bobId}", new
        {
            displayName = "Bob",
            role = "admin",
            isDisabled = false
        });
        promote.EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/api/jobs/")).StatusCode);
    }
}