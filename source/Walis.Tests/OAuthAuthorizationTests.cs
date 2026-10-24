using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Walis.Core.Backup;

namespace Walis.Tests;

public class OAuthAuthorizationTests
{
    private sealed class StubFactory(HttpMessageHandler handler) : IBackupHttpClientFactory
    {
        public HttpClient Create() => new(handler, false);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Form { get; private set; }
        public Uri? Uri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Form = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public void AuthorizationUrlCarriesParametersEachProviderNeedsForRefreshToken()
    {
        var dropbox = OAuthAuthorization.BuildAuthorizeUrl(BackupProviders.Dropbox, "app-key")!;
        Assert.StartsWith("https://www.dropbox.com/oauth2/authorize?", dropbox);
        Assert.Contains("token_access_type=offline", dropbox);
        Assert.DoesNotContain("redirect_uri", dropbox);

        var google = OAuthAuthorization.BuildAuthorizeUrl(BackupProviders.GoogleDrive, "id.apps.googleusercontent.com")!;
        Assert.Contains("access_type=offline", google);
        Assert.Contains("prompt=consent", google);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost", google);
        Assert.Contains("drive.file", google);

        var onedrive = OAuthAuthorization.BuildAuthorizeUrl(BackupProviders.OneDrive, "guid")!;
        Assert.Contains("offline_access", onedrive);
        Assert.Contains("response_mode=query", onedrive);

        Assert.Null(OAuthAuthorization.BuildAuthorizeUrl(BackupProviders.Dropbox, " "));
        Assert.Null(OAuthAuthorization.BuildAuthorizeUrl(BackupProviders.S3, "x"));
    }

    [Fact]
    public async Task ExchangesCodeForRefreshTokenAndErrorMessagesContainNoSecrets()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"access_token":"short","refresh_token":"rt-123","expires_in":3600}""");
        var token = await OAuthAuthorization.ExchangeCodeAsync(new StubFactory(handler), BackupProviders.GoogleDrive, "cid", "csecret-value", "the-code", null);

        Assert.Equal("rt-123", token);
        Assert.Equal("https://oauth2.googleapis.com/token", handler.Uri!.ToString());
        Assert.Contains("grant_type=authorization_code", handler.Form);
        Assert.Contains("code=the-code", handler.Form);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost", handler.Form);

        var dropbox = new RecordingHandler(HttpStatusCode.OK, """{"refresh_token":"rt-dbx"}""");
        await OAuthAuthorization.ExchangeCodeAsync(new StubFactory(dropbox), BackupProviders.Dropbox, "cid", "csecret-value", "code", null);
        Assert.DoesNotContain("redirect_uri", dropbox.Form);

        var failing = new RecordingHandler(HttpStatusCode.BadRequest, """{"error":"invalid_grant","secret":"csecret-value","code":"the-code"}""");
        var exception = await Assert.ThrowsAsync<BackupTargetException>(() =>
            OAuthAuthorization.ExchangeCodeAsync(new StubFactory(failing), BackupProviders.OneDrive, "cid", "csecret-value", "the-code", null));
        Assert.Contains("400", exception.Message);
        Assert.DoesNotContain("csecret-value", exception.Message);
        Assert.DoesNotContain("the-code", exception.Message);

        var noRefresh = new RecordingHandler(HttpStatusCode.OK, """{"access_token":"only"}""");
        var missing = await Assert.ThrowsAsync<BackupTargetException>(() =>
            OAuthAuthorization.ExchangeCodeAsync(new StubFactory(noRefresh), BackupProviders.GoogleDrive, "cid", "csecret-value", "code", null));
        Assert.Contains("refresh token", missing.Message);
    }
}
