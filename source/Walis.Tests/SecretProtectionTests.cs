using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Walis.Core.Backup;
using Walis.Core.Database;
using Walis.Core.Models;
using Walis.Core.Security;

namespace Walis.Tests;

/// <summary>
///     Secrets at rest: what the repositories write must be ciphertext, what they hand back must be the plaintext, and a value
///     written by an older version in plaintext must still be readable. Runs on every configured database like the other provider tests.
/// </summary>
[Collection("外部資料庫")]
public class SecretProtectionTests
{
    private static JobDraft Draft(string name)
    {
        return new JobDraft(name, "Walis", "說明", null, "main", Runner.LocalName, null, "echo hi", 30, null,
            TriggerMode.Manual, null, true, false, ScriptModes.Source, null);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task SecretEnvironmentVariablesAreStoredEncryptedAndReadBack(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var job = await database.Jobs.CreateJobAsync(Draft($"機密-{Guid.NewGuid():N}"));

        var secret = await database.Variables.CreateAsync(
            new EnvironmentVariableDraft("DEPLOY_TOKEN", "s3cr3t-token-value", true, job.Id));
        var plain = await database.Variables.CreateAsync(
            new EnvironmentVariableDraft("REGION", "ap-northeast-1", false, null));
        var tooShort = await database.Variables.CreateAsync(
            new EnvironmentVariableDraft("PIN", "123", true, job.Id));

        using var connection = database.Factory.Create();
        var storedSecret = await connection.ExecuteScalarAsync<string>(
            "SELECT Value FROM EnvironmentVariables WHERE Id = @Id;", new { secret.Id });
        var storedPlain = await connection.ExecuteScalarAsync<string>(
            "SELECT Value FROM EnvironmentVariables WHERE Id = @Id;", new { plain.Id });

        Assert.StartsWith(SecretCipher.Prefix, storedSecret);
        Assert.DoesNotContain("s3cr3t-token-value", storedSecret);
        Assert.Equal("ap-northeast-1", storedPlain);

        Assert.Equal("s3cr3t-token-value", secret.Value);
        Assert.Equal("s3cr3t-token-value", (await database.Variables.GetAsync(secret.Id))!.Value);
        Assert.Contains((await database.Variables.ListAsync()), variable =>
            variable.Id == secret.Id && variable.Value == "s3cr3t-token-value");

        var resolved = await database.Variables.ResolveForJobAsync(job.Id);
        Assert.Equal("s3cr3t-token-value", resolved["DEPLOY_TOKEN"]);
        Assert.Equal("ap-northeast-1", resolved["REGION"]);

        // The "at least four characters" rule still applies after decryption: a 3-character secret is neither injected nor masked.
        Assert.DoesNotContain("PIN", resolved.Keys);
        Assert.Equal(["s3cr3t-token-value"], await database.Variables.ListSecretValuesForJobAsync(job.Id));
        Assert.Equal(["s3cr3t-token-value"], await database.Variables.ListSecretValuesAsync());

        var updated = await database.Variables.UpdateAsync(tooShort.Id,
            new EnvironmentVariableDraft("PIN", "4567", true, job.Id));
        Assert.Equal("4567", updated!.Value);
        Assert.StartsWith(SecretCipher.Prefix, await connection.ExecuteScalarAsync<string>(
            "SELECT Value FROM EnvironmentVariables WHERE Id = @Id;", new { tooShort.Id }));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task AiProviderKeyIsStoredEncrypted(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var claude = (await database.Ai.ListProvidersAsync()).First();

        var updated = await database.Ai.UpdateProviderAsync(claude.Id,
            new AiProviderDraft("sk-ant-api03-test-key", claude.Model, null, true, true));

        using var connection = database.Factory.Create();
        var stored = await connection.ExecuteScalarAsync<string>(
            "SELECT ApiKey FROM AiProviders WHERE Id = @Id;", new { claude.Id });

        Assert.StartsWith(SecretCipher.Prefix, stored);
        Assert.DoesNotContain("sk-ant", stored);
        Assert.Equal("sk-ant-api03-test-key", updated!.ApiKey);
        Assert.Equal("sk-ant-api03-test-key", (await database.Ai.GetProviderAsync(claude.Id))!.ApiKey);
        Assert.Equal("sk-ant-api03-test-key", (await database.Ai.ResolveProviderAsync(null))!.ApiKey);

        // A null key means "keep": the stored ciphertext is re-written but still opens.
        await database.Ai.UpdateProviderAsync(claude.Id, new AiProviderDraft(null, claude.Model, null, true, true));
        Assert.Equal("sk-ant-api03-test-key", (await database.Ai.GetProviderAsync(claude.Id))!.ApiKey);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task SettingsSecretFieldsAreEncryptedFieldByField(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var settings = new WalisSettings
        {
            SiteName = "明文站台名稱",
            WebhookSecret = "webhook-secret-0123456789abcdefghijklmnop",
            NotificationSigningSecret = "signing-secret-0123456789abcdefghijklmnop",
            SmtpPassword = "smtp-password-value",
            BackupPassphrase = "backup-passphrase-value",
            BackupPostgresPassword = "pg-password-value",
            BackupTargets =
            [
                new BackupTarget
                {
                    Name = "雲端",
                    Provider = BackupProviders.S3,
                    Bucket = "bucket",
                    AccessKeyId = "AKIA-visible",
                    SecretAccessKey = "s3-secret-value",
                    ClientSecret = "oauth-client-secret",
                    RefreshToken = "oauth-refresh-token"
                }
            ]
        };

        var saved = await database.Settings.SaveAsync(settings);

        // The caller's instance is untouched: services keep using plaintext after saving.
        Assert.Equal("smtp-password-value", saved.SmtpPassword);
        Assert.Equal("s3-secret-value", saved.BackupTargets[0].SecretAccessKey);

        using var connection = database.Factory.Create();
        var payload = await connection.ExecuteScalarAsync<string>("SELECT Payload FROM Settings WHERE Id = 1;");

        // Non-ASCII is \u-escaped by the serializer, so the site name is checked on the parsed payload below.
        Assert.Contains("AKIA-visible", payload);
        foreach (var secret in new[]
                 {
                     "webhook-secret-", "signing-secret-", "smtp-password-value", "backup-passphrase-value",
                     "pg-password-value", "s3-secret-value", "oauth-client-secret", "oauth-refresh-token"
                 })
            Assert.DoesNotContain(secret, payload);

        var stored = JsonSerializer.Deserialize<WalisSettings>(payload!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.StartsWith(SecretCipher.Prefix, stored.WebhookSecret);
        Assert.StartsWith(SecretCipher.Prefix, stored.NotificationSigningSecret);
        Assert.StartsWith(SecretCipher.Prefix, stored.SmtpPassword);
        Assert.StartsWith(SecretCipher.Prefix, stored.BackupPassphrase);
        Assert.StartsWith(SecretCipher.Prefix, stored.BackupPostgresPassword);
        Assert.StartsWith(SecretCipher.Prefix, stored.BackupTargets[0].SecretAccessKey);
        Assert.StartsWith(SecretCipher.Prefix, stored.BackupTargets[0].ClientSecret);
        Assert.StartsWith(SecretCipher.Prefix, stored.BackupTargets[0].RefreshToken);
        Assert.Equal("AKIA-visible", stored.BackupTargets[0].AccessKeyId);
        Assert.Equal("明文站台名稱", stored.SiteName);

        var loaded = await database.Settings.GetAsync();
        Assert.Equal("webhook-secret-0123456789abcdefghijklmnop", loaded.WebhookSecret);
        Assert.Equal("signing-secret-0123456789abcdefghijklmnop", loaded.NotificationSigningSecret);
        Assert.Equal("smtp-password-value", loaded.SmtpPassword);
        Assert.Equal("backup-passphrase-value", loaded.BackupPassphrase);
        Assert.Equal("pg-password-value", loaded.BackupPostgresPassword);
        Assert.Equal("s3-secret-value", loaded.BackupTargets[0].SecretAccessKey);
        Assert.Equal("oauth-client-secret", loaded.BackupTargets[0].ClientSecret);
        Assert.Equal("oauth-refresh-token", loaded.BackupTargets[0].RefreshToken);
        Assert.Equal("明文站台名稱", loaded.SiteName);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task PasswordParametersAreEncryptedInBuilds(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var job = await database.Jobs.CreateJobAsync(Draft($"參數-{Guid.NewGuid():N}"));
        var secretJson = BuildParameters.SerializeValues(new Dictionary<string, string> { ["DEPLOY_PASSWORD"] = "p@ssw0rd-value" });

        var queued = await database.Jobs.TryQueueBuildsAsync(job.Id, "manual", Runner.LocalName,
            [new BuildQueueItem(ParametersJson: "{\"ENV\":\"prod\"}", SecretParametersJson: secretJson)]);
        var build = Assert.Single(queued!);

        Assert.Equal(secretJson, build.SecretParametersJson);

        using var connection = database.Factory.Create();
        var stored = await connection.ExecuteScalarAsync<string>(
            "SELECT SecretParametersJson FROM Builds WHERE Id = @Id;", new { build.Id });
        var storedPlain = await connection.ExecuteScalarAsync<string>(
            "SELECT ParametersJson FROM Builds WHERE Id = @Id;", new { build.Id });

        Assert.StartsWith(SecretCipher.Prefix, stored);
        Assert.DoesNotContain("p@ssw0rd", stored);
        Assert.Contains("prod", storedPlain);

        Assert.Equal(secretJson, (await database.Jobs.GetBuildAsync(build.Id))!.SecretParametersJson);
        Assert.Equal(secretJson,
            (await database.Jobs.ListBuildsAsync(new BuildQuery(null, null, job.Id, null, 10)))[0].SecretParametersJson);
        Assert.Equal(secretJson,
            (await database.Jobs.QueryBuildsAsync(new BuildQuery(null, null, job.Id, null, 10, null, 1, 10))).Items[0]
            .SecretParametersJson);
        Assert.Equal(secretJson, (await database.Jobs.ClaimNextLocalBuildAsync(Runner.LocalName, "節點一"))!.SecretParametersJson);
        Assert.Equal(secretJson, (await database.Jobs.ListRunningBuildsAsync())[0].SecretParametersJson);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task TotpSecretIsEncryptedAndEnrollmentStillWorks(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var user = await database.Security.CreateUserAsync(new UserDraft("otp-user", "OTP", "walis-password", UserRole.Admin));
        var secret = Totp.CreateSecret();

        Assert.True(await database.Security.BeginTotpEnrollmentAsync(user.Id, user.PasswordHash, secret));

        using var connection = database.Factory.Create();
        var stored = await connection.ExecuteScalarAsync<string>("SELECT TotpSecret FROM Users WHERE Id = @Id;", new { user.Id });
        Assert.StartsWith(SecretCipher.Prefix, stored);
        Assert.DoesNotContain(secret, stored);

        var enrolled = (await database.Security.GetUserAsync(user.Id))!;
        Assert.Equal(secret, enrolled.TotpSecret);
        Assert.Equal(secret, (await database.Security.FindUserAsync("otp-user"))!.TotpSecret);

        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Totp.PeriodSeconds;
        var code = Totp.Compute(secret, counter);

        // Enabling compares the stored secret: a different secret must not enable, the right one must.
        Assert.False(await database.Security.EnableTotpAsync(user.Id, user.PasswordHash, Totp.CreateSecret(), counter, Totp.CreateRecoveryCodes()));
        Assert.True(await database.Security.EnableTotpAsync(user.Id, user.PasswordHash, secret, counter, Totp.CreateRecoveryCodes()));
        Assert.True((await database.Security.GetUserAsync(user.Id))!.HasOtp);

        var session = await database.Security.CreateSessionAsync(user.Id, TimeSpan.FromHours(1));
        Assert.True((await database.Security.GetSessionAsync(session.Token))!.HasOtp);

        // The same time step is consumed once; the next step verifies again.
        var next = Totp.Compute(secret, counter + 1);
        Assert.True(await database.Security.ConsumeTotpCodeAsync(user.Id, next));
        Assert.False(await database.Security.ConsumeTotpCodeAsync(user.Id, next));
        Assert.False(await database.Security.ConsumeTotpCodeAsync(user.Id, code));

        // Re-issuing recovery codes keeps the (re-encrypted) secret readable.
        Assert.True(await database.Security.SetTotpAsync(user.Id, secret, enrolled.TotpEnabledAt ?? DateTime.UtcNow, Totp.CreateRecoveryCodes()));
        Assert.Equal(secret, (await database.Security.GetUserAsync(user.Id))!.TotpSecret);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task LegacyPlaintextValuesAreReadAsIs(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var job = await database.Jobs.CreateJobAsync(Draft($"舊資料-{Guid.NewGuid():N}"));
        var variableId = Guid.NewGuid();
        var claude = (await database.Ai.ListProvidersAsync()).First();

        using var connection = database.Factory.Create();
        var boolean = database.Factory.Dialect.Boolean(true);
        await connection.ExecuteAsync(
            $"INSERT INTO EnvironmentVariables (Id, Name, Value, IsSecret, JobId, UpdatedAt) VALUES (@Id, 'LEGACY', 'plain-legacy-value', {boolean}, @JobId, @Now);",
            new { Id = variableId, JobId = job.Id, Now = DateTime.UtcNow });
        await connection.ExecuteAsync("UPDATE AiProviders SET ApiKey = 'plain-legacy-key' WHERE Id = @Id;", new { claude.Id });
        await connection.ExecuteAsync(
            "UPDATE Settings SET Payload = @Payload WHERE Id = 1;",
            new { Payload = """{"siteName":"舊版","webhookSecret":"plain-legacy-webhook-secret-0123456789"}""" });

        Assert.Equal("plain-legacy-value", (await database.Variables.GetAsync(variableId))!.Value);
        Assert.Equal(["plain-legacy-value"], await database.Variables.ListSecretValuesForJobAsync(job.Id));
        Assert.Equal("plain-legacy-key", (await database.Ai.GetProviderAsync(claude.Id))!.ApiKey);
        Assert.Equal("plain-legacy-webhook-secret-0123456789", (await database.Settings.GetAsync()).WebhookSecret);

        // Nothing is undecryptable about plaintext; the scan stays clean and the next write turns it into ciphertext.
        var scanner = new StoredSecretScanner(database.Factory, database.Protector);
        Assert.Equal(0, (await scanner.ScanAsync()).Count);

        await database.Settings.SaveAsync(await database.Settings.GetAsync());
        Assert.StartsWith(SecretCipher.Prefix,
            JsonSerializer.Deserialize<WalisSettings>(
                (await connection.ExecuteScalarAsync<string>("SELECT Payload FROM Settings WHERE Id = 1;"))!,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!.WebhookSecret);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Available), MemberType = typeof(TestDatabases))]
    public async Task ScannerReportsAndClearsValuesTheKeyCannotRead(DatabaseProvider provider)
    {
        await using var database = await TemporaryDatabase.CreateAsync(provider);
        var foreign = new SecretProtector("another-machine-key-0123456789abcdefghij");
        var job = await database.Jobs.CreateJobAsync(Draft($"外來-{Guid.NewGuid():N}"));
        var claude = (await database.Ai.ListProvidersAsync()).First();
        var user = await database.Security.CreateUserAsync(new UserDraft("restored", "Restored", "walis-password", UserRole.Admin));
        var variable = await database.Variables.CreateAsync(new EnvironmentVariableDraft("GOOD", "readable-secret", true, job.Id));
        var foreignVariable = await database.Variables.CreateAsync(new EnvironmentVariableDraft("BAD", "placeholder", true, job.Id));
        var build = (await database.Jobs.TryQueueBuildsAsync(job.Id, "manual", Runner.LocalName,
            [new BuildQueueItem(SecretParametersJson: "{\"X\":\"y\"}")]))![0];
        await database.Settings.SaveAsync(new WalisSettings { SmtpPassword = "readable-smtp" });

        using var connection = database.Factory.Create();
        await connection.ExecuteAsync("UPDATE AiProviders SET ApiKey = @Key WHERE Id = @Id;",
            new { Key = foreign.Protect("foreign-key", SecretPurposes.AiProviderApiKey), claude.Id });
        await connection.ExecuteAsync("UPDATE EnvironmentVariables SET Value = @Value WHERE Id = @Id;",
            new { Value = foreign.Protect("foreign-value", SecretPurposes.EnvironmentVariableValue), foreignVariable.Id });
        await connection.ExecuteAsync("UPDATE Users SET TotpSecret = @Secret, TotpEnabledAt = @Now WHERE Id = @Id;",
            new { Secret = foreign.Protect("JBSWY3DPEHPK3PXP", SecretPurposes.UserTotpSecret), Now = DateTime.UtcNow, user.Id });
        await connection.ExecuteAsync("UPDATE Builds SET SecretParametersJson = @Json WHERE Id = @Id;",
            new { Json = foreign.Protect("{\"X\":\"y\"}", SecretPurposes.BuildSecretParameters), build.Id });
        var payload = JsonSerializer.Deserialize<WalisSettings>(
            (await connection.ExecuteScalarAsync<string>("SELECT Payload FROM Settings WHERE Id = 1;"))!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        payload.WebhookSecret = foreign.Protect("foreign-webhook-secret-0123456789abcdef", SecretPurposes.SettingsWebhookSecret);
        await connection.ExecuteAsync("UPDATE Settings SET Payload = @Payload WHERE Id = 1;",
            new { Payload = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });

        // Reads degrade to "unset" instead of handing ciphertext to callers.
        Assert.Null((await database.Ai.GetProviderAsync(claude.Id))!.ApiKey);
        Assert.Equal(string.Empty, (await database.Variables.GetAsync(foreignVariable.Id))!.Value);
        Assert.Equal(["readable-secret"], await database.Variables.ListSecretValuesForJobAsync(job.Id));
        Assert.Null((await database.Security.GetUserAsync(user.Id))!.TotpSecret);
        Assert.Null((await database.Jobs.GetBuildAsync(build.Id))!.SecretParametersJson);
        var settings = await database.Settings.GetAsync();
        Assert.Null(settings.WebhookSecret);
        Assert.Equal("readable-smtp", settings.SmtpPassword);

        var scanner = new StoredSecretScanner(database.Factory, database.Protector);
        var report = await scanner.ScanAsync();

        Assert.Equal(5, report.Count);
        Assert.Contains(report.Undecryptable, item => item.Table == "AiProviders" && item.Item == claude.Name);
        Assert.Contains(report.Undecryptable, item => item.Table == "EnvironmentVariables" && item.Item == "BAD");
        Assert.Contains(report.Undecryptable, item => item.Table == "Users" && item.Item == "restored");
        Assert.Contains(report.Undecryptable, item => item.Table == "Builds" && item.Item == build.Id.ToString());
        Assert.Contains(report.Undecryptable, item => item.Table == "Settings" && item.Item == "webhookSecret");

        var cleared = await scanner.ResetUndecryptableAsync();
        Assert.Equal(5, cleared.Count);
        Assert.Equal(0, (await scanner.ScanAsync()).Count);

        Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT ApiKey FROM AiProviders WHERE Id = @Id;", new { claude.Id }));
        Assert.Equal(string.Empty, await connection.ExecuteScalarAsync<string>("SELECT Value FROM EnvironmentVariables WHERE Id = @Id;", new { foreignVariable.Id }));
        Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT TotpSecret FROM Users WHERE Id = @Id;", new { user.Id }));
        Assert.False((await database.Security.GetUserAsync(user.Id))!.HasOtp);
        Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT SecretParametersJson FROM Builds WHERE Id = @Id;", new { build.Id }));

        // Readable values survive the reset untouched.
        Assert.Equal("readable-secret", (await database.Variables.GetAsync(variable.Id))!.Value);
        var after = await database.Settings.GetAsync();
        Assert.Null(after.WebhookSecret);
        Assert.Equal("readable-smtp", after.SmtpPassword);
    }
}

public class SecretKeyStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("walis-secret-key").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    [Fact]
    public void GeneratesKeyFileOnceAndReusesIt()
    {
        var first = SecretKeyStore.LoadOrCreate(null, _directory);
        var path = Path.Combine(_directory, SecretKeyStore.FileName);

        Assert.True(first.Source.Generated);
        Assert.Equal(path, first.Source.FilePath);
        Assert.True(File.Exists(path));
        Assert.True(first.Value.Length >= SecretKeyStore.MinimumConfiguredLength);

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        var second = SecretKeyStore.LoadOrCreate(null, _directory);
        Assert.False(second.Source.Generated);
        Assert.Equal(first.Value, second.Value);

        // Both keys encrypt and decrypt each other's output: the file is the single source of truth.
        var encrypted = new SecretProtector(first.Value).Protect("value", SecretPurposes.EnvironmentVariableValue);
        Assert.Equal("value", new SecretProtector(second.Value).Unprotect(encrypted, SecretPurposes.EnvironmentVariableValue));
    }

    [Fact]
    public void ConfiguredKeyWinsAndWritesNoFile()
    {
        var key = SecretKeyStore.LoadOrCreate(new string('k', 40), _directory);

        Assert.Equal(new string('k', 40), key.Value);
        Assert.Null(key.Source.FilePath);
        Assert.False(key.Source.Generated);
        Assert.Contains(SecretKeyStore.ConfigurationKey, key.Source.Description);
        Assert.False(File.Exists(Path.Combine(_directory, SecretKeyStore.FileName)));
    }

    [Fact]
    public void ShortConfiguredKeyIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => SecretKeyStore.LoadOrCreate("too-short", _directory));
    }

    [Fact]
    public void SymbolicLinkInPlaceOfTheKeyFileIsRejected()
    {
        if (OperatingSystem.IsWindows()) return;

        var elsewhere = Path.Combine(_directory, "elsewhere.txt");
        File.WriteAllText(elsewhere, new string('x', 48));
        File.CreateSymbolicLink(Path.Combine(_directory, SecretKeyStore.FileName), elsewhere);

        Assert.Throws<IOException>(() => SecretKeyStore.LoadOrCreate(null, _directory));
    }
}

public class SecretProtectorTests
{
    private const string Purpose = SecretPurposes.EnvironmentVariableValue;

    [Fact]
    public void ProtectIsIdempotentAndUnprotectPassesPlaintextThrough()
    {
        var protector = new SecretProtector(TemporaryDatabase.TestEncryptionKey);
        var encrypted = protector.Protect("value", Purpose)!;

        Assert.True(protector.IsProtected(encrypted));
        Assert.StartsWith(SecretCipher.Prefix, encrypted);
        Assert.Same(encrypted, protector.Protect(encrypted, Purpose));
        Assert.Null(protector.Protect(null, Purpose));
        Assert.Equal(string.Empty, protector.Protect(string.Empty, Purpose));
        Assert.Equal("value", protector.Unprotect(encrypted, Purpose));
        Assert.Equal("legacy", protector.Unprotect("legacy", Purpose));
        Assert.Null(protector.Unprotect(null, Purpose));
    }

    [Fact]
    public void UndecryptableValueReadsAsNullAndIsReported()
    {
        var protector = new SecretProtector(TemporaryDatabase.TestEncryptionKey);
        var foreign = new SecretProtector("some-other-key-0123456789abcdefghijklmnop").Protect("value", Purpose);

        Assert.Null(protector.Unprotect(foreign, Purpose));
        Assert.False(protector.TryUnprotect(foreign, Purpose, out _));
        Assert.False(protector.TryUnprotect(SecretCipher.Prefix + "not-base64!!", Purpose, out _));
        Assert.True(protector.TryUnprotect("plain", Purpose, out var plain));
        Assert.Equal("plain", plain);
    }

    [Fact]
    public void CiphertextMovedToAnotherColumnDoesNotDecrypt()
    {
        // Without the purpose bound in, database write access alone could copy an AI key into a job's secret variable.
        var protector = new SecretProtector(TemporaryDatabase.TestEncryptionKey);
        var aiKey = protector.Protect("sk-ant-secret", SecretPurposes.AiProviderApiKey);

        Assert.Equal("sk-ant-secret", protector.Unprotect(aiKey, SecretPurposes.AiProviderApiKey));
        Assert.Null(protector.Unprotect(aiKey, SecretPurposes.EnvironmentVariableValue));
    }

    [Fact]
    public void LegacyV1CiphertextIsReadAndRewrittenAsV2()
    {
        var protector = new SecretProtector(TemporaryDatabase.TestEncryptionKey);
        var v1 = LegacyEncrypt(TemporaryDatabase.TestEncryptionKey, "old-value");

        Assert.StartsWith(SecretCipher.LegacyPrefix, v1);
        Assert.Equal("old-value", protector.Unprotect(v1, Purpose));

        var rewritten = protector.Protect(v1, Purpose)!;
        Assert.StartsWith(SecretCipher.Prefix, rewritten);
        Assert.Equal("old-value", protector.Unprotect(rewritten, Purpose));
    }

    [Fact]
    public void PlaintextThatOnlyLooksLikeCiphertextIsEncrypted()
    {
        var protector = new SecretProtector(TemporaryDatabase.TestEncryptionKey);
        var lookalike = SecretCipher.Prefix + "pasted-by-a-user";

        var stored = protector.Protect(lookalike, Purpose)!;

        Assert.NotEqual(lookalike, stored);
        Assert.Equal(lookalike, protector.Unprotect(stored, Purpose));
    }

    /// <summary>The v1 writer, reproduced: same key schedule and payload layout as v2, but no associated data.</summary>
    private static string LegacyEncrypt(string key, string plaintext)
    {
        var stretched = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(key),
            "walis:secret-cipher:v1"u8.ToArray(), 100_000, System.Security.Cryptography.HashAlgorithmName.SHA512, 32);
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var valueKey = System.Security.Cryptography.HKDF.DeriveKey(System.Security.Cryptography.HashAlgorithmName.SHA256, stretched, 32, salt, "walis:secret-value"u8.ToArray());
        var bytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var cipherText = new byte[bytes.Length];
        var tag = new byte[16];
        using (var aes = new System.Security.Cryptography.AesGcm(valueKey, 16)) aes.Encrypt(nonce, bytes, cipherText, tag);
        return SecretCipher.LegacyPrefix + Convert.ToBase64String([.. salt, .. nonce, .. tag, .. cipherText]);
    }
}

public class SecretSettingsApiTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    [Fact]
    public async Task AdminSeesKeySourceAndCanResetUndecryptableSecrets()
    {
        var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/settings/secrets");
        Assert.Contains(SecretKeyStore.ConfigurationKey, status.GetProperty("keySource").GetString());
        Assert.Equal(0, status.GetProperty("count").GetInt32());

        var reset = await client.PostAsync("/api/settings/secrets/reset-undecryptable", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(0, (await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task SlackAndWebhookTargetsAreEncryptedAtRestAndMaskedInTheApi()
    {
        var client = factory.CreateClient();
        const string url = "https://hooks.slack.example/services/T000/B000/secret-token";

        var created = await client.PostAsJsonAsync("/api/notifications/channels", new
        {
            kind = "slack", name = $"slack-{Guid.NewGuid():N}", target = url, isEnabled = false
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal("https://hooks.slack.example/***", body.GetProperty("target").GetString());
        Assert.True(body.GetProperty("targetMasked").GetBoolean());

        using (var connection = factory.Services.GetRequiredService<IDbConnectionFactory>().Create())
        {
            var stored = await connection.ExecuteScalarAsync<string>(
                "SELECT Target FROM NotificationChannels WHERE Id = @Id;", new { Id = id });
            Assert.StartsWith(SecretCipher.Prefix, stored);
            Assert.DoesNotContain("secret-token", stored);
        }

        // Sending the mask back keeps the stored URL; the repository still hands the real one to the dispatcher.
        var updated = await client.PutAsJsonAsync($"/api/notifications/channels/{id}", new
        {
            kind = "slack", name = "renamed", target = "https://hooks.slack.example/***", isEnabled = false
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var repository = factory.Services.GetRequiredService<Walis.Core.Repositories.INotificationRepository>();
        Assert.Equal(url, (await repository.GetChannelAsync(id))!.Target);

        var list = await client.GetStringAsync("/api/notifications/channels");
        Assert.DoesNotContain("secret-token", list);
    }

}
