using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Walis.Core.Backup;

/// <summary>
///     Dropbox. The token is obtained after creating an app in the Dropbox App Console;
///     a single upload is capped at 150 MB, beyond which an upload session would be needed, so this rejects it with an explanation for now.
/// </summary>
public sealed class DropboxTargetClient(IBackupHttpClientFactory httpClientFactory)
    : OAuthTargetClient(httpClientFactory)
{
    private const long SingleUploadLimit = 150L * 1024 * 1024;

    public override string Provider => BackupProviders.Dropbox;

    protected override string TokenEndpoint => "https://api.dropbox.com/oauth2/token";

    public override async Task TestAsync(BackupTarget target, CancellationToken cancellationToken = default)
    {
        var folder = NormalizeFolder(target.Path);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/files/list_folder")
        {
            Content = JsonContent.Create(new { path = folder, limit = 1 })
        };

        try
        {
            using var response = await SendAsync(target, request, cancellationToken);
        }
        catch (BackupTargetException exception) when (exception.Message.Contains("not_found"))
        {
            // A missing folder is normal; just create it.
            await CreateFolderAsync(target, folder, cancellationToken);
        }
    }

    public override async Task UploadAsync(
        BackupTarget target,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(filePath);

        if (info.Length > SingleUploadLimit)
            throw new BackupTargetException(
                $"備份檔 {info.Length / 1024 / 1024} MB 超過 Dropbox 單次上傳的 150 MB 上限。");

        var destination = $"{NormalizeFolder(target.Path)}/{info.Name}";
        var arguments = JsonSerializer.Serialize(new
        {
            path = destination,
            mode = "overwrite",
            autorename = false,
            mute = true
        });

        await using var stream = File.OpenRead(filePath);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload")
        {
            Content = new StreamContent(stream)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", arguments);

        using var response = await SendAsync(target, request, cancellationToken);
    }

    public override async Task<IReadOnlyList<RemoteBackupFile>> ListAsync(
        BackupTarget target,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/files/list_folder")
        {
            Content = JsonContent.Create(new { path = NormalizeFolder(target.Path) })
        };

        using var response = await SendAsync(target, request, cancellationToken);
        var payload = await BackupHttpContent.ReadJsonAsync(response.Content, cancellationToken);

        var files = new List<RemoteBackupFile>();

        if (!payload.TryGetProperty("entries", out var entries)) return files;

        foreach (var entry in entries.EnumerateArray())
        {
            var name = entry.TryGetProperty("name", out var nameValue)
                ? nameValue.GetString() ?? string.Empty
                : string.Empty;

            if (!BackupNaming.IsBackupFile(name)) continue;

            _ = DateTimeOffset.TryParse(
                entry.TryGetProperty("server_modified", out var modified) ? modified.GetString() : null,
                out var modifiedAt);

            files.Add(new RemoteBackupFile(
                entry.TryGetProperty("path_lower", out var path) ? path.GetString() ?? string.Empty : string.Empty,
                name,
                modifiedAt,
                entry.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
        }

        return files;
    }

    public override async Task DeleteAsync(
        BackupTarget target,
        RemoteBackupFile file,
        CancellationToken cancellationToken = default)
    {
        var expected = $"{NormalizeFolder(target.Path)}/{file.Name}";
        if (!BackupNaming.IsBackupFile(file.Name) ||
            file.Id.Length > 2048 ||
            !string.Equals(file.Id, expected, StringComparison.OrdinalIgnoreCase))
            throw new BackupTargetException("遠端備份檔不在設定的 Dropbox 資料夾內。");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/files/delete_v2")
        {
            Content = JsonContent.Create(new { path = file.Id })
        };

        using var response = await SendAsync(target, request, cancellationToken);
    }

    private async Task CreateFolderAsync(BackupTarget target, string folder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(folder)) return;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.dropboxapi.com/2/files/create_folder_v2")
        {
            Content = JsonContent.Create(new { path = folder })
        };

        using var response = await SendAsync(target, request, cancellationToken);
    }
}