using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Walis.Api.Services;
using Walis.Core.Backup;
using Walis.Core.Models;

namespace Walis.Tests;

public class BackupCipherTests
{
    [Fact]
    public async Task EncryptedBackupRestoresWithSamePassphrase()
    {
        var directory = Directory.CreateTempSubdirectory("walis-cipher");

        try
        {
            var source = Path.Combine(directory.FullName, "plain.txt");
            var encrypted = Path.Combine(directory.FullName, "cipher.bin");
            var restored = Path.Combine(directory.FullName, "restored.txt");
            var content = new string('資', 5_000);

            await File.WriteAllTextAsync(source, content);
            await BackupCipher.EncryptAsync(source, encrypted, "通關密語");
            await BackupCipher.DecryptAsync(encrypted, restored, "通關密語");

            Assert.Equal(content, await File.ReadAllTextAsync(restored));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task EncryptedFileUsesVersionedWalisBkpHeader()
    {
        var directory = Directory.CreateTempSubdirectory("walis-cipher");

        try
        {
            var source = Path.Combine(directory.FullName, "plain.txt");
            var encrypted = Path.Combine(directory.FullName, "cipher.bin");

            await File.WriteAllTextAsync(source, "內容");
            await BackupCipher.EncryptAsync(source, encrypted, "通關密語");

            var header = new byte[8];
            await using var stream = File.OpenRead(encrypted);
            await stream.ReadExactlyAsync(header);

            Assert.Equal("WALISBKP", Encoding.ASCII.GetString(header));
            Assert.Equal(
                BackupCipher.BackupEncryptionFormat.AuthenticatedV1,
                await BackupCipher.GetFormatAsync(encrypted));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task RestoreFailsWithWrongPassphrase()
    {
        var directory = Directory.CreateTempSubdirectory("walis-cipher");

        try
        {
            var source = Path.Combine(directory.FullName, "plain.txt");
            var encrypted = Path.Combine(directory.FullName, "cipher.bin");
            var restored = Path.Combine(directory.FullName, "restored.txt");

            await File.WriteAllTextAsync(source, "內容");
            await BackupCipher.EncryptAsync(source, encrypted, "正確的密語");

            await Assert.ThrowsAnyAsync<CryptographicException>(() =>
                BackupCipher.DecryptAsync(encrypted, restored, "錯誤的密語"));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TamperedOrTruncatedEncryptedFileLeavesNoPartialOutput(bool truncate)
    {
        var directory = Directory.CreateTempSubdirectory("walis-cipher");

        try
        {
            var source = Path.Combine(directory.FullName, "plain.bin");
            var encrypted = Path.Combine(directory.FullName, "cipher.bin");
            var restored = Path.Combine(directory.FullName, "restored.bin");
            await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(BackupCipher.ChunkSize + 37));
            await BackupCipher.EncryptAsync(source, encrypted, "通關密語");

            var bytes = await File.ReadAllBytesAsync(encrypted);

            if (truncate)
                Array.Resize(ref bytes, bytes.Length - 7);
            else
                bytes[bytes.Length / 2] ^= 0x40;

            await File.WriteAllBytesAsync(encrypted, bytes);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                BackupCipher.DecryptAuthenticatedBackupAsync(encrypted, restored, "通關密語"));
            Assert.False(File.Exists(restored));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task DecryptionExceedingLimitLeavesNoPartialFile()
    {
        var directory = Directory.CreateTempSubdirectory("walis-cipher-limit");

        try
        {
            var source = Path.Combine(directory.FullName, "plain.bin");
            var encrypted = Path.Combine(directory.FullName, "cipher.bin");
            var restored = Path.Combine(directory.FullName, "restored.bin");
            await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(4097));
            await BackupCipher.EncryptAsync(source, encrypted, "通關密語");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                BackupCipher.DecryptAuthenticatedBackupAsync(
                    encrypted,
                    restored,
                    "通關密語",
                    4096));

            Assert.Contains("解密後", exception.Message);
            Assert.False(File.Exists(restored));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}

public class BackupArchiveTests
{
    [Fact]
    public async Task PacksFilesAndDirectoriesAndSkipsSymbolicLinks()
    {
        var root = Directory.CreateTempSubdirectory("walis-archive");

        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root.FullName, "source"));
            await File.WriteAllTextAsync(Path.Combine(source.FullName, "a.txt"), "A");
            var nested = Directory.CreateDirectory(Path.Combine(source.FullName, "nested"));
            await File.WriteAllTextAsync(Path.Combine(nested.FullName, "b.txt"), "B");

            // A symbolic link pointing at itself: following it would loop forever.
            Directory.CreateSymbolicLink(Path.Combine(source.FullName, "loop"), source.FullName);

            var single = Path.Combine(root.FullName, "single.txt");
            await File.WriteAllTextAsync(single, "S");

            // The output is deliberately placed inside a packed source; the implementation must skip itself or it recursively reads the growing archive.
            var archivePath = Path.Combine(source.FullName, "out.tar.gz");

            await using (var archive = new BackupArchive(archivePath))
            {
                await archive.AddFileAsync(single, "walis/single.txt");
                var count = await archive.AddDirectoryAsync(source.FullName, "paths/source");
                Assert.Equal(2, count);
            }

            var entries = ReadEntryNames(archivePath);

            Assert.Contains("walis/single.txt", entries);
            Assert.Contains("paths/source/a.txt", entries);
            Assert.Contains("paths/source/nested/b.txt", entries);
            Assert.DoesNotContain(entries, name => name.Contains("loop"));
            Assert.DoesNotContain(entries, name => name.EndsWith("out.tar.gz", StringComparison.Ordinal));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task ExcludedFilesStayOutOfTheArchive()
    {
        var root = Directory.CreateTempSubdirectory("walis-archive-exclude");

        try
        {
            // An extra backup path that covers the data directory must not carry the master key along with the ciphertext.
            var data = Directory.CreateDirectory(Path.Combine(root.FullName, "data"));
            await File.WriteAllTextAsync(Path.Combine(data.FullName, "secret.key"), "master-key");
            await File.WriteAllTextAsync(Path.Combine(data.FullName, "notes.txt"), "keep");
            var archivePath = Path.Combine(root.FullName, "out.tar.gz");

            await using (var archive = new BackupArchive(archivePath))
            {
                var count = await archive.AddDirectoryAsync(data.FullName, "paths/data", excludedFiles:
                    [Path.Combine(data.FullName, "secret.key")]);
                Assert.Equal(1, count);
            }

            var entries = ReadEntryNames(archivePath);
            Assert.Contains("paths/data/notes.txt", entries);
            Assert.DoesNotContain(entries, name => name.EndsWith("secret.key", StringComparison.Ordinal));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void HeaderCannotDemandUnboundedKeyDerivationWork()
    {
        Assert.True(BackupCipher.MaximumAcceptedIterations >= BackupCipher.Iterations);
        Assert.True(BackupCipher.MaximumAcceptedIterations <= BackupCipher.Iterations * 4);
    }

    [Fact]
    public async Task CreationAppliesSameSingleFileAndTotalQuotasAsRestore()
    {
        var root = Directory.CreateTempSubdirectory("walis-archive-limit");

        try
        {
            var first = Path.Combine(root.FullName, "first.txt");
            var second = Path.Combine(root.FullName, "second.txt");
            await File.WriteAllTextAsync(first, "123");
            await File.WriteAllTextAsync(second, "456");
            var limits = new SafeTarExtractionLimits(
                2,
                4,
                5,
                128,
                1024);

            await using var archive = new BackupArchive(
                Path.Combine(root.FullName, "out.tar.gz"),
                limits);
            await archive.AddFileAsync(first, "paths/first.txt");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                archive.AddFileAsync(second, "paths/second.txt"));
            Assert.Contains("合計", exception.Message);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task SingleFileEntryAlsoRejectsSymbolicLinks()
    {
        var root = Directory.CreateTempSubdirectory("walis-archive-link");

        try
        {
            var target = Path.Combine(root.FullName, "target.txt");
            var link = Path.Combine(root.FullName, "link.txt");
            await File.WriteAllTextAsync(target, "secret");
            File.CreateSymbolicLink(link, target);

            await using var archive = new BackupArchive(Path.Combine(root.FullName, "out.tar.gz"));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                archive.AddFileAsync(link, "paths/link.txt"));
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static List<string> ReadEntryNames(string archivePath)
    {
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        var names = new List<string>();

        while (reader.GetNextEntry() is { } entry) names.Add(entry.Name);

        return names;
    }
}

public class PrivateBackupFileSystemTests
{
    [Fact]
    public async Task PrivateStagingWorkspaceAndFilesAreOwnerOnlyOnUnix()
    {
        var data = Directory.CreateTempSubdirectory("walis-private-data");

        try
        {
            var workspace = PrivateFileSystem.CreateTemporaryDirectory(data.FullName, "test");
            var path = Path.Combine(workspace, "secret.txt");

            await using (var stream = PrivateFileSystem.CreatePrivateFile(path))
            {
                await stream.WriteAsync("secret"u8.ToArray());
            }

            Assert.StartsWith(Path.Combine(data.FullName, ".private-temp"), workspace, StringComparison.Ordinal);

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(workspace));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));
            }
        }
        finally
        {
            data.Delete(true);
        }
    }

    [Fact]
    public async Task ExistingDataDirectoryAndDatabaseFileAlsoGetTightenedUnixPermissions()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("walis-private-existing");

        try
        {
            var data = Path.Combine(root.FullName, "data");
            Directory.CreateDirectory(data);
            File.SetUnixFileMode(
                data,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var database = Path.Combine(data, "walis.db");
            await File.WriteAllTextAsync(database, "database");
            File.SetUnixFileMode(
                database,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            PrivateFileSystem.EnsurePrivateDirectory(data);
            PrivateFileSystem.RestrictFile(database);

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(data));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(database));
        }
        finally
        {
            root.Delete(true);
        }
    }
}

public class SafeTarExtractorTests
{
    [Fact]
    public async Task ExtractsOnlyDatabaseAndArtifactsAndIgnoresOtherBackupContent()
    {
        var directory = Directory.CreateTempSubdirectory("walis-safe-tar");

        try
        {
            using var archive = CreateArchive(
                Regular(DatabaseDump.EntryName, "database"),
                Regular("walis/artifacts/build/result.txt", "artifact"),
                Regular("postgres/production.dump", "not-needed"));

            await SafeTarExtractor.ExtractForRestoreAsync(
                archive,
                directory.FullName,
                true);

            Assert.Equal(
                "database",
                await File.ReadAllTextAsync(Path.Combine(directory.FullName, "walis", DatabaseDump.FileName)));
            Assert.Equal(
                "artifact",
                await File.ReadAllTextAsync(
                    Path.Combine(directory.FullName, "walis", "artifacts", "build", "result.txt")));
            Assert.False(File.Exists(Path.Combine(directory.FullName, "postgres", "production.dump")));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task RejectsSymbolicLinksAndOtherNonRegularFiles()
    {
        var directory = Directory.CreateTempSubdirectory("walis-safe-tar");

        try
        {
            var link = new PaxTarEntry(TarEntryType.SymbolicLink, "walis/artifacts/leak")
            {
                LinkName = "../walis-data.jsonl"
            };
            using var archive = CreateArchive(link);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                SafeTarExtractor.ExtractForRestoreAsync(
                    archive,
                    directory.FullName,
                    true));

            Assert.Contains("不允許", exception.Message);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("walis\\artifacts\\windows.txt")]
    public async Task RejectsTraversalAbsoluteAndCrossPlatformAmbiguousPaths(string entryName)
    {
        var directory = Directory.CreateTempSubdirectory("walis-safe-tar");

        try
        {
            using var archive = CreateArchive(Regular(entryName, "bad"));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SafeTarExtractor.ExtractForRestoreAsync(
                    archive,
                    directory.FullName,
                    true));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task SingleFileAndTotalLimitsStopCompressionBombsBeforeWriting()
    {
        var directory = Directory.CreateTempSubdirectory("walis-safe-tar");

        try
        {
            using var archive = CreateArchive(Regular(DatabaseDump.EntryName, new string('x', 9)));
            var limits = new SafeTarExtractionLimits(
                10,
                8,
                16,
                128);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SafeTarExtractor.ExtractForRestoreAsync(
                    archive,
                    directory.FullName,
                    false,
                    limits));

            Assert.False(File.Exists(Path.Combine(directory.FullName, "walis", DatabaseDump.FileName)));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CombinedSizeOfRegularFilesMustNotExceedTotalLimit()
    {
        var directory = Directory.CreateTempSubdirectory("walis-safe-tar");

        try
        {
            using var archive = CreateArchive(
                Regular(DatabaseDump.EntryName, "123456"),
                Regular("walis/artifacts/result.txt", "abcdef"));
            var limits = new SafeTarExtractionLimits(
                10,
                8,
                10,
                128);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SafeTarExtractor.ExtractForRestoreAsync(
                    archive,
                    directory.FullName,
                    true,
                    limits));

            Assert.False(File.Exists(Path.Combine(directory.FullName, "walis", "artifacts", "result.txt")));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task EntryCountLimitIncludesFilesThatNeedNoExtraction()
    {
        var directory = Directory.CreateTempSubdirectory("walis-safe-tar");

        try
        {
            using var archive = CreateArchive(
                Regular("paths/a.txt", string.Empty),
                Regular("paths/b.txt", string.Empty));
            var limits = new SafeTarExtractionLimits(
                1,
                1,
                1,
                128);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SafeTarExtractor.ExtractForRestoreAsync(
                    archive,
                    directory.FullName,
                    false,
                    limits));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task RawTarBytesAreAlsoLimitedToCoverHeaderPaddingAndPaxMetadata()
    {
        var directory = Directory.CreateTempSubdirectory("walis-safe-tar");

        try
        {
            using var archive = CreateArchive(Regular(DatabaseDump.EntryName, "x"));
            var limits = new SafeTarExtractionLimits(
                10,
                1,
                1,
                128,
                512);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                SafeTarExtractor.ExtractForRestoreAsync(
                    archive,
                    directory.FullName,
                    false,
                    limits));

            Assert.Contains("tar 資料", exception.Message);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "walis", DatabaseDump.FileName)));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static PaxTarEntry Regular(string name, string contents)
    {
        return new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(contents))
        };
    }

    private static MemoryStream CreateArchive(params TarEntry[] entries)
    {
        var stream = new MemoryStream();

        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, true))
        {
            foreach (var entry in entries)
            {
                writer.WriteEntry(entry);
                entry.DataStream?.Dispose();
            }
        }

        stream.Position = 0;
        return stream;
    }
}

public class BackupSettingsProtectorTests
{
    [Fact]
    public void SecretFieldsAreMaskedInResponse()
    {
        var settings = new WalisSettings
        {
            BackupPassphrase = "密語",
            BackupPostgresPassword = "密碼",
            BackupTargets =
            [
                new BackupTarget
                    { Name = "r2", Provider = BackupProviders.S3, SecretAccessKey = "秘密", AccessKeyId = "公開" }
            ]
        };

        var masked = BackupSettingsProtector.Mask(settings);

        Assert.Equal(BackupTarget.MaskedValue, masked.BackupPassphrase);
        Assert.Equal(BackupTarget.MaskedValue, masked.BackupPostgresPassword);
        Assert.Equal(BackupTarget.MaskedValue, masked.BackupTargets[0].SecretAccessKey);

        // Non-secret fields must stay intact, and the original object must not be modified.
        Assert.Equal("公開", masked.BackupTargets[0].AccessKeyId);
        Assert.Equal("秘密", settings.BackupTargets[0].SecretAccessKey);
    }

    [Fact]
    public void SendingBackMaskKeepsOriginalValue()
    {
        var id = Guid.NewGuid();
        var stored = new WalisSettings
        {
            BackupPassphrase = "原本的密語",
            BackupTargets = [new BackupTarget { Id = id, Name = "r2", SecretAccessKey = "原本的秘密" }]
        };

        var incoming = new WalisSettings
        {
            BackupPassphrase = BackupTarget.MaskedValue,
            BackupTargets = [new BackupTarget { Id = id, Name = "r2", SecretAccessKey = BackupTarget.MaskedValue }]
        };

        var merged = BackupSettingsProtector.Merge(incoming, stored);

        Assert.Equal("原本的密語", merged.BackupPassphrase);
        Assert.Equal("原本的秘密", merged.BackupTargets[0].SecretAccessKey);
    }

    [Fact]
    public void ChangedPassphraseIsAdopted()
    {
        var stored = new WalisSettings { BackupPassphrase = "舊的" };
        var incoming = new WalisSettings { BackupPassphrase = "新的" };

        Assert.Equal("新的", BackupSettingsProtector.Merge(incoming, stored).BackupPassphrase);
    }
}

public class BackupApiTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    [Fact]
    public async Task RemoteDestinationWithoutPassphraseIsBlocked()
    {
        var client = factory.CreateClient();
        var payload = await client.GetFromJsonAsync<SettingsResponse>("/api/settings/");
        var settings = payload!.Settings;

        settings.BackupTargets =
        [
            new BackupTarget
            {
                Name = "r2",
                Provider = BackupProviders.S3,
                Bucket = "backup",
                AccessKeyId = "key",
                SecretAccessKey = "secret"
            }
        ];
        settings.BackupPassphrase = null;

        var response = await client.PutAsJsonAsync("/api/settings/", settings);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("backupPassphrase", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EnablingBackupValidatesCron()
    {
        var client = factory.CreateClient();
        var payload = await client.GetFromJsonAsync<SettingsResponse>("/api/settings/");
        var settings = payload!.Settings;

        settings.BackupEnabled = true;
        settings.BackupCron = "不是 cron";

        var response = await client.PutAsJsonAsync("/api/settings/", settings);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("backupCron", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LocalDestinationAlsoRequiresPassphrase()
    {
        var client = factory.CreateClient();
        var payload = await client.GetFromJsonAsync<SettingsResponse>("/api/settings/");
        var settings = payload!.Settings;

        settings.BackupTargets =
        [
            new BackupTarget
            {
                Name = "外接磁碟",
                Provider = BackupProviders.Local,
                Path = Path.Combine(Path.GetTempPath(), "walis-backup-target")
            }
        ];
        settings.BackupPassphrase = string.Empty;

        var response = await client.PutAsJsonAsync("/api/settings/", settings);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("backupPassphrase", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BackupPageDataCanBeRead()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/backup/");

        response.EnsureSuccessStatusCode();
        Assert.Contains("defaultDirectory", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConnectionTestReportsFailureReasonInsteadOfThrowing()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/backup/test", new BackupTarget
        {
            Name = "壞掉的本機路徑",
            Provider = BackupProviders.Local,
            Path = "相對路徑"
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TestResult>();

        Assert.False(result!.Ok);
        Assert.Contains("絕對路徑", result.Message);
    }

    private sealed record SettingsResponse(WalisSettings Settings);

    private sealed record TestResult(bool Ok, string Message);
}