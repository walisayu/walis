using System.Net;
using Walis.Mcp;

namespace Walis.Tests;

public sealed class McpTransportSecurityTests
{
    [Theory]
    [InlineData("http://10.0.0.5:11120")]
    [InlineData("http://walis.example.test:11120")]
    public void RemoteMcpUrlOverPlaintextHttpConnectsAndIsFlagged(string url)
    {
        // A fresh server answers plain HTTP, so the bridge must reach it; the flag is what makes the caller warn.
        Assert.True(new WalisApi(new HttpClient(), url, "walis_test").UsesPlaintextRemote);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11120")]
    [InlineData("http://localhost:11120")]
    [InlineData("https://walis.example.test")]
    public void LoopbackAndHttpsUrlsAreNotFlagged(string url)
    {
        Assert.False(new WalisApi(new HttpClient(), url, null).UsesPlaintextRemote);
    }

    [Theory]
    [InlineData("ftp://walis.example.test")]
    [InlineData("http://user:secret@walis.example.test")]
    [InlineData("http://walis.example.test/?token=1")]
    public void MalformedOrCredentialBearingUrlIsStillRejected(string url)
    {
        Assert.Throws<ArgumentException>(() => new WalisApi(new HttpClient(), url, null));
    }

    [Fact]
    public async Task OversizedResponseIsNotReadIntoMemoryWithoutLimit()
    {
        using var handler = new FixedResponseHandler(new string('x', WalisApi.MaximumResponseBytes + 1));
        using var client = new HttpClient(handler);
        var api = new WalisApi(client, "http://127.0.0.1:11120", null);

        await Assert.ThrowsAsync<WalisApiException>(() => api.GetAsync("api/jobs/", CancellationToken.None));
    }

    private sealed class FixedResponseHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload)
            });
        }
    }
}