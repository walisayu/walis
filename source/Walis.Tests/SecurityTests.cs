using Microsoft.Extensions.DependencyInjection;
using Dapper;
using Walis.Api.Services;
using Walis.Core.Database;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void SamePasswordHashesDifferentlyButAlwaysVerifies()
    {
        var first = PasswordHasher.Hash("walis-password");
        var second = PasswordHasher.Hash("walis-password");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("walis-password", first));
        Assert.True(PasswordHasher.Verify("walis-password", second));
    }

    [Fact]
    public void VerificationFailsWhenPasswordDiffers()
    {
        var hash = PasswordHasher.Hash("walis-password");

        Assert.False(PasswordHasher.Verify("wrong-password", hash));
        Assert.False(PasswordHasher.Verify("walis-password", null));
        Assert.False(PasswordHasher.Verify("walis-password", "不是雜湊"));
        Assert.False(PasswordHasher.Verify(
            "walis-password",
            $"pbkdf2$999999999${Convert.ToBase64String(new byte[16])}${Convert.ToBase64String(new byte[32])}"));
    }

    [Fact]
    public void GeneratedTokenIsUrlSafe()
    {
        var token = PasswordHasher.CreateToken();

        Assert.True(token.Length >= 32);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }
}

public class SecurityRepositoryTests
{
    [Fact]
    public async Task NewDatabaseHasNoAccounts()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        Assert.False(await database.Security.HasAnyUserAsync());
    }

    [Fact]
    public async Task CreatedAccountCanBeFoundByUserName()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var created =
            await database.Security.CreateUserAsync(new UserDraft("leo", "Leo", "walis-password", UserRole.Admin));

        Assert.True(await database.Security.HasAnyUserAsync());
        Assert.Equal("Leo", (await database.Security.FindUserAsync("leo"))!.DisplayName);
        Assert.True(PasswordHasher.Verify("walis-password", created.PasswordHash));
    }

    [Fact]
    public async Task UserNameMatchingIsCaseInsensitive()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await database.Security.CreateUserAsync(new UserDraft("Leo", "Leo", "walis-password", UserRole.Admin));

        Assert.NotNull(await database.Security.FindUserAsync("leo"));
        Assert.NotNull(await database.Security.FindUserAsync("LEO"));
    }

    [Fact]
    public async Task SignInCreatesQueryableSession()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var user = await database.Security.CreateUserAsync(
            new UserDraft("leo", "Leo", "walis-password", UserRole.Admin));

        var session = await database.Security.CreateSessionAsync(user.Id, TimeSpan.FromHours(1));

        Assert.Equal("leo", session.Username);
        Assert.NotNull(await database.Security.GetSessionAsync(session.Token));
        Assert.NotNull((await database.Security.FindUserAsync("leo"))!.LastLoginAt);
    }

    [Fact]
    public async Task SessionsAndOtpChallengesAreStoredOnlyAsHashes()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var user = await database.Security.CreateUserAsync(
            new UserDraft("leo", "Leo", "walis-password", UserRole.Admin));
        var session = await database.Security.CreateSessionAsync(user.Id, TimeSpan.FromHours(1));
        var challenge = await database.Security.CreateOtpChallengeAsync(user.Id, TimeSpan.FromMinutes(5));

        using var connection = database.Factory.Create();
        var storedSession = await connection.ExecuteScalarAsync<string>("SELECT Token FROM Sessions;");
        var storedChallenge = await connection.ExecuteScalarAsync<string>("SELECT Token FROM OtpChallenges;");

        Assert.NotNull(storedSession);
        Assert.NotNull(storedChallenge);
        Assert.NotEqual(session.Token, storedSession);
        Assert.NotEqual(challenge, storedChallenge);
        Assert.Equal(64, storedSession.Length);
        Assert.Equal(64, storedChallenge.Length);
        Assert.Equal(session.Token, (await database.Security.GetSessionAsync(session.Token))!.Token);
        Assert.Equal(user.Id, await database.Security.ConsumeOtpChallengeAsync(challenge));
    }

    [Fact]
    public async Task SameTotpTimeStepIsConsumedAtomicallyOnce()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var user = await database.Security.CreateUserAsync(
            new UserDraft("leo", "Leo", "walis-password", UserRole.Admin));
        var secret = Totp.CreateSecret();
        Assert.True(await database.Security.SetTotpAsync(user.Id, secret, DateTime.UtcNow, []));
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Totp.PeriodSeconds;
        var code = Totp.Compute(secret, counter);

        var results = await Task.WhenAll(
            database.Security.ConsumeTotpCodeAsync(user.Id, code),
            database.Security.ConsumeTotpCodeAsync(user.Id, code));

        Assert.Single(results, consumed => consumed);
        Assert.False(await database.Security.ConsumeTotpCodeAsync(user.Id, code));
    }

    [Fact]
    public async Task ExpiredSessionIsNotFoundAndIsCleanedUp()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var user = await database.Security.CreateUserAsync(
            new UserDraft("leo", "Leo", "walis-password", UserRole.Admin));

        var session = await database.Security.CreateSessionAsync(user.Id, TimeSpan.FromSeconds(-1));

        Assert.Null(await database.Security.GetSessionAsync(session.Token));
        Assert.Equal(1, await database.Security.PurgeExpiredSessionsAsync());
    }

    [Fact]
    public async Task SignOutDeletesSession()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var user = await database.Security.CreateUserAsync(
            new UserDraft("leo", "Leo", "walis-password", UserRole.Admin));
        var session = await database.Security.CreateSessionAsync(user.Id, TimeSpan.FromHours(1));

        Assert.True(await database.Security.DeleteSessionAsync(session.Token));
        Assert.Null(await database.Security.GetSessionAsync(session.Token));
    }

    [Fact]
    public async Task SelfPasswordChangeDoesNotOverwriteAdminResetMadeAfterVerification()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var user = await database.Security.CreateUserAsync(
            new UserDraft("leo", "Leo", "original-password", UserRole.Viewer));
        var verifiedHash = user.PasswordHash;

        Assert.True(await database.Security.ChangePasswordAsync(user.Id, "admin-reset-password"));
        Assert.False(await database.Security.ChangePasswordIfCurrentAsync(
            user.Id,
            verifiedHash,
            "attacker-overwrite"));

        var current = await database.Security.GetUserAsync(user.Id);
        Assert.True(PasswordHasher.Verify("admin-reset-password", current!.PasswordHash));
        Assert.False(PasswordHasher.Verify("attacker-overwrite", current.PasswordHash));
    }

    [Fact]
    public async Task DeletingAccountAlsoRemovesItsSessions()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var user = await database.Security.CreateUserAsync(
            new UserDraft("leo", "Leo", "walis-password", UserRole.Admin));
        var session = await database.Security.CreateSessionAsync(user.Id, TimeSpan.FromHours(1));

        Assert.True(await database.Security.DeleteUserAsync(user.Id));
        Assert.Null(await database.Security.GetSessionAsync(session.Token));
    }

    [Fact]
    public async Task ConcurrentAdminDemotionStillKeepsOneEnabledAdmin()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var first = await database.Security.CreateUserAsync(
            new UserDraft("admin-a", "Admin A", "walis-password", UserRole.Admin));
        var second = await database.Security.CreateUserAsync(
            new UserDraft("admin-b", "Admin B", "walis-password", UserRole.Admin));

        var results = await Task.WhenAll(
            database.Security.UpdateProfilePreservingAdminAsync(
                first.Id,
                new UserProfileDraft(first.DisplayName, UserRole.Operator, false)),
            database.Security.UpdateProfilePreservingAdminAsync(
                second.Id,
                new UserProfileDraft(second.DisplayName, UserRole.Operator, false)));

        Assert.Single(results, result => result.Status == UserMutationStatus.Success);
        Assert.Single(results, result => result.Status == UserMutationStatus.LastActiveAdmin);
        Assert.Equal(1, await database.Security.CountAdminsAsync());
    }

    [Fact]
    public async Task SafeDeleteProtectsLastEnabledAdmin()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var admin = await database.Security.CreateUserAsync(
            new UserDraft("admin", "Admin", "walis-password", UserRole.Admin));
        await database.Security.CreateUserAsync(
            new UserDraft("viewer", "Viewer", "walis-password", UserRole.Viewer));

        var result = await database.Security.DeleteUserPreservingAdminAsync(admin.Id);

        Assert.Equal(UserMutationStatus.LastActiveAdmin, result.Status);
        Assert.NotNull(await database.Security.GetUserAsync(admin.Id));
    }

    [Fact]
    public async Task UpgradingExistingAccountDatabaseBackfillsFirstAdminClaimPermanently()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await database.Security.CreateUserAsync(
            new UserDraft("legacy-admin", "Legacy Admin", "walis-password", UserRole.Admin));

        await new DatabaseInitializer(database.Factory).InitializeAsync();

        using var connection = database.Factory.Create();
        var owner = await connection.ExecuteScalarAsync<string?>(
            "SELECT Owner FROM SecurityClaims WHERE Name = 'initial-admin';");
        Assert.Equal("database-upgrade", owner);
    }

    [Fact]
    public async Task SignInLockoutStateIsKeptInDatabaseAcrossInstances()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var first = new SignInThrottle(TimeProvider.System, database.Factory);

        for (var attempt = 0; attempt < SignInThrottle.AccountLimit; attempt++)
            Assert.Null(await first.TryBeginAttemptAsync("leo", "10.0.0.1"));

        var restarted = new SignInThrottle(TimeProvider.System, database.Factory);
        Assert.NotNull(await restarted.TryBeginAttemptAsync("LEO", "10.0.0.2"));
    }

    [Fact]
    public async Task AFullThrottleTableMakesRoomInsteadOfLockingEveryoneOut()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var past = DateTime.UtcNow.AddMinutes(-1);

        // What a flood of random names from rotating sources leaves behind: 50,000 buckets, none of them locked.
        using (var connection = database.Factory.Create())
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            await connection.ExecuteAsync(
                "INSERT INTO SignInRateLimits (BucketKey, Failures, FirstFailureAt, LockedUntil) VALUES (@Key, 1, @At, @At);",
                Enumerable.Range(0, 50_000).Select(index => new { Key = $"account:flood-{index}", At = past }),
                transaction);
            transaction.Commit();
        }

        var throttle = new SignInThrottle(TimeProvider.System, database.Factory);

        Assert.Null(await throttle.TryBeginAttemptAsync("real-user", "198.51.100.7"));
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData(null, "unknown")]
    public void PerSourceLimitsCountIpv6PerSlash64(string? address, string expected)
    {
        Assert.Equal(expected, ClientAddressKey.For(address));
    }

    [Fact]
    public async Task HighRiskAdminActionsAskForThePasswordAgain()
    {
        await using var factory = new WalisApiFactory();
        var client = factory.CreateAnonymousClient();
        var setup = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, "/api/auth/setup?bearer=true",
            new { username = "stepup", displayName = "Step Up", password = "step-up-password-123" });
        setup.EnsureSuccessStatusCode();
        var token = (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<System.Text.Json.JsonElement>(setup.Content))
            .GetProperty("token").GetString()!;

        async Task<HttpResponseMessage> CreateKey(string? password)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/api-keys")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { name = $"k-{Guid.NewGuid():N}"[..12], role = "viewer" })
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (password is not null) request.Headers.Add(Reauthentication.HeaderName, password);
            return await client.SendAsync(request);
        }

        var missing = await CreateKey(null);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Contains(Reauthentication.Code, await missing.Content.ReadAsStringAsync());
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await CreateKey("not-my-password")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Created, (await CreateKey("step-up-password-123")).StatusCode);

        // Turning auditing off is the same kind of action.
        var settings = factory.Services.GetRequiredService<ISettingsRepository>();
        var current = await settings.GetAsync();
        current.AuditLog = false;
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = System.Net.Http.Json.JsonContent.Create(current) };
        put.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.SendAsync(put)).StatusCode);
        Assert.True((await settings.GetAsync()).AuditLog);
    }

    [Fact]
    public async Task OlderPasswordHashesAreRewrittenOnTheNextSignIn()
    {
        await using var factory = new WalisApiFactory();
        var security = factory.Services.GetRequiredService<ISecurityRepository>();
        var user = await security.CreateUserAsync(new UserDraft("legacy-hash", "Legacy", "long-enough-password", UserRole.Viewer));
        using (var connection = factory.Services.GetRequiredService<IDbConnectionFactory>().Create())
            await connection.ExecuteAsync("UPDATE Users SET PasswordHash = @Hash WHERE Id = @Id;",
                new { Hash = PasswordHasher.Hash("long-enough-password", 210_000), user.Id });

        var client = factory.CreateAnonymousClient();
        var login = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, "/api/auth/login",
            new { username = "legacy-hash", password = "long-enough-password" });

        Assert.Equal(System.Net.HttpStatusCode.OK, login.StatusCode);
        var stored = (await security.FindUserAsync("legacy-hash"))!.PasswordHash;
        Assert.StartsWith($"pbkdf2${PasswordHasher.Iterations}$", stored);
        Assert.True(PasswordHasher.Verify("long-enough-password", stored));
    }

    [Fact]
    public async Task SignInEndpointsHaveTheirOwnPerSourceCeiling()
    {
        await using var factory = new WalisApiFactory { AuthRateLimitPerMinute = 3 };
        var client = factory.CreateAnonymousClient();

        var statuses = new List<System.Net.HttpStatusCode>();
        for (var attempt = 0; attempt < 4; attempt++)
            statuses.Add((await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, "/api/auth/login",
                new { username = $"nobody-{attempt}", password = "wrong-password-1" })).StatusCode);

        Assert.All(statuses.Take(3), status => Assert.NotEqual(System.Net.HttpStatusCode.TooManyRequests, status));
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, statuses[3]);
    }

    [Fact]
    public async Task RotatingAddressesInsideOneIpv6Slash64ShareTheSourceBucket()
    {
        var throttle = new SignInThrottle(TimeProvider.System);

        for (var attempt = 0; attempt < SignInThrottle.AddressLimit; attempt++)
            Assert.Null(await throttle.TryBeginAttemptAsync($"name-{attempt}", $"2001:db8:5:6::{attempt + 1:x}"));

        Assert.NotNull(await throttle.TryBeginAttemptAsync("another-name", "2001:db8:5:6:ffff::1"));
    }
}

public class AuditRepositoryTests
{
    [Fact]
    public async Task AuditRecordsAreOrderedNewestFirst()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.Audit.AppendAsync(new AuditEntry
        {
            OccurredAt = DateTime.UtcNow.AddMinutes(-10),
            Actor = "leo",
            Action = AuditAction.Create,
            EntityType = "job",
            Summary = "建立任務 A"
        });

        await database.Audit.AppendAsync(new AuditEntry
        {
            Actor = "leo",
            Action = AuditAction.Delete,
            EntityType = "job",
            Summary = "刪除任務 B"
        });

        var entries = await database.Audit.ListAsync(10);

        Assert.Equal(2, entries.Count);
        Assert.Equal("刪除任務 B", entries[0].Summary);
    }

    [Fact]
    public async Task AuditRecordsPastRetentionAreCleanedUp()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.Audit.AppendAsync(new AuditEntry
        {
            OccurredAt = DateTime.UtcNow.AddDays(-40),
            Actor = "leo",
            Action = AuditAction.Update,
            EntityType = "settings",
            Summary = "很久以前"
        });

        await database.Audit.AppendAsync(new AuditEntry
        {
            Actor = "leo",
            Action = AuditAction.Update,
            EntityType = "settings",
            Summary = "剛剛"
        });

        Assert.Equal(1, await database.Audit.PurgeAsync(30));
        Assert.Single(await database.Audit.ListAsync(10));
    }
}

public class WorkspaceCleanerTests
{
    [Fact]
    public void DriveRootCannotBeCleaned()
    {
        Assert.False(WorkspaceCleaner.IsSafe(Path.GetPathRoot(Path.GetTempPath()) ?? "/", out _));
    }

    [Fact]
    public void HomeDirectoryCannotBeCleaned()
    {
        Assert.False(WorkspaceCleaner.IsSafe(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            out var reason));
        Assert.Contains("家目錄", reason);
    }

    [Fact]
    public void BlankPathCannotBeCleaned()
    {
        Assert.False(WorkspaceCleaner.IsSafe("   ", out _));
    }

    [Fact]
    public void DeepEnoughPathIsCleanedAndContentsRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"walis-clean-{Guid.NewGuid():N}", "workspace");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "nested", "b.txt"), "b");

        Assert.True(WorkspaceCleaner.IsSafe(root, out _));

        var report = WorkspaceCleaner.Clean(root);

        Assert.Contains("移除 2 個項目", report);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));

        Directory.Delete(Path.GetDirectoryName(root)!, true);
    }
}

public class ArtifactCollectorTests : IDisposable
{
    private readonly string _destination = Path.Combine(Path.GetTempPath(), $"walis-collected-{Guid.NewGuid():N}");
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"walis-artifacts-{Guid.NewGuid():N}");

    public ArtifactCollectorTests()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "dist", "assets"));
        File.WriteAllText(Path.Combine(_workspace, "dist", "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(_workspace, "dist", "assets", "app.js"), "console.log(1)");
        File.WriteAllText(Path.Combine(_workspace, "report.xml"), "<report/>");
        File.WriteAllText(Path.Combine(_workspace, "notes.txt"), "略過");
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _workspace, _destination })
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void DirectoryIsSavedWholeWithRelativePaths()
    {
        var collected = ArtifactCollector.Collect(_workspace, "dist", _destination, out var report);

        Assert.Equal(2, collected.Count);
        Assert.Contains(collected, artifact => artifact.RelativePath == "assets/app.js");
        Assert.Contains("已保存 2 個產出檔案", report);
        Assert.True(File.Exists(Path.Combine(_destination, "index.html")));
    }

    [Fact]
    public void SupportsWildcardFileNames()
    {
        var collected = ArtifactCollector.Collect(_workspace, "*.xml", _destination, out _);

        Assert.Single(collected);
        Assert.Equal("report.xml", collected[0].RelativePath);
    }

    [Fact]
    public void UnixArtifactDirectoryAndFilesAreServiceAccountOnly()
    {
        if (OperatingSystem.IsWindows()) return;

        ArtifactCollector.Collect(_workspace, "dist", _destination, out _);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(_destination));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.Combine(_destination, "assets")));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(_destination, "assets", "app.js")));
    }

    [Fact]
    public void SafeFileOpenDoesNotFollowSymbolicLinks()
    {
        if (OperatingSystem.IsWindows()) return;

        var outside = Path.Combine(_workspace, "outside-secret.txt");
        var link = Path.Combine(_workspace, "artifact-link.txt");
        File.WriteAllText(outside, "secret");
        File.CreateSymbolicLink(link, outside);

        Assert.False(SafeRegularFile.TryOpenRead(link, out var stream));
        Assert.Null(stream);
    }

    [Fact]
    public void MissingFileIsReportedWithoutCreatingDirectory()
    {
        var collected = ArtifactCollector.Collect(_workspace, "build/*.zip", _destination, out var report);

        Assert.Empty(collected);
        Assert.Contains("找不到", report);
    }

    [Fact]
    public void ArtifactPathsCannotEscapeWorkspace()
    {
        var collected = ArtifactCollector.Collect(_workspace, "../secret", _destination, out var report);

        Assert.Empty(collected);
        Assert.Contains("超出工作目錄", report);
    }

    [Fact]
    public void CollectionIsRefusedWhenWorkspaceContainsProtectedDataOrArtifactDestination()
    {
        var protectedData = Directory.CreateDirectory(Path.Combine(_workspace, "service-data"));
        File.WriteAllText(Path.Combine(protectedData.FullName, "walis.db"), "secret");

        var fromProtectedWorkspace = ArtifactCollector.Collect(
            _workspace,
            "service-data",
            _destination,
            out var protectedReport,
            [protectedData.FullName]);

        Assert.Empty(fromProtectedWorkspace);
        Assert.Contains("受保護", protectedReport);

        var nestedDestination = Path.Combine(_workspace, "artifact-staging");
        var recursive = ArtifactCollector.Collect(
            _workspace,
            "dist",
            nestedDestination,
            out var destinationReport);

        Assert.Empty(recursive);
        Assert.Contains("保存位置", destinationReport);
        Assert.False(Directory.Exists(nestedDestination));
    }

    [Fact]
    public void DoesNothingWithoutArtifactPaths()
    {
        Assert.Empty(ArtifactCollector.Collect(_workspace, null, _destination, out var report));
        Assert.Equal(string.Empty, report);
    }

    [Fact]
    public void SingleFileOverTotalLimitIsSkippedAtPrecheckWithoutPartialFile()
    {
        var huge = Path.Combine(_workspace, "huge.bin");

        using (var stream = File.Create(huge))
        {
            stream.SetLength(ArtifactCollector.MaximumTotalBytes + 1);
        }

        var collected = ArtifactCollector.Collect(_workspace, "huge.bin", _destination, out var report);

        Assert.Empty(collected);
        Assert.Contains("超過上限", report);
        Assert.False(File.Exists(Path.Combine(_destination, "huge.bin")));
        Assert.Empty(Directory.EnumerateFiles(_destination, ".walis-artifact-*.tmp", SearchOption.AllDirectories));
    }
}