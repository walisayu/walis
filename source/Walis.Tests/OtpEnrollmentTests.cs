using Walis.Core.Models;
using Walis.Core.Security;

namespace Walis.Tests;

public class OtpEnrollmentTests
{
    [Fact]
    public async Task OldSecretCannotBeEnabledAfterSetupChanged()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var security = database.Security;
        var user = await security.CreateUserAsync(new UserDraft("otp", "OTP", "test-password", UserRole.Admin));
        var oldSecret = Totp.CreateSecret();
        var newSecret = Totp.CreateSecret();
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Totp.PeriodSeconds;
        Assert.True(await security.BeginTotpEnrollmentAsync(user.Id, user.PasswordHash, oldSecret));
        Assert.True(await security.BeginTotpEnrollmentAsync(user.Id, user.PasswordHash, newSecret));

        Assert.False(await security.EnableTotpAsync(user.Id, user.PasswordHash, oldSecret, counter, []));
        Assert.False((await security.GetUserAsync(user.Id))!.HasOtp);
        Assert.True(await security.EnableTotpAsync(user.Id, user.PasswordHash, newSecret, counter, []));
        Assert.False(await security.BeginTotpEnrollmentAsync(user.Id, user.PasswordHash, oldSecret));
        Assert.Equal(newSecret, (await security.GetUserAsync(user.Id))!.TotpSecret);
    }

    [Fact]
    public async Task ConcurrentEnableKeepsOneBackupCodeSetAndCodeCannotBeReplayed()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var security = database.Security;
        var user = await security.CreateUserAsync(new UserDraft("otp", "OTP", "test-password", UserRole.Admin));
        var secret = Totp.CreateSecret();
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Totp.PeriodSeconds;
        Assert.True(await security.BeginTotpEnrollmentAsync(user.Id, user.PasswordHash, secret));
        var codes = new[] { Totp.CreateRecoveryCodes(1), Totp.CreateRecoveryCodes(1) };

        var results = await Task.WhenAll(codes.Select(batch => Task.Run(() =>
            security.EnableTotpAsync(user.Id, user.PasswordHash, secret, counter, batch))));

        Assert.Single(results, success => success);
        Assert.False(await security.ConsumeTotpCodeAsync(user.Id, Totp.Compute(secret, counter)));
        for (var index = 0; index < results.Length; index++)
            Assert.Equal(results[index], await security.ConsumeRecoveryCodeAsync(user.Id, codes[index][0]));
    }

    [Fact]
    public async Task OldRequestCannotSetOrEnableOtpAfterPasswordReset()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var security = database.Security;
        var user = await security.CreateUserAsync(new UserDraft("otp", "OTP", "test-password", UserRole.Admin));
        var secret = Totp.CreateSecret();
        Assert.True(await security.BeginTotpEnrollmentAsync(user.Id, user.PasswordHash, secret));
        Assert.True(await security.ChangePasswordAsync(user.Id, "replacement-password"));

        Assert.False(await security.BeginTotpEnrollmentAsync(user.Id, user.PasswordHash, Totp.CreateSecret()));
        Assert.False(await security.EnableTotpAsync(user.Id, user.PasswordHash, secret, 1, []));
        Assert.False((await security.GetUserAsync(user.Id))!.HasOtp);
    }
}