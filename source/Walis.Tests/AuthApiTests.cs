using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Walis.Api.Services;
using Walis.Core.Models;

namespace Walis.Tests;

/// <summary>Uses a separate database so enabling sign-in does not affect the other API tests.</summary>
public class AuthApiTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    private const string Username = "leo";
    private const string Password = "walis-password";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<string> EnsureTokenAsync(HttpClient client)
    {
        var setup = await client.PostAsJsonAsync("/api/auth/setup?bearer=true", new
        {
            username = Username,
            displayName = "Leo",
            password = Password
        });

        if (setup.StatusCode == HttpStatusCode.OK)
            return (await setup.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;

        var login = await client.PostAsJsonAsync("/api/auth/login?bearer=true", new { username = Username, password = Password });
        login.EnsureSuccessStatusCode();

        return (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task SignInFailsWithWrongPassword()
    {
        var client = factory.CreateClient();
        await EnsureTokenAsync(client);

        var response = await client.PostAsJsonAsync("/api/auth/login?bearer=true", new { username = Username, password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SetupCannotRunAgainOnceAccountExists()
    {
        var client = factory.CreateClient();
        await EnsureTokenAsync(client);

        var response = await client.PostAsJsonAsync("/api/auth/setup?bearer=true", new
        {
            username = "another",
            displayName = "Another",
            password = "walis-password"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ShortPasswordIsRejected()
    {
        var client = factory.CreateClient();
        await EnsureTokenAsync(client);

        var token = await EnsureTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Remove("X-Walis-Reauth");
        client.DefaultRequestHeaders.Add("X-Walis-Reauth", "walis-password");

        var response = await client.PostAsJsonAsync("/api/accounts", new
        {
            username = "shorty",
            displayName = "Shorty",
            password = "1234",
            role = "viewer"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RequestsWithoutTokenAreBlockedOnceSignInEnabled()
    {
        var client = factory.CreateClient();
        var token = await EnsureTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Remove("X-Walis-Reauth");
        client.DefaultRequestHeaders.Add("X-Walis-Reauth", "walis-password");

        var payload = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        var settings = payload.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
        settings.RequireSignIn = true;

        (await client.PutAsJsonAsync("/api/settings", settings)).EnsureSuccessStatusCode();

        try
        {
            var anonymous = factory.CreateAnonymousClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/jobs/")).StatusCode);

            // The health check and sign-in endpoints are unaffected.
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/auth/status")).StatusCode);

            // With the token, access works normally.
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/jobs/")).StatusCode);
        }
        finally
        {
            settings.RequireSignIn = false;
            (await client.PutAsJsonAsync("/api/settings", settings)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task FreshInstallRejectsAnonymousApiUntilSetupCompletes()
    {
        await using var isolated = new WalisApiFactory();
        var anonymous = isolated.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/jobs")).StatusCode);

        var setup = await anonymous.PostAsJsonAsync("/api/auth/setup?bearer=true", new
        {
            username = "first-admin",
            displayName = "First Admin",
            password = Password
        });

        setup.EnsureSuccessStatusCode();
        Assert.Equal("admin", (await setup.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("user").GetProperty("role").GetString());
    }

    [Fact]
    public async Task RemoteCannotCreateFirstAdminWithoutBootstrapToken()
    {
        await using var isolated = new WalisApiFactory();
        var remote = isolated.CreateRemoteAnonymousClient();

        var response = await remote.PostAsJsonAsync("/api/auth/setup?bearer=true", new
        {
            username = "remote-admin",
            displayName = "Remote Admin",
            password = Password
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RemoteCreatesFirstAdminWithTheSetupCodePrintedAtStartup()
    {
        await using var isolated = new WalisApiFactory();
        var setupCode = isolated.Services.GetRequiredService<SetupCode>();
        var code = Assert.IsType<string>(setupCode.Current);
        var remote = isolated.CreateRemoteAnonymousClient();
        var local = isolated.CreateAnonymousClient();

        // Only the remote setup screen has to ask for the code.
        Assert.True((await remote.GetFromJsonAsync<JsonElement>("/api/auth/status"))
            .GetProperty("setupCodeRequired").GetBoolean());
        Assert.False((await local.GetFromJsonAsync<JsonElement>("/api/auth/status"))
            .GetProperty("setupCodeRequired").GetBoolean());

        var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/auth/setup?bearer=true")
        {
            Content = JsonContent.Create(new { username = "remote-admin", displayName = "Remote", password = Password })
        };
        wrong.Headers.Add("X-Walis-Bootstrap-Token", "AAAAA-AAAAA-AAAAA-AAAAA");
        Assert.Equal(HttpStatusCode.Forbidden, (await remote.SendAsync(wrong)).StatusCode);

        // Typed by hand on another machine: lower case and without the separators must still be accepted.
        var typed = new HttpRequestMessage(HttpMethod.Post, "/api/auth/setup?bearer=true")
        {
            Content = JsonContent.Create(new { username = "remote-admin", displayName = "Remote", password = Password })
        };
        typed.Headers.Add("X-Walis-Bootstrap-Token", code.Replace("-", string.Empty).ToLowerInvariant());
        var created = await remote.SendAsync(typed);

        created.EnsureSuccessStatusCode();
        Assert.Equal("admin", (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("user").GetProperty("role").GetString());

        // Spent: the code is gone and the setup screen no longer asks for one.
        Assert.Null(setupCode.Current);
        Assert.False(setupCode.Matches(code));
        Assert.False((await remote.GetFromJsonAsync<JsonElement>("/api/auth/status"))
            .GetProperty("setupCodeRequired").GetBoolean());
    }

    [Fact]
    public async Task ConcurrentSetupCreatesOnlyOneAdmin()
    {
        await using var isolated = new WalisApiFactory();
        var requests = Enumerable.Range(0, 8).Select(index =>
            isolated.CreateAnonymousClient().PostAsJsonAsync("/api/auth/setup?bearer=true", new
            {
                username = $"admin-{index}",
                displayName = $"Admin {index}",
                password = Password
            }));

        var responses = await Task.WhenAll(requests);

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(
            responses.Where(response => response.StatusCode != HttpStatusCode.OK),
            response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
    }

    [Fact]
    public async Task BrowserSignInGetsOnlyTheCookieWhileCliGetsASeparateShortBearerSession()
    {
        await using var isolated = new WalisApiFactory();
        var anonymous = isolated.CreateAnonymousClient();
        var setup = await anonymous.PostAsJsonAsync("/api/auth/setup", new
        {
            username = "channel-admin",
            displayName = "Channel Admin",
            password = Password
        });
        setup.EnsureSuccessStatusCode();

        // Browser flow: cookie, no token in the body.
        var browserBody = await setup.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(browserBody.TryGetProperty("token", out _));
        var sessionCookie = Assert.Single(setup.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("walis_session=", StringComparison.Ordinal));
        var cookieToken = sessionCookie.Split(';', 2)[0]["walis_session=".Length..];

        // The default client keeps cookies; the channel checks below need a client that sends exactly what the test says.
        var raw = isolated.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });

        // CLI flow: a separate one-hour bearer session in the body and no cookie.
        var cli = await raw.PostAsJsonAsync("/api/auth/login?bearer=true", new { username = "channel-admin", password = Password });
        cli.EnsureSuccessStatusCode();
        Assert.False(cli.Headers.Contains("Set-Cookie"));
        var cliBody = await cli.Content.ReadFromJsonAsync<JsonElement>(Json);
        var bearer = cliBody.GetProperty("token").GetString()!;
        Assert.NotEqual(cookieToken, bearer);
        var expires = cliBody.GetProperty("expiresAt").GetDateTime();
        Assert.InRange(expires, DateTime.UtcNow.AddMinutes(55), DateTime.UtcNow.AddMinutes(65));

        // A page asking for a bearer token (it always sends Origin) still gets none.
        var page = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login?bearer=true")
        {
            Content = JsonContent.Create(new { username = "channel-admin", password = Password }, options: Json)
        };
        page.Headers.Add("Origin", "http://localhost:11124");
        var pageResponse = await raw.SendAsync(page);
        pageResponse.EnsureSuccessStatusCode();
        Assert.False((await pageResponse.Content.ReadFromJsonAsync<JsonElement>(Json)).TryGetProperty("token", out _));
        Assert.True(pageResponse.Headers.Contains("Set-Cookie"));

        // Each session works on its own channel only.
        var viaHeader = new HttpRequestMessage(HttpMethod.Get, "/api/settings");
        viaHeader.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        viaHeader.Headers.Remove("X-Walis-Reauth");
        viaHeader.Headers.Add("X-Walis-Reauth", "walis-password");
        Assert.Equal(HttpStatusCode.OK, (await raw.SendAsync(viaHeader)).StatusCode);

        var bearerAsCookie = new HttpRequestMessage(HttpMethod.Get, "/api/settings");
        bearerAsCookie.Headers.Add("Cookie", $"walis_session={bearer}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.SendAsync(bearerAsCookie)).StatusCode);

        var cookieAsBearer = new HttpRequestMessage(HttpMethod.Get, "/api/settings");
        cookieAsBearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cookieToken);
        cookieAsBearer.Headers.Remove("X-Walis-Reauth");
        cookieAsBearer.Headers.Add("X-Walis-Reauth", "walis-password");
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.SendAsync(cookieAsBearer)).StatusCode);

        var cookieAsCookie = new HttpRequestMessage(HttpMethod.Get, "/api/settings");
        cookieAsCookie.Headers.Add("Cookie", $"walis_session={cookieToken}");
        Assert.Equal(HttpStatusCode.OK, (await raw.SendAsync(cookieAsCookie)).StatusCode);
    }

    [Fact]
    public async Task BrowserSessionCookieRequiresCsrfHeaderForWrites()
    {
        await using var isolated = new WalisApiFactory();
        var browser = isolated.CreateAnonymousClient();
        var setup = await browser.PostAsJsonAsync("/api/auth/setup", new
        {
            username = "cookie-admin",
            displayName = "Cookie Admin",
            password = Password
        });
        setup.EnsureSuccessStatusCode();

        var cookies = setup.Headers.GetValues("Set-Cookie").ToArray();
        var sessionCookie =
            Assert.Single(cookies, value => value.StartsWith("walis_session=", StringComparison.Ordinal));
        var csrfCookie = Assert.Single(cookies, value => value.StartsWith("walis_csrf=", StringComparison.Ordinal));
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("httponly", csrfCookie, StringComparison.OrdinalIgnoreCase);

        var current = await browser.GetFromJsonAsync<JsonElement>("/api/settings");
        var settings = current.GetProperty("settings").Deserialize<WalisSettings>(Json)!;
        settings.SiteName = "Cookie protected";

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await browser.PutAsJsonAsync("/api/settings", settings)).StatusCode);

        var csrf = csrfCookie.Split(';', 2)[0]["walis_csrf=".Length..];
        var update = new HttpRequestMessage(HttpMethod.Put, "/api/settings")
        {
            Content = JsonContent.Create(settings, options: Json)
        };
        update.Headers.Add("X-Walis-CSRF", csrf);
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(update)).StatusCode);
    }

    [Fact]
    public async Task ProductionReverseProxyModeIssuesSecureCookieAndHsts()
    {
        await using var isolated = new WalisApiFactory
        {
            HostEnvironment = Environments.Production,
            RequireSetupCode = false,
            ForceSecureCookies = true
        };
        var browser = isolated.CreateAnonymousClient();

        var setup = await browser.PostAsJsonAsync("/api/auth/setup", new
        {
            username = "secure-admin",
            displayName = "Secure Admin",
            password = Password
        });
        setup.EnsureSuccessStatusCode();

        var sessionCookie = Assert.Single(
            setup.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("walis_session=", StringComparison.Ordinal));
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=31536000", setup.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public async Task FreshProductionInstallAnswersRemotePlaintextAndIssuesCookiesTheBrowserKeeps()
    {
        await using var isolated = new WalisApiFactory { HostEnvironment = Environments.Production, RequireSetupCode = false };
        var remote = isolated.CreateRemoteAnonymousClient();

        Assert.Equal(HttpStatusCode.OK, (await remote.GetAsync("/api/auth/status")).StatusCode);

        var setup = await isolated.CreateAnonymousClient().PostAsJsonAsync("/api/auth/setup", new
        {
            username = "plain-admin",
            displayName = "Plain Admin",
            password = Password
        });
        setup.EnsureSuccessStatusCode();

        // A browser drops a Secure cookie it received over plain HTTP, which would make sign-in impossible.
        var sessionCookie = Assert.Single(
            setup.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("walis_session=", StringComparison.Ordinal));
        Assert.DoesNotContain("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.False(setup.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task RequireHttpsRejectsRemotePlaintextApiAndMarksCookiesSecure()
    {
        await using var isolated = new WalisApiFactory
        {
            HostEnvironment = Environments.Production,
            RequireSetupCode = false,
            RequireHttps = true
        };
        var remote = isolated.CreateRemoteAnonymousClient();

        Assert.Equal(HttpStatusCode.UpgradeRequired, (await remote.GetAsync("/api/auth/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await remote.GetAsync("/health")).StatusCode);

        // Loopback stays available for maintenance, and what it hands out is still Secure.
        var setup = await isolated.CreateAnonymousClient().PostAsJsonAsync("/api/auth/setup", new
        {
            username = "strict-admin",
            displayName = "Strict Admin",
            password = Password
        });
        setup.EnsureSuccessStatusCode();
        Assert.Contains(
            "secure",
            Assert.Single(
                setup.Headers.GetValues("Set-Cookie"),
                value => value.StartsWith("walis_session=", StringComparison.Ordinal)),
            StringComparison.OrdinalIgnoreCase);
    }
}