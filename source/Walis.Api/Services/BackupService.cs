using Walis.Core.Security;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Walis.Core.Backup;
using Walis.Core.Database;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Services;

/// <summary>
///     Runs one full backup: packs Walis's own database and artifacts, the configured extra paths and PostgreSQL,
///     encrypts, stores locally, uploads to every enabled destination, then cleans up by retention days.
///     Only one runs at a time; manual and scheduled runs share the lock.
/// </summary>
public sealed class BackupService(
    DataDirectory data,
    IDbConnectionFactory connectionFactory,
    ISettingsRepository settings,
    IBackupRepository runs,
    ILeaseRepository leases,
    BackupTargetClientFactory clients,
    ILogger<BackupService> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsRunning => _lock.CurrentCount == 0;

    public string DefaultDirectory => data.Combine("backups");

    public async Task<BackupRun> RunAsync(string triggeredBy, CancellationToken cancellationToken = default)
    {
        if (!await _lock.WaitAsync(TimeSpan.Zero, cancellationToken))
            throw new InvalidOperationException("已經有一個備份在執行中。");

        try
        {
            var owner = $"backup:{Guid.NewGuid():N}";
            var operationLease = await RenewingDatabaseLease.TryAcquireAsync(
                leases,
                LeaseNames.BackupOperation,
                owner,
                cancellationToken);

            if (operationLease is null) throw new InvalidOperationException("另一個備份或還原作業正在執行，請稍後再試。");

            await using (operationLease)
            {
                var run = await runs.StartRunAsync(triggeredBy, operationLease.Token);
                var log = new StringBuilder();
                var size = 0L;
                string? fileName = null;
                var status = BackupRunStatus.Succeeded;

                try
                {
                    var configuration = await settings.GetAsync(operationLease.Token);
                    (fileName, size) = await ExecuteAsync(configuration, log, operationLease.Token);
                }
                catch (Exception exception)
                {
                    status = BackupRunStatus.Failed;
                    log.AppendLine().AppendLine($"備份失敗：{exception.Message}");
                    logger.LogError(exception, "備份失敗");
                }
                finally
                {
                    await runs.CompleteRunAsync(
                        run.Id,
                        status,
                        size,
                        fileName,
                        log.ToString(),
                        CancellationToken.None);
                }

                return (await runs.GetRunAsync(run.Id, CancellationToken.None))!;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<(string FileName, long Size)> ExecuteAsync(
        WalisSettings configuration,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.BackupPassphrase) ||
            configuration.BackupPassphrase.Length is < 16 or > 1024)
            throw new InvalidOperationException("備份含有憑證與密碼雜湊；執行前必須設定 16 到 1,024 字元的加密通關密語。");

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var staging = PrivateFileSystem.CreateTemporaryDirectory(data.Root, "backup");
        var fileName = $"{BackupNaming.Prefix}{stamp}.tar.gz.enc";
        var archivePath = Path.Combine(staging, "archive.tar.gz");

        try
        {
            await using (var archive = new BackupArchive(archivePath))
            {
                await AddWalisAsync(archive, staging, log, cancellationToken);
                await AddExtraPathsAsync(archive, configuration, staging, data, log, cancellationToken);
                await AddPostgresAsync(archive, configuration, staging, log, cancellationToken);
            }

            var finalSource = Path.Combine(staging, "archive.enc");
            await BackupCipher.EncryptAsync(
                archivePath,
                finalSource,
                configuration.BackupPassphrase,
                cancellationToken);
            log.AppendLine("已加密並認證（分塊 AES-256-GCM，PBKDF2-SHA256）");

            var directory = string.IsNullOrWhiteSpace(configuration.BackupDirectory)
                ? DefaultDirectory
                : configuration.BackupDirectory.Trim();
            Directory.CreateDirectory(directory);

            var destination = Path.Combine(directory, fileName);
            File.Move(finalSource, destination, true);

            var size = new FileInfo(destination).Length;
            log.AppendLine($"備份檔：{destination}（{Format(size)}）");

            // AEAD provides authenticity; this sidecar remains as a cheap early
            // corruption check and for use with ordinary file-management tools.
            var hash = await BackupIntegrity.WriteAsync(destination, cancellationToken);
            log.AppendLine($"SHA-256：{hash}");

            PruneLocal(directory, configuration.BackupKeepDays, log);
            await UploadAsync(configuration, destination, log, cancellationToken);

            return (fileName, size);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private async Task AddWalisAsync(
        BackupArchive archive,
        string staging,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        // The primary format is the database-agnostic logical export: all four databases share one file,
        // which is also what allows restoring from SQLite to PostgreSQL.
        var dumpPath = Path.Combine(staging, DatabaseDump.FileName);

        await using (var stream = PrivateFileSystem.CreatePrivateFile(dumpPath))
        {
            var result = await new DatabaseDump(connectionFactory).ExportAsync(stream, cancellationToken);
            log.AppendLine(
                $"Walis 資料（{DatabaseProviders.DisplayName(connectionFactory.Provider)}）" +
                $"{result.Rows} 列、{Format(new FileInfo(dumpPath).Length)}");
        }

        await archive.AddFileAsync(dumpPath, DatabaseDump.EntryName, cancellationToken);

        // SQLite additionally includes the database file: it can be opened directly without going through Walis.
        if (connectionFactory.Provider == DatabaseProvider.Sqlite)
        {
            var snapshot = Path.Combine(staging, "walis.db");

            using (var connection = connectionFactory.Create())
            {
                // VACUUM INTO produces a consistent copy and is safe while running.
                await connection.ExecuteAsync(new CommandDefinition(
                    $"VACUUM INTO '{snapshot.Replace("'", "''")}';",
                    cancellationToken: cancellationToken));
            }

            PrivateFileSystem.RestrictFile(snapshot);

            await archive.AddFileAsync(snapshot, "walis/walis.db", cancellationToken);
            log.AppendLine($"SQLite 檔案複本 {Format(new FileInfo(snapshot).Length)}");
        }

        var artifacts = data.Combine("artifacts");
        var count = await archive.AddDirectoryAsync(artifacts, "walis/artifacts", cancellationToken);

        if (count > 0) log.AppendLine($"產出檔案 {count} 個");
    }

    private static async Task AddExtraPathsAsync(
        BackupArchive archive,
        WalisSettings configuration,
        string staging,
        DataDirectory data,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        // The archive holds the database's ciphertext; the key that opens it is kept apart on purpose, so whoever obtains a backup
        // and its passphrase still cannot read the stored secrets. An extra path that covers the data directory must not undo that.
        string[] keyFiles = [data.Combine(SecretKeyStore.FileName), data.Combine(SetupCode.FileName)];

        foreach (var path in SplitLines(configuration.BackupPaths))
        {
            if (keyFiles.Any(keyFile => string.Equals(Path.GetFullPath(path), Path.GetFullPath(keyFile), StringComparison.Ordinal)))
            {
                log.AppendLine($"{path} 是加密金鑰或設定碼檔，不放進備份，略過");
                continue;
            }

            if (File.Exists(path))
            {
                await archive.AddFileAsync(path, $"paths/{Path.GetFileName(path)}", cancellationToken);
                log.AppendLine($"{path}（單一檔案）");
                continue;
            }

            if (!Directory.Exists(path))
            {
                log.AppendLine($"{path} 不存在，略過");
                continue;
            }

            var name = new DirectoryInfo(path).Name;
            var count = await archive.AddDirectoryAsync(
                path,
                $"paths/{name}",
                cancellationToken,
                [Path.GetDirectoryName(staging)!],
                keyFiles);
            log.AppendLine($"{path} {count} 個檔案");
        }
    }

    private async Task AddPostgresAsync(
        BackupArchive archive,
        WalisSettings configuration,
        string staging,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        if (!configuration.BackupPostgresEnabled) return;

        var databases = SplitLines(configuration.BackupPostgresDatabases);

        if (databases.Count == 0)
        {
            log.AppendLine("PostgreSQL 沒有指定資料庫，略過");
            return;
        }

        databases = databases.Distinct(StringComparer.Ordinal).ToList();

        if (databases.Count > 100 ||
            databases.Any(database => database.Length is < 1 or > 256 || database.Contains('\0')))
            throw new InvalidOperationException("PostgreSQL 資料庫清單最多 100 個，每個名稱最長 256 字元。");

        foreach (var database in databases)
        {
            // Database names are passed as one pg_dump argument, but must never be reused as a
            // filesystem/archive path (a name such as ../../x would escape the private staging dir).
            var safeName = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(database)))[..24];
            var dump = Path.Combine(staging, $"postgres-{safeName}.dump");
            var arguments = new List<string>
            {
                "--format=custom",
                $"--host={configuration.BackupPostgresHost}",
                $"--port={configuration.BackupPostgresPort}",
                $"--dbname={database}",
                $"--file={dump}"
            };

            if (!string.IsNullOrWhiteSpace(configuration.BackupPostgresUser))
                arguments.Add($"--username={configuration.BackupPostgresUser}");

            // The password travels in a private PGPASSFILE inside the 0700 staging directory, never in the environment, and the
            // file is gone before the dump is archived.
            var passwordFile = string.IsNullOrEmpty(configuration.BackupPostgresPassword)
                ? null
                : PgPassFile.Create(staging, configuration.BackupPostgresPassword);

            (int exitCode, string output) result;

            try
            {
                result = await RunProcessAsync(
                    string.IsNullOrWhiteSpace(configuration.BackupPgDumpPath) ? "pg_dump" : configuration.BackupPgDumpPath,
                    arguments,
                    passwordFile,
                    cancellationToken);
            }
            finally
            {
                if (passwordFile is not null) File.Delete(passwordFile);
            }

            var (exitCode, output) = result;

            if (exitCode != 0) throw new InvalidOperationException($"pg_dump {database} 失敗：{output.Trim()}");

            PrivateFileSystem.RestrictFile(dump);
            await archive.AddFileAsync(dump, $"postgres/{safeName}.dump", cancellationToken);
            log.AppendLine($"PostgreSQL {database} {Format(new FileInfo(dump).Length)}");
        }
    }

    private async Task UploadAsync(
        WalisSettings configuration,
        string filePath,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        var targets = configuration.BackupTargets.Where(target => !target.IsDisabled).ToList();

        if (targets.Count == 0)
        {
            log.AppendLine("沒有啟用任何目的地，只保留本機這一份。");
            return;
        }

        var failures = new List<string>();

        foreach (var target in targets)
            try
            {
                var client = clients.Resolve(target);
                await client.UploadAsync(target, filePath, cancellationToken);
                log.AppendLine($"已上傳到 {target.Name}");

                var remote = await client.ListAsync(target, cancellationToken);
                var threshold = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(configuration.BackupKeepDays, 1, 3650));

                foreach (var old in remote.Where(file => file.ModifiedAt < threshold))
                {
                    await client.DeleteAsync(target, old, cancellationToken);
                    log.AppendLine($"  清掉 {target.Name} 的舊備份 {old.Name}");
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{target.Name}：{exception.Message}");
                log.AppendLine($"上傳到 {target.Name} 失敗：{exception.Message}");
            }

        if (failures.Count != 0) throw new InvalidOperationException($"有目的地上傳失敗——{string.Join("；", failures)}");
    }

    private static void PruneLocal(string directory, int keepDays, StringBuilder log)
    {
        var threshold = DateTime.UtcNow.AddDays(-Math.Clamp(keepDays, 1, 3650));

        foreach (var file in Directory.EnumerateFiles(directory, $"{BackupNaming.Prefix}*"))
        {
            var info = new FileInfo(file);

            if (info.LastWriteTimeUtc >= threshold) continue;

            info.Delete();
            log.AppendLine($"清掉本機舊備份 {info.Name}");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? passwordFile,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        // Homebrew executables are not necessarily on the service's PATH.
        startInfo.Environment["PATH"] =
            $"/opt/homebrew/bin:/opt/homebrew/opt/postgresql@18/bin:/usr/local/bin:{Environment.GetEnvironmentVariable("PATH")}";

        // Never PGPASSWORD: see PgPassFile. A PGPASSWORD inherited from the service's own environment is dropped as well.
        startInfo.Environment.Remove("PGPASSWORD");

        if (!string.IsNullOrEmpty(passwordFile)) startInfo.Environment["PGPASSFILE"] = passwordFile;

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            return (-1, $"無法執行 {fileName}：{exception.Message}");
        }

        var output = ReadBoundedProcessOutputAsync(process.StandardOutput);
        var error = ReadBoundedProcessOutputAsync(process.StandardError);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        return (process.ExitCode, $"{await output}\n{await error}");
    }

    private static async Task<string> ReadBoundedProcessOutputAsync(StreamReader reader)
    {
        const int maximumCharacters = 64 * 1024;
        var result = new StringBuilder(maximumCharacters);
        var buffer = new char[4096];
        var truncated = false;
        int read;

        // Keep draining after the display quota is full, otherwise a noisy child can fill its pipe
        // and deadlock while the parent waits for it to exit.
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) != 0)
        {
            var remaining = maximumCharacters - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(read, remaining));

            truncated |= read > remaining;
        }

        if (truncated) result.Append("\n…輸出已截斷…");

        return result.ToString();
    }

    private static List<string> SplitLines(string? value)
    {
        return
        [
            .. (value ?? string.Empty)
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
        ];
    }

    private static string Format(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
            _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"
        };
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException)
        {
            // A temporary directory that cannot be removed does not affect the backup result.
        }
    }
}