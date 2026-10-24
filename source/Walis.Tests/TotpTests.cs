using Walis.Api.Services;
using Walis.Core.Models;
using Walis.Core.Security;

namespace Walis.Tests;

public class TotpTests
{
    // RFC 6238 test vector: the Base32 form of the ASCII secret "12345678901234567890".
    private const string ReferenceSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void MatchesRfc6238TestVectors(long unixTime, string expected)
    {
        Assert.Equal(expected, Totp.Compute(ReferenceSecret, unixTime / Totp.PeriodSeconds));
    }

    [Fact]
    public void GeneratedSecretIsLongEnoughAndBase32()
    {
        var secret = Totp.CreateSecret();

        Assert.Equal(32, secret.Length);
        Assert.All(secret, character => Assert.Contains(character, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"));
    }

    [Fact]
    public void Base32EncodesAndDecodesRoundTrip()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 250, 251, 252 };

        Assert.Equal(data, Totp.FromBase32(Totp.ToBase32(data)));
    }

    [Fact]
    public void CorrectCodePasses()
    {
        var secret = Totp.CreateSecret();
        var now = DateTimeOffset.UtcNow;
        var code = Totp.Compute(secret, now.ToUnixTimeSeconds() / Totp.PeriodSeconds);

        Assert.True(Totp.Verify(secret, code, now: now));
    }

    [Fact]
    public void AllowsOneWindowEitherSideForClockDrift()
    {
        var secret = Totp.CreateSecret();
        var now = DateTimeOffset.UtcNow;
        var counter = now.ToUnixTimeSeconds() / Totp.PeriodSeconds;

        Assert.True(Totp.Verify(secret, Totp.Compute(secret, counter - 1), now: now));
        Assert.True(Totp.Verify(secret, Totp.Compute(secret, counter + 1), now: now));
        Assert.False(Totp.Verify(secret, Totp.Compute(secret, counter + 3), now: now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("abcdef")]
    public void MalformedCodesAlwaysFail(string? code)
    {
        Assert.False(Totp.Verify(Totp.CreateSecret(), code));
    }

    [Fact]
    public void AlwaysFailsWithoutSecret()
    {
        Assert.False(Totp.Verify(null, "123456"));
    }

    [Fact]
    public void OtpauthUrlContainsParametersAuthenticatorsNeed()
    {
        var uri = Totp.BuildUri("Walis 建置管理中心", "leo", ReferenceSecret);

        Assert.StartsWith("otpauth://totp/", uri);
        Assert.Contains($"secret={ReferenceSecret}", uri);
        Assert.Contains("digits=6", uri);
        Assert.Contains("period=30", uri);
        Assert.Contains("algorithm=SHA1", uri);
    }

    [Fact]
    public void BackupCodesAreEightWithConsistentFormat()
    {
        var codes = Totp.CreateRecoveryCodes();

        Assert.Equal(8, codes.Count);
        Assert.Equal(8, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^[a-z2-7]{4}-[a-z2-7]{4}$", code));
    }

    [Fact]
    public void BackupCodeMatchingIgnoresCaseAndHyphens()
    {
        Assert.Equal("abcd1234", Totp.NormalizeRecoveryCode("ABCD-1234"));
        Assert.Equal("abcd1234", Totp.NormalizeRecoveryCode(" abcd 1234 "));
    }
}

public class RoleAuthorizationTests
{
    [Theory]
    [InlineData(UserRole.Admin, "GET", "/api/jobs/", true)]
    [InlineData(UserRole.Admin, "PUT", "/api/settings/", true)]
    [InlineData(UserRole.Admin, "POST", "/api/accounts", true)]
    [InlineData(UserRole.Operator, "GET", "/api/settings/", true)]
    [InlineData(UserRole.Operator, "POST", "/api/jobs/", true)]
    [InlineData(UserRole.Operator, "POST", "/api/jobs/1/builds", true)]
    [InlineData(UserRole.Operator, "PUT", "/api/settings/", false)]
    [InlineData(UserRole.Operator, "POST", "/api/accounts", false)]
    [InlineData(UserRole.Operator, "PUT", "/api/i18n/translations", false)]
    // Self-service security endpoints are allowed by AuthenticationMiddleware's session-only allowlist,
    // not by the general role path allowlist; this keeps the pure function from being misused as an unauthenticated authorization check.
    [InlineData(UserRole.Operator, "POST", "/api/auth/password", false)]
    [InlineData(UserRole.Viewer, "GET", "/api/builds/", true)]
    [InlineData(UserRole.Viewer, "POST", "/api/jobs/1/builds", false)]
    [InlineData(UserRole.Viewer, "DELETE", "/api/jobs/1", false)]
    public void RoleDecidesAllowedOperations(string role, string method, string path, bool allowed)
    {
        Assert.Equal(allowed, AuthenticationMiddleware.IsAllowed(role, method, path));
    }
}

public class OtpRepositoryTests
{
    private static UserDraft Draft()
    {
        return new UserDraft("leo", "Leo", "walis-password", UserRole.Admin);
    }

    [Fact]
    public async Task NewAccountHasOtpDisabledByDefault()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var user = await database.Security.CreateUserAsync(Draft());

        Assert.False(user.HasOtp);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.False(user.IsDisabled);
    }

    [Fact]
    public async Task StoringSecretAloneDoesNotEnableOtp()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var user = await database.Security.CreateUserAsync(Draft());

        await database.Security.SetTotpAsync(user.Id, Totp.CreateSecret(), null, null);

        Assert.False((await database.Security.GetUserAsync(user.Id))!.HasOtp);
    }

    [Fact]
    public async Task OtpIsEnabledOnlyAfterEnabledTimeIsSet()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var user = await database.Security.CreateUserAsync(Draft());

        await database.Security.SetTotpAsync(user.Id, Totp.CreateSecret(), DateTime.UtcNow, Totp.CreateRecoveryCodes());

        Assert.True((await database.Security.GetUserAsync(user.Id))!.HasOtp);
    }

    [Fact]
    public async Task BackupCodeCanBeUsedOnlyOnce()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var user = await database.Security.CreateUserAsync(Draft());
        var codes = Totp.CreateRecoveryCodes();

        await database.Security.SetTotpAsync(user.Id, Totp.CreateSecret(), DateTime.UtcNow, codes);

        Assert.True(await database.Security.ConsumeRecoveryCodeAsync(user.Id, codes[0]));
        Assert.False(await database.Security.ConsumeRecoveryCodeAsync(user.Id, codes[0]));
        Assert.True(await database.Security.ConsumeRecoveryCodeAsync(user.Id, codes[1].ToUpperInvariant()));
    }

    [Fact]
    public async Task UnknownBackupCodeIsRejected()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var user = await database.Security.CreateUserAsync(Draft());
        await database.Security.SetTotpAsync(user.Id, Totp.CreateSecret(), DateTime.UtcNow, Totp.CreateRecoveryCodes());

        Assert.False(await database.Security.ConsumeRecoveryCodeAsync(user.Id, "0000-0000"));
    }

    [Fact]
    public async Task SessionsAreInvalidatedAfterAccountIsDisabled()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var user = await database.Security.CreateUserAsync(Draft());
        var session = await database.Security.CreateSessionAsync(user.Id, TimeSpan.FromHours(1));

        await database.Security.UpdateProfileAsync(user.Id, new UserProfileDraft("Leo", UserRole.Admin, true));

        Assert.Null(await database.Security.GetSessionAsync(session.Token));
    }

    [Fact]
    public async Task CountsOnlyEnabledAdmins()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var admin = await database.Security.CreateUserAsync(Draft());
        await database.Security.CreateUserAsync(new UserDraft("bob", "Bob", "walis-password", UserRole.Operator));

        Assert.Equal(1, await database.Security.CountAdminsAsync());

        await database.Security.UpdateProfileAsync(admin.Id, new UserProfileDraft("Leo", UserRole.Admin, true));

        Assert.Equal(0, await database.Security.CountAdminsAsync());
    }
}