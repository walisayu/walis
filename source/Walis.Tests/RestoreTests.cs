using Microsoft.Extensions.Logging.Abstractions;
using Walis.Api.Services;
using Walis.Core.Backup;
using Walis.Core.Database;
using Walis.Core.Models;

namespace Walis.Tests;

public class RestoreTests
{
    private static JobDraft Draft(string name)
    {
        return new JobDraft(name, "Walis", "說明", null, "main", Runner.LocalName, null, "echo hi", 30, null,
            TriggerMode.Manual, null, true, false, ScriptModes.Source, null);
    }

    private static RestoreService CreateRestore(
        TemporaryDatabase database,
        string workspace,
        CorsOriginStore? corsOrigins = null)
    {
        return new RestoreService(
            database.Factory,
            database.Jobs,
            database.Leases,
            new DatabaseInitializer(database.Factory),
            database.Settings,
            corsOrigins ?? new CorsOriginStore(),
            new McpAccessStore(),
            new DataDirectory(Path.Combine(workspace, "data")),
            NullLogger<RestoreService>.Instance);
    }

    private static async Task<string> PackAsync(
        string workspace,
        string dumpPath,
        string? passphrase,
        string? artifactPath = null)
    {
        var archivePath = Path.Combine(workspace, "backup.tar.gz");

        await using (var archive = new BackupArchive(archivePath))
        {
            await archive.AddFileAsync(dumpPath, DatabaseDump.EntryName);

            if (artifactPath is not null)
                await archive.AddFileAsync(artifactPath, "walis/artifacts/example/result.txt");
        }

        if (passphrase is null) return archivePath;

        var encrypted = Path.Combine(workspace, "backup.tar.gz.enc");
        await BackupCipher.EncryptAsync(archivePath, encrypted, passphrase);
        return encrypted;
    }

    [Theory]
    [InlineData("null")]
    [InlineData("https://user:password@example.test")]
    [InlineData("https://example.test/path")]
    public void CorsRuntimeStoreItselfRejectsNonOriginSettings(string configured)
    {
        var origins = new CorsOriginStore();

        origins.Update(configured);

        Assert.False(origins.IsAllowed(configured));
        Assert.True(origins.IsAllowed("http://127.0.0.1:11124"));
    }

    [Fact]
    public async Task ArtifactsAreRestoredAsPrivateFilesAfterSafeExtraction()
    {
        var workspace = Directory.CreateTempSubdirectory("walis-restore").FullName;

        try
        {
            await using var database = await TemporaryDatabase.CreateAsync();
            await database.Jobs.CreateJobAsync(Draft("含產出檔的任務"));

            var dumpPath = Path.Combine(workspace, DatabaseDump.FileName);
            await using (var stream = File.Create(dumpPath))
            {
                await new DatabaseDump(database.Factory).ExportAsync(stream);
            }

            var artifactPath = Path.Combine(workspace, "result.txt");
            await File.WriteAllTextAsync(artifactPath, "artifact contents");
            var archivePath = await PackAsync(workspace, dumpPath, "產出檔測試密語", artifactPath);

            var restore = CreateRestore(database, workspace);

            var result = await restore.RestoreAsync(
                archivePath,
                "產出檔測試密語",
                true,
                false);
            var restored = Path.Combine(workspace, "data", "artifacts", "example", "result.txt");

            Assert.Equal(1, result.Artifacts);
            Assert.Equal("artifact contents", await File.ReadAllTextAsync(restored));

            if (!OperatingSystem.IsWindows())
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(restored));
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public async Task RestoringFromEncryptedBackupOverwritesExistingData()
    {
        var workspace = Directory.CreateTempSubdirectory("walis-restore").FullName;

        try
        {
            await using var database = await TemporaryDatabase.CreateAsync();
            var job = await database.Jobs.CreateJobAsync(Draft("備份時就有的任務"));

            var dumpPath = Path.Combine(workspace, DatabaseDump.FileName);
            await using (var stream = File.Create(dumpPath))
            {
                await new DatabaseDump(database.Factory).ExportAsync(stream);
            }

            var archivePath = await PackAsync(workspace, dumpPath, "通關密語");

            // A job created after the backup must be gone after the restore.
            var later = await database.Jobs.CreateJobAsync(Draft("備份之後才建立的任務"));

            var restore = CreateRestore(database, workspace);

            var result = await restore.RestoreAsync(archivePath, "通關密語", true, false);

            Assert.True(result.Rows > 0);
            Assert.NotNull(await database.Jobs.GetJobAsync(job.Id));
            Assert.Null(await database.Jobs.GetJobAsync(later.Id));
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public async Task RestoringSettingsRefreshesThisNodesCorsOriginsImmediately()
    {
        var workspace = Directory.CreateTempSubdirectory("walis-restore").FullName;

        try
        {
            await using var database = await TemporaryDatabase.CreateAsync();
            await database.Settings.SaveAsync(new WalisSettings
            {
                CorsOrigins = "https://restored.example"
            });

            var dumpPath = Path.Combine(workspace, DatabaseDump.FileName);
            await using (var stream = File.Create(dumpPath))
            {
                await new DatabaseDump(database.Factory).ExportAsync(stream);
            }

            var archivePath = await PackAsync(workspace, dumpPath, "CORS 還原測試密語");
            await database.Settings.SaveAsync(new WalisSettings
            {
                CorsOrigins = "https://current.example"
            });

            var corsOrigins = new CorsOriginStore();
            corsOrigins.Update("https://current.example");
            var restore = CreateRestore(database, workspace, corsOrigins);

            await restore.RestoreAsync(
                archivePath,
                "CORS 還原測試密語",
                false,
                false);

            Assert.True(corsOrigins.IsAllowed("https://restored.example"));
            Assert.False(corsOrigins.IsAllowed("https://current.example"));
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public async Task RestoredSettingsAreNormalizedAndRestoredAuditRowsAreMarked()
    {
        var workspace = Directory.CreateTempSubdirectory("walis-restore").FullName;

        try
        {
            await using var database = await TemporaryDatabase.CreateAsync();
            var plantedId = Guid.NewGuid();
            await database.Audit.AppendAsync(new AuditEntry
            {
                Id = plantedId,
                OccurredAt = DateTime.UtcNow.AddYears(1),
                Actor = "someone-else",
                Action = AuditAction.Delete,
                EntityType = "job",
                Summary = "刪除任務 正式部署"
            });

            // A crafted archive: retention below the floor that SaveAsync and PUT /api/settings enforce.
            var current = await database.Settings.GetAsync();
            current.AuditRetentionDays = 1;
            using (var connection = database.Factory.Create())
                await Dapper.SqlMapper.ExecuteAsync(connection, "UPDATE Settings SET Payload = @Payload WHERE Id = 1;",
                    new { Payload = System.Text.Json.JsonSerializer.Serialize(current,
                        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) });

            var dumpPath = Path.Combine(workspace, DatabaseDump.FileName);
            await using (var stream = File.Create(dumpPath))
            {
                await new DatabaseDump(database.Factory).ExportAsync(stream);
            }

            // The planted row exists only in the archive, so the restore merges it in.
            using (var connection = database.Factory.Create())
                await Dapper.SqlMapper.ExecuteAsync(connection, "DELETE FROM AuditLogs WHERE Id = @Id;", new { Id = plantedId });

            var archivePath = await PackAsync(workspace, dumpPath, "稽核還原測試密語");
            await CreateRestore(database, workspace).RestoreAsync(archivePath, "稽核還原測試密語", false, false);

            Assert.True((await database.Settings.GetAsync()).AuditRetentionDays >= 30);

            var restored = (await database.Audit.ListAsync(500)).Single(entry => entry.Id == plantedId);
            Assert.StartsWith(DatabaseDump.RestoredAuditMarker, restored.Summary);
            Assert.True(restored.OccurredAt <= DateTime.UtcNow);
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public async Task DataIsUntouchedWhenPassphraseIsWrong()
    {
        var workspace = Directory.CreateTempSubdirectory("walis-restore").FullName;

        try
        {
            await using var database = await TemporaryDatabase.CreateAsync();
            var job = await database.Jobs.CreateJobAsync(Draft("原本的任務"));

            var dumpPath = Path.Combine(workspace, DatabaseDump.FileName);
            await using (var stream = File.Create(dumpPath))
            {
                await new DatabaseDump(database.Factory).ExportAsync(stream);
            }

            var archivePath = await PackAsync(workspace, dumpPath, "正確密語");

            var restore = CreateRestore(database, workspace);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                restore.RestoreAsync(archivePath, "錯的密語", false, false));

            Assert.NotNull(await database.Jobs.GetJobAsync(job.Id));
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public async Task RestoreIsRefusedByDefaultWhileBuildsRun()
    {
        var workspace = Directory.CreateTempSubdirectory("walis-restore").FullName;

        try
        {
            await using var database = await TemporaryDatabase.CreateAsync();
            var job = await database.Jobs.CreateJobAsync(Draft("執行中的任務"));
            var build = await database.Jobs.QueueBuildAsync(job.Id, "manual", Runner.LocalName);
            await database.Jobs.MarkBuildRunningAsync(build.Id, Runner.LocalName, "節點一");

            var dumpPath = Path.Combine(workspace, DatabaseDump.FileName);
            await using (var stream = File.Create(dumpPath))
            {
                await new DatabaseDump(database.Factory).ExportAsync(stream);
            }

            var archivePath = await PackAsync(workspace, dumpPath, "強制還原測試密語");

            var restore = CreateRestore(database, workspace);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                restore.RestoreAsync(archivePath, "強制還原測試密語", false, false));

            // Forcing a bypass would let an old runner write back after the restore, so now it must fail closed too.
            var forced = await Assert.ThrowsAsync<InvalidOperationException>(() => restore.RestoreAsync(
                archivePath,
                "強制還原測試密語",
                false,
                true));
            Assert.Contains("不再允許強制繞過", forced.Message);
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }
}