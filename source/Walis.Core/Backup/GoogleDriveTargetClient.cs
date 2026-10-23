using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Walis.Core.Backup;

/// <summary>
///     Google Drive. Drive has no real paths, so the "path" field holds a folder ID
///     (the last segment of the URL after opening the folder).
/// </summary>
public sealed class GoogleDriveTargetClient(IBackupHttpClientFactory httpClientFactory)
    : OAuthTargetClient(httpClientFactory)
{
    public override string Provider => BackupProviders.GoogleDrive;

    protected override string TokenEndpoint => "https://oauth2.googleapis.com/token";

    public override async Task TestAsync(BackupTarget target, CancellationToken cancellationToken = default)
    {
        var folder = FolderId(target);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(folder)}?fields=id,name,mimeType");

        using var response = await SendAsync(target, request, cancellationToken);
        var payload = await BackupHttpContent.ReadJsonAsync(response.Content, cancellationToken);

        if (payload.TryGetProperty("mimeType", out var mimeType) &&
            mimeType.GetString() != "application/vnd.google-apps.folder")
            throw new BackupTargetException("這個 ID 不是資料夾。");
    }

    public override async Task UploadAsync(
        BackupTarget target,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var metadata = JsonContent.Create(new
        {
            name = Path.GetFileName(filePath),
            parents = new[] { FolderId(target) }
        });

        await using var stream = File.OpenRead(filePath);
        using var media = new StreamContent(stream);
        media.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var content = new MultipartContent("related") { metadata, media };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart")
        {
            Content = content
        };

        using var response = await SendAsync(target, request, cancellationToken);
    }

    public override async Task<IReadOnlyList<RemoteBackupFile>> ListAsync(
        BackupTarget target,
        CancellationToken cancellationToken = default)
    {
        var query = Uri.EscapeDataString($"'{FolderId(target)}' in parents and trashed = false");
        var fields = Uri.EscapeDataString("files(id,name,modifiedTime,size)");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://www.googleapis.com/drive/v3/files?q={query}&fields={fields}&pageSize=1000");

        using var response = await SendAsync(target, request, cancellationToken);
        var payload = await BackupHttpContent.ReadJsonAsync(response.Content, cancellationToken);

        var files = new List<RemoteBackupFile>();

        if (!payload.TryGetProperty("files", out var entries)) return files;

        foreach (var entry in entries.EnumerateArray())
        {
            var name = entry.TryGetProperty("name", out var nameValue)
                ? nameValue.GetString() ?? string.Empty
                : string.Empty;

            if (!BackupNaming.IsBackupFile(name)) continue;

            _ = DateTimeOffset.TryParse(
                entry.TryGetProperty("modifiedTime", out var modified) ? modified.GetString() : null,
                out var modifiedAt);
            _ = long.TryParse(
                entry.TryGetProperty("size", out var size) ? size.GetString() : null,
                out var sizeValue);

            files.Add(new RemoteBackupFile(
                entry.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                name,
                modifiedAt,
                sizeValue));
        }

        return files;
    }

    public override async Task DeleteAsync(
        BackupTarget target,
        RemoteBackupFile file,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(file.Id)}");

        using var response = await SendAsync(target, request, cancellationToken);
    }

    private static string FolderId(BackupTarget target)
    {
        return Require(target.Path, "資料夾 ID", 1024);
    }
}