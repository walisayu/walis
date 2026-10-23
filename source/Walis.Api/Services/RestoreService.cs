using System.IO.Compression;
using Walis.Core.Backup;
using Walis.Core.Database;
using Walis.Core.Repositories;

namespace Walis.Api.Services;

/// <summary>
///     IntegrityVerified: true when the sidecar or AEAD authentication succeeded. AuditRowsKept/AuditRowsAdded: the audit log is
///     merged rather than replaced, so the rows already present survive and only the archive's missing rows are added.
/// </summary>
public sealed record RestoreResult(
    int Tables,
    long Rows,
    int Artifacts,
    string Provider,
    bool? IntegrityVerified = null,
    long AuditRowsKept = 0,
    long AuditRowsAdded = 0);

/// <summary>
///     Restores from a backup file. The data in the archive is a database-agnostic logical export,
///     so it can be restored into any supported database; migrations and upgrading from a single node to HA both go through here.
/// </summary>
public sealed class RestoreService(
    IDbConnectionFactory connectionFactory,
    IJobRepository jobs,
    ILeaseRepository leases,
    IDatabaseInitializer initializer,
    ISettingsRepository settings,
    CorsOriginStore corsOrigins,
    McpAccessStore mcpAccess,
    DataDirectory data,
    ILogger<RestoreService> logger,
    AuditService? audit = null,
    SecurityEventService? securityEvents = null)
{
    private const long MaximumEncryptedBackupBytes = 11L * 1024 * 1024 * 1024;

    /// <summary>
    ///     Restores a whole backup. A database maintenance lease is taken during the restore to keep new builds from starting.
    ///     The legacy <paramref name="force" /> parameter is kept for API compatibility but can no longer bypass the running-build safety check.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(
        string archivePath,
        string? passphrase,
        bool includeArtifacts,
        bool force,
        CancellationToken cancellationToken = default)
    {
        ValidateArchiveFile(archivePath);

        var owner = $"restore:{Guid.NewGuid():N}";
        var operationLease = await RenewingDatabaseLease.TryAcquireAsync(
            leases,
            LeaseNames.BackupOperation,
            owner,
            cancellationToken);

        if (operationLease is null) throw new InvalidOperationException("另一個備份或還原作業正在執行，請稍後再試。");

        await using (operationLease)
        {
            return await RestoreWithOperationLeaseAsync(
                archivePath,
                passphrase,
                includeArtifacts,
                force,
                owner,
                operationLease,
                cancellationToken);
        }
    }

    private async Task<RestoreResult> RestoreWithOperationLeaseAsync(
        string archivePath,
        string? passphrase,
        bool includeArtifacts,
        bool force,
        string owner,
        RenewingDatabaseLease operationLease,
        CancellationToken cancellationToken)
    {
        var operationToken = operationLease.Token;

        // API restore only accepts the authenticated format. Legacy CBC and
        // plaintext archives can still be handled offline, but must never reach
        // the destructive database import path without cryptographic integrity.
        var format = await BackupCipher.GetFormatAsync(archivePath, operationToken);

        if (format != BackupCipher.BackupEncryptionFormat.AuthenticatedV1)
            throw new InvalidDataException(
                "基於完整性保護，API 只接受目前的 WALISBKP 認證加密備份；舊 CBC 或未加密備份請先離線解密後重新建立安全備份。");

        // The SHA-256 sidecar is an early corruption check. Security does not
        // depend on it: AES-GCM authenticates every chunk and the final marker.
        var integrity = await BackupIntegrity.VerifyAsync(archivePath, operationToken);

        if (integrity == false)
            throw new InvalidDataException(
                $"{Path.GetFileName(archivePath)} 與旁邊的 {BackupIntegrity.Suffix} 校驗值不符，檔案可能損毀或被竄改，已拒絕還原。");

        var workspace = PrivateFileSystem.CreateTemporaryDirectory(data.Root, "restore");

        try
        {
            var tarball = await PrepareAsync(archivePath, passphrase, workspace, operationToken);
            var extracted = Path.Combine(workspace, "extracted");
            PrivateFileSystem.EnsurePrivateDirectory(extracted);

            await using (var file = File.OpenRead(tarball))
            await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                await SafeTarExtractor.ExtractForRestoreAsync(
                    gzip,
                    extracted,
                    includeArtifacts,
                    cancellationToken: operationToken);
            }

            var dumpPath = Path.Combine(extracted, "walis", DatabaseDump.FileName);

            if (!File.Exists(dumpPath))
                throw new InvalidDataException(
                    $"備份包裡沒有 {DatabaseDump.EntryName}；" +
                    "這可能是舊版備份，舊版只放了 SQLite 檔案複本。");

            // Only acquire the disruptive maintenance fence after the archive has been fully
            // authenticated and safely extracted. New builds cannot enter Running once held.
            var maintenanceLease = await RenewingDatabaseLease.TryAcquireAsync(
                leases,
                LeaseNames.Maintenance,
                owner,
                operationToken);

            if (maintenanceLease is null) throw new InvalidOperationException("系統正在維護中，無法開始還原。");

            await using (maintenanceLease)
            {
                var restoreToken = maintenanceLease.Token;

                // The lease row is locked by the Queued -> Running transition too, so this count
                // cannot race with another node starting a build. Running processes cannot be
                // safely killed across hosts; the legacy force bypass is therefore deliberately
                // fail-closed instead of risking writes into the restored database/filesystem.
                if (await jobs.CountRunningBuildsAsync(restoreToken) > 0)
                    throw new InvalidOperationException(
                        force
                            ? "還有建置在執行中；為避免舊執行器在還原後回寫資料，安全模式不再允許強制繞過。"
                            : "還有建置在執行中，請等全部結束後再還原。");

                await operationLease.EnsureOwnedAsync(restoreToken);
                await maintenanceLease.EnsureOwnedAsync(restoreToken);

                // Make sure the schema is current before restoring: the backup may come from an older version with fewer columns.
                await initializer.InitializeAsync(restoreToken);

                // The settings before the restore, to describe what the restore changed (see below).
                var settingsBefore = await settings.GetAsync(restoreToken);

                DatabaseDump.DumpResult result;

                await using (var stream = File.OpenRead(dumpPath))
                {
                    result = await new DatabaseDump(connectionFactory).ImportAsync(
                        stream,
                        [
                            new ImportLeaseGuard(
                                operationLease.Name,
                                operationLease.Owner,
                                operationLease.Duration),
                            new ImportLeaseGuard(
                                maintenanceLease.Name,
                                maintenanceLease.Owner,
                                maintenanceLease.Duration)
                        ],
                        restoreToken);
                }

                // Importing an older archive may omit newly-added tables/claims. Re-run the
                // idempotent initializer and revoke any legacy credential representation before
                // allowing traffic to use the restored state.
                await initializer.InitializeAsync(restoreToken);

                // CORS and the MCP allowlist are the settings-backed runtime snapshots. Repository consumers load
                // settings for each operation, but these singletons must be refreshed explicitly or
                // this node keeps authorizing origins and addresses from before the restore until the next tick.
                // The archive's settings row went in as is; saving it again applies the same normalization as PUT /api/settings
                // (audit retention floor and the other clamps), so a crafted archive cannot set retention to one day. What the
                // restore changed is recorded field by field, exactly like a settings update, including the forced entries for
                // the audit switch and retention.
                var restoredSettings = await settings.SaveAsync(await settings.GetAsync(restoreToken), restoreToken);

                if (audit is not null)
                {
                    var changed = SettingsChangeDescriber.ChangedFields(settingsBefore, restoredSettings);
                    if (changed.Count != 0)
                        await audit.RecordAsync(SettingsChangeDescriber.Describe(changed) with
                        {
                            Summary = $"還原備份改變了設定：{string.Join("、", changed)}",
                            Force = true
                        }, restoreToken);

                    foreach (var change in SettingsChangeDescriber.DescribeSecurityChanges(settingsBefore, restoredSettings))
                        await audit.RecordAsync(change with { Force = true }, restoreToken);
                }

                if (securityEvents is not null && settingsBefore.AuditLog && !restoredSettings.AuditLog)
                    await securityEvents.AuditDisabledAsync(restoreToken);

                corsOrigins.Update(restoredSettings.CorsOrigins);
                mcpAccess.Update(restoredSettings.McpAllowedAddresses);

                var artifacts = includeArtifacts
                    ? RestoreArtifacts(Path.Combine(extracted, "walis", "artifacts"))
                    : 0;

                logger.LogWarning(
                    "已從 {Archive} 還原：{Tables} 張表、{Rows} 列、產出檔案 {Artifacts} 個；稽核紀錄保留 {AuditKept} 筆、補入 {AuditAdded} 筆",
                    Path.GetFileName(archivePath), result.Tables, result.Rows, artifacts, result.AuditRowsKept,
                    result.AuditRowsAdded);

                return new RestoreResult(
                    result.Tables,
                    result.Rows,
                    artifacts,
                    DatabaseProviders.DisplayName(connectionFactory.Provider),
                    integrity ?? true,
                    result.AuditRowsKept,
                    result.AuditRowsAdded);
            }
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    /// <summary>Only the authenticated current format reaches this method.</summary>
    private static async Task<string> PrepareAsync(
        string archivePath,
        string? passphrase,
        string workspace,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new InvalidOperationException("這份備份是加密的，需要通關密語。");

        var plain = Path.Combine(workspace, "backup.tar.gz");

        try
        {
            await BackupCipher.DecryptAuthenticatedBackupAsync(archivePath, plain, passphrase, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("加密備份已損壞或超出安全大小上限。", exception);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("解密失敗，通關密語可能不對。", exception);
        }

        return plain;
    }

    private static void ValidateArchiveFile(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var info = new FileInfo(Path.GetFullPath(archivePath));

        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("備份必須是既存的普通檔案，且不可為連結或 reparse point。");

        // Authenticated plaintext is capped at 10 GiB. Eleven GiB leaves ample room for chunk
        // framing while preventing a huge local file from consuming minutes of hashing first.
        if (info.Length is <= 0 or > MaximumEncryptedBackupBytes)
            throw new InvalidDataException(
                $"加密備份大小必須介於 1 與 {MaximumEncryptedBackupBytes} bytes 之間。");
    }

    private int RestoreArtifacts(string source)
    {
        if (!Directory.Exists(source)) return 0;

        var target = data.Combine("artifacts");
        PrivateFileSystem.EnsurePrivateDirectory(target);
        var fullTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var targetPrefix = fullTarget + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var count = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"還原工作區包含不允許的連結：{file}");

            var relative = Path.GetRelativePath(source, file);
            var destination = Path.GetFullPath(Path.Combine(fullTarget, relative));

            // Extracted paths must not escape the artifact directory.
            if (!destination.StartsWith(targetPrefix, comparison))
                throw new InvalidDataException($"產出檔還原路徑超出允許範圍：{relative}");

            var parent = Path.GetDirectoryName(destination)!;
            EnsureArtifactPathHasNoLinks(fullTarget, parent);
            CopyPrivateAtomically(file, destination);
            count++;
        }

        return count;
    }

    private static void EnsureArtifactPathHasNoLinks(string root, string directory)
    {
        var relative = Path.GetRelativePath(root, directory);
        var current = root;

        if (relative == ".") return;

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"產出檔目的地包含不允許的連結：{current}");

            PrivateFileSystem.EnsurePrivateDirectory(current);
        }
    }

    private static void CopyPrivateAtomically(string source, string destination)
    {
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var input = File.OpenRead(source))
            using (var output = PrivateFileSystem.CreatePrivateFile(temporary))
            {
                input.CopyTo(output);
            }

            File.Move(temporary, destination, true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // Preserve the original restore error.
            }

            throw;
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch (IOException)
        {
        }
    }
}