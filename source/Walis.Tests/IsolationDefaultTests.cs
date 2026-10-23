using Walis.Core.Security;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Walis.Api.Endpoints;
using Walis.Api.Services;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

/// <summary>
///     The isolation default of a fresh database, the first administrator's acknowledgement of unisolated builds, and the
///     removal of deployment secrets from the process environment once configuration has captured them.
/// </summary>
public class IsolationDefaultTests
{
    private const string Password = "isolation-default-password";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("http://localhost:11120", false)]
    [InlineData("http://127.0.0.1:11120;https://[::1]:11121", false)]
    [InlineData("http://0.0.0.0:11120", true)]
    [InlineData("http://*:11120", true)]
    [InlineData("http://+:11120", true)]
    [InlineData("http://[::]:11120", true)]
    [InlineData("http://walis.example.internal:11120", true)]
    [InlineData("http://127.0.0.1:11120;http://10.0.0.5:11120", true)]
    public void NonLoopbackListeningAddressesAreRecognized(string? urls, bool nonLoopback)
    {
        Assert.Equal(nonLoopback, BuildIsolationPolicy.ListensOnNonLoopback(urls));
    }

    [Theory]
    [InlineData(true, "http://0.0.0.0:11120", false)]
    [InlineData(true, "http://127.0.0.1:11120", true)]
    [InlineData(true, null, true)]
    [InlineData(false, "http://0.0.0.0:11120", true)]
    public void OnlyProductionOnANonLoopbackAddressStartsFailClosed(bool production, string? urls, bool allowed)
    {
        Assert.Equal(allowed, BuildIsolationPolicy.AllowsUnisolatedBuildsByDefault(production, urls));
    }

    [Theory]
    [InlineData("Production", "http://0.0.0.0:11120", false)]
    [InlineData("Production", "http://127.0.0.1:11120", true)]
    [InlineData("Test", "http://0.0.0.0:11120", true)]
    public async Task FreshDatabaseGetsItsIsolationDefaultFromEnvironmentAndAddress(
        string environment,
        string urls,
        bool allowed)
    {
        await using var factory = new WalisApiFactory { HostEnvironment = environment, ListeningUrls = urls };
        var settings = factory.Services.GetRequiredService<ISettingsRepository>();

        Assert.Equal(allowed, (await settings.GetAsync()).AllowUnisolatedBuilds);

        var payload = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/settings", Json);
        Assert.Equal(allowed, payload.GetProperty("settings").GetProperty("allowUnisolatedBuilds").GetBoolean());
    }

    [Fact]
    public async Task StoredSettingsAreNotChangedByTheStartupDefault()
    {
        // A database that already stored "allowed" keeps it when the initializer runs again with a fail-closed default.
        await using var database = await TemporaryDatabase.CreateAsync();
        var stored = await database.Settings.GetAsync();
        stored.AllowUnisolatedBuilds = true;
        stored.SiteName = "既有的設定";
        await database.Settings.SaveAsync(stored);

        await new Walis.Core.Database.DatabaseInitializer(database.Factory)
        {
            InitialSettings = new WalisSettings { AllowUnisolatedBuilds = false }
        }.InitializeAsync();

        var after = await database.Settings.GetAsync();
        Assert.True(after.AllowUnisolatedBuilds);
        Assert.Equal("既有的設定", after.SiteName);
    }

    [Fact]
    public async Task FreshDatabaseStoresTheInitializersDefault()
    {
        Walis.Core.Database.DapperConfiguration.Register();
        var path = Path.Combine(Path.GetTempPath(), $"walis-isolation-{Guid.NewGuid():N}.db");
        var factory = new Walis.Core.Database.SqliteConnectionFactory($"Data Source={path}");

        try
        {
            await new Walis.Core.Database.DatabaseInitializer(factory)
            {
                InitialSettings = new WalisSettings { AllowUnisolatedBuilds = false }
            }.InitializeAsync();

            Assert.False((await new SettingsRepository(factory, new SecretProtector(TemporaryDatabase.TestEncryptionKey)).GetAsync()).AllowUnisolatedBuilds);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstAdministratorAnswersTheIsolationExplanation(bool accept)
    {
        await using var factory = new WalisApiFactory();
        var anonymous = factory.CreateAnonymousClient();

        var before = await anonymous.GetFromJsonAsync<JsonElement>("/api/auth/status", Json);
        Assert.True(before.GetProperty("unisolatedBuildsAllowed").GetBoolean());

        var setup = await anonymous.PostAsJsonAsync("/api/auth/setup", new
        {
            username = "first-admin",
            displayName = "First Admin",
            password = Password,
            allowUnisolatedBuilds = accept
        });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);

        var settings = await factory.Services.GetRequiredService<ISettingsRepository>().GetAsync();
        Assert.Equal(accept, settings.AllowUnisolatedBuilds);
        Assert.Equal(accept, settings.UnisolatedBuildsAcknowledged);
        Assert.Equal(accept ? "first-admin" : null, settings.UnisolatedBuildsAcknowledgedBy);
        Assert.Equal(accept, settings.UnisolatedBuildsAcknowledgedAt is not null);

        // The explanation belongs to the setup screen only; once an administrator exists the status says nothing about it.
        var after = await anonymous.GetFromJsonAsync<JsonElement>("/api/auth/status", Json);
        Assert.False(after.GetProperty("unisolatedBuildsAllowed").GetBoolean());
    }

    [Fact]
    public async Task SettingsPageAcknowledgementIsStampedByTheServer()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateClient();
        var payload = await client.GetFromJsonAsync<JsonElement>("/api/settings", Json);
        var settings = payload.GetProperty("settings").Deserialize<WalisSettings>(Json)!;

        Assert.False(settings.UnisolatedBuildsAcknowledged);

        // Whatever the client claims about who accepted and when is ignored.
        settings.UnisolatedBuildsAcknowledged = true;
        settings.UnisolatedBuildsAcknowledgedBy = "forged";
        settings.UnisolatedBuildsAcknowledgedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var saved = await client.PutAsJsonAsync("/api/settings", settings, Json);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());

        var stored = await factory.Services.GetRequiredService<ISettingsRepository>().GetAsync();
        Assert.True(stored.UnisolatedBuildsAcknowledged);
        Assert.Equal("test-admin", stored.UnisolatedBuildsAcknowledgedBy);
        Assert.True(stored.UnisolatedBuildsAcknowledgedAt > DateTime.UtcNow.AddMinutes(-5));

        // Saving again keeps the original record; turning the switch off clears it.
        var stamp = stored.UnisolatedBuildsAcknowledgedAt;
        var again = new WalisSettings { UnisolatedBuildsAcknowledged = true };
        SettingsEndpoints.StampIsolationAcknowledgement(again, stored, "someone-else");
        Assert.Equal(stamp, again.UnisolatedBuildsAcknowledgedAt);
        Assert.Equal("test-admin", again.UnisolatedBuildsAcknowledgedBy);

        var cleared = new WalisSettings { UnisolatedBuildsAcknowledged = false };
        SettingsEndpoints.StampIsolationAcknowledgement(cleared, stored, "test-admin");
        Assert.Null(cleared.UnisolatedBuildsAcknowledgedAt);
        Assert.Null(cleared.UnisolatedBuildsAcknowledgedBy);
    }

    [Fact]
    public void ConfigurationKeepsAnEnvironmentValueAfterItIsRemovedFromTheProcess()
    {
        // Program.cs clears the secret variables right after builder.Build(); this is the property that makes that safe:
        // the environment provider copies the values when it loads, so IConfiguration still has them afterwards.
        var name = $"WALIS_TEST_CAPTURED_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "captured-value");

        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            Environment.SetEnvironmentVariable(name, null);

            Assert.Equal("captured-value", configuration[name]);
            Assert.Null(Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void SensitiveEnvironmentIsClearedByName()
    {
        Assert.Contains("Security__BootstrapToken", SensitiveEnvironment.Names);
        Assert.Contains("ConnectionStrings__Walis", SensitiveEnvironment.Names);
        Assert.Contains("Security__EncryptionKey", SensitiveEnvironment.Names);

        Environment.SetEnvironmentVariable("ConnectionStrings__Walis", "Data Source=/usr/local/walis/data/walis.db");

        try
        {
            SensitiveEnvironment.Clear();
            Assert.Null(Environment.GetEnvironmentVariable("ConnectionStrings__Walis"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Walis", null);
        }
    }

    [Fact]
    public void PgPassLineEscapesColonsAndBackslashes()
    {
        Assert.Equal(@"*:*:*:*:pa\:ss\\word", PgPassFile.Line(@"pa:ss\word"));
        Assert.Equal("*:*:*:*:plain", PgPassFile.Line("plain"));
    }

    [Fact]
    public void PgPassFileIsPrivateAndRejectsLineBreaks()
    {
        var directory = Directory.CreateTempSubdirectory("walis-pgpass");

        try
        {
            var path = PgPassFile.Create(directory.FullName, "secret:one");
            Assert.Equal("*:*:*:*:secret\\:one\n", File.ReadAllText(path));

            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

            Assert.Throws<InvalidOperationException>(() => PgPassFile.Create(directory.FullName, "two\nlines"));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
