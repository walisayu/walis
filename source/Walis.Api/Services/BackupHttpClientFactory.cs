using Walis.Core.Backup;

namespace Walis.Api.Services;

/// <summary>Hands the API side's named HttpClient to Core's upload implementations.</summary>
public sealed class BackupHttpClientFactory(IHttpClientFactory factory) : IBackupHttpClientFactory
{
    public HttpClient Create()
    {
        return factory.CreateClient("backup");
    }
}