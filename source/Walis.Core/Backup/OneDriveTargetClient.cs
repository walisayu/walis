using System.Net.Http.Headers;

namespace Walis.Core.Backup;

/// <summary>OneDrive via Microsoft Graph. A single upload is capped at 250 MB.</summary>
public sealed class OneDriveTargetClient(IBackupHttpClientFactory httpClientFactory)
    : OAuthTargetClient(httpClientFactory)
{
    private const long SingleUploadLimit = 250L * 1024 * 1024;
    private const string Root = "https://graph.microsoft.com/v1.0/me/drive";

    public override string Provider => BackupProviders.OneDrive;

    protected override string TokenEndpoint => "https://login.microsoftonline.com/common/oauth2/v2.0/token";

    protected override IEnumerable<KeyValuePair<string, string>> ExtraTokenFields =>
        [new("scope", "Files.ReadWrite offline_access")];

    public override async Task TestAsync(BackupTarget target, CancellationToken cancellationToken = default)
    {
        var folder = NormalizeFolder(target.Path);
        var url = string.IsNullOrEmpty(folder) ? $"{Root}/root" : $"{Root}/root:{Escape(folder)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(target, request, cancellationToken);
    }

    public override async Task UploadAsync(
        BackupTarget target,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(filePath);

        if (info.Length > SingleUploadLimit)
            throw new BackupTargetException(
                $"備份檔 {info.Length / 1024 / 1024} MB 超過 OneDrive 單次上傳的 250 MB 上限。");

        var folder = NormalizeFolder(target.Path);
        var url = $"{Root}/root:{Escape($"{folder}/{info.Name}")}:/content";

        await using var stream = File.OpenRead(filePath);
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StreamContent(stream)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await SendAsync(target, request, cancellationToken);
    }

    public override async Task<IReadOnlyList<RemoteBackupFile>> ListAsync(
        BackupTarget target,
        CancellationToken cancellationToken = default)
    {
        var folder = NormalizeFolder(target.Path);
        var url = string.IsNullOrEmpty(folder)
            ? $"{Root}/root/children"
            : $"{Root}/root:{Escape(folder)}:/children";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(target, request, cancellationToken);
        var payload = await BackupHttpContent.ReadJsonAsync(response.Content, cancellationToken);

        var files = new List<RemoteBackupFile>();

        if (!payload.TryGetProperty("value", out var entries)) return files;

        foreach (var entry in entries.EnumerateArray())
        {
            var name = entry.TryGetProperty("name", out var nameValue)
                ? nameValue.GetString() ?? string.Empty
                : string.Empty;

            if (!BackupNaming.IsBackupFile(name)) continue;

            _ = DateTimeOffset.TryParse(
                entry.TryGetProperty("lastModifiedDateTime", out var modified) ? modified.GetString() : null,
                out var modifiedAt);

            files.Add(new RemoteBackupFile(
                entry.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
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
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{Root}/items/{Uri.EscapeDataString(file.Id)}");
        using var response = await SendAsync(target, request, cancellationToken);
    }

    /// <summary>Graph paths must be encoded, but slashes are kept.</summary>
    private static string Escape(string path)
    {
        return string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    }
}