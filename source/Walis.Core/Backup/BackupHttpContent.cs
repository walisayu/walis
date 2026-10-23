using System.Text;
using System.Text.Json;

namespace Walis.Core.Backup;

/// <summary>Bounds provider-controlled response bodies before materializing JSON/XML/error text.</summary>
internal static class BackupHttpContent
{
    private const int MaximumBytes = 4 * 1024 * 1024;

    public static async Task<string> ReadStringAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumBytes) throw new BackupTargetException("備份供應商回應超過 4 MiB 上限。");

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;

        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > MaximumBytes) throw new BackupTargetException("備份供應商回應超過 4 MiB 上限。");

            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    public static async Task<JsonElement> ReadJsonAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var text = await ReadStringAsync(content, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new BackupTargetException("備份供應商回傳無效 JSON。", exception);
        }
    }
}