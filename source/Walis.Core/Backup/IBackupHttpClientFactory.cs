namespace Walis.Core.Backup;

/// <summary>
///     Keeps Core free of a dependency on Microsoft.Extensions.Http: the API layer supplies an HttpClient with timeouts configured.
/// </summary>
public interface IBackupHttpClientFactory
{
    HttpClient Create();
}