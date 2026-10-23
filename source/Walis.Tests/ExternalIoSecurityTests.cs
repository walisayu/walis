using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Walis.Api.Services;
using Walis.Core.Backup;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Tests;

public sealed class ExternalIoSecurityTests
{
    [Fact]
    public async Task NotificationsReadOnlyResponseHeadersAndRecordsDoNotStoreWebhookSecretPaths()
    {
        var content = new FailingReadContent();
        var repository = new CapturingNotifications();
        var dispatcher = new NotificationDispatcher(
            repository,
            new StaticSettings(),
            new StubHttpClientFactory(new ResponseHandler(content)),
            NullLogger<NotificationDispatcher>.Instance);
        var channel = new NotificationChannel
        {
            Kind = NotificationChannelKind.Webhook,
            Name = "hook",
            Target = "https://hooks.example.test/services/secret-token?key=secret",
            IsEnabled = true
        };

        var result = await dispatcher.SendTestAsync(channel, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(content.WasRead);
        Assert.Equal("https://hooks.example.test/***", result.Target);
        Assert.Equal(result, repository.LastEntry);
    }

    [Fact]
    public void SharedOutboundHandlerKeepsNoCrossRequestCookiesAndLimitsConnectionsAndHeaders()
    {
        using var handler = OutboundHttpHandlers.Create(7);

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(7, handler.MaxConnectionsPerServer);
        Assert.Equal(32, handler.MaxResponseHeadersLength);

        using var pluginHandler = OutboundHttpHandlers.CreatePlugin(3);
        Assert.False(pluginHandler.UseProxy);
        Assert.NotNull(pluginHandler.ConnectCallback);

        Assert.False(OutboundHttpHandlers.IsPublicAddress(IPAddress.Parse("127.0.0.1")));
        Assert.False(OutboundHttpHandlers.IsPublicAddress(IPAddress.Parse("169.254.169.254")));
        Assert.False(OutboundHttpHandlers.IsPublicAddress(IPAddress.Parse("10.20.30.40")));
        Assert.False(OutboundHttpHandlers.IsPublicAddress(IPAddress.Parse("fd00::1")));
        Assert.True(OutboundHttpHandlers.IsPublicAddress(IPAddress.Parse("8.8.8.8")));
        Assert.True(OutboundHttpHandlers.IsPublicAddress(IPAddress.Parse("2606:4700:4700::1111")));
    }

    [Fact]
    public async Task OAuthTokenRefreshCoalescesAndRefetchesImmediatelyAfterCredentialRotation()
    {
        var handler = new CountingOAuthHandler();
        var client = new TestOAuthClient(new StubBackupHttpClientFactory(handler));
        var target = OAuthTarget();

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.TestAsync(target)));
        Assert.Equal(1, handler.TokenRequests);

        target.RefreshToken = "rotated-refresh-token";
        await client.TestAsync(target);
        Assert.Equal(2, handler.TokenRequests);
    }

    [Fact]
    public async Task OAuthProviderErrorsDoNotEchoConfiguredSecrets()
    {
        const string secret = "oauth-client-secret-value";
        var handler = new CountingOAuthHandler(secret);
        var client = new TestOAuthClient(new StubBackupHttpClientFactory(handler));
        var target = OAuthTarget();
        target.ClientSecret = secret;

        var exception = await Assert.ThrowsAsync<BackupTargetException>(() => client.TestAsync(target));

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.Contains("***", exception.Message);
    }

    [Fact]
    public void ErrorMaskingCoversUrlFormBase64AndJsonEncodedSecrets()
    {
        const string secret = "secret value/with+symbols";
        var bytes = Encoding.UTF8.GetBytes(secret);
        var variants = new[]
        {
            secret,
            Uri.EscapeDataString(secret),
            WebUtility.UrlEncode(secret),
            Convert.ToBase64String(bytes),
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            JsonSerializer.Serialize(secret)[1..^1]
        };

        foreach (var variant in variants)
            Assert.Equal("before *** after", SecretRedactor.Redact($"before {variant} after", secret));
    }

    private static BackupTarget OAuthTarget()
    {
        return new BackupTarget
        {
            Id = Guid.NewGuid(),
            Provider = BackupProviders.Dropbox,
            ClientId = "client-id",
            ClientSecret = "client-secret",
            RefreshToken = "refresh-token"
        };
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(handler, false);
        }
    }

    private sealed class StubBackupHttpClientFactory(HttpMessageHandler handler) : IBackupHttpClientFactory
    {
        public HttpClient Create()
        {
            return new HttpClient(handler, false);
        }
    }

    private sealed class TestOAuthClient(IBackupHttpClientFactory factory) : OAuthTargetClient(factory)
    {
        public override string Provider => "test-oauth";
        protected override string TokenEndpoint => "https://tokens.example.test/token";

        public override async Task TestAsync(BackupTarget target, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://storage.example.test/test");
            using var response = await SendAsync(target, request, cancellationToken);
        }

        public override Task UploadAsync(BackupTarget target, string filePath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public override Task<IReadOnlyList<RemoteBackupFile>> ListAsync(BackupTarget target,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public override Task DeleteAsync(BackupTarget target, RemoteBackupFile file,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CountingOAuthHandler(string? reflectedSecret = null) : HttpMessageHandler
    {
        private int _tokenRequests;
        public int TokenRequests => Volatile.Read(ref _tokenRequests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "tokens.example.test")
            {
                Interlocked.Increment(ref _tokenRequests);
                await Task.Delay(20, cancellationToken);

                if (reflectedSecret is not null)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent($"client_secret={reflectedSecret}")
                    };

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"access-token","expires_in":3600}""")
                };
            }

            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ResponseHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class FailingReadContent : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            throw new InvalidOperationException("response body must not be buffered");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            WasRead = true;
            throw new InvalidOperationException("response body must not be read");
        }
    }

    private sealed class StaticSettings : ISettingsRepository
    {
        public Task<WalisSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WalisSettings());
        }

        public Task<WalisSettings> SaveAsync(WalisSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(settings);
        }
    }

    private sealed class CapturingNotifications : INotificationRepository
    {
        public NotificationLogEntry? LastEntry { get; private set; }

        public Task AppendLogAsync(NotificationLogEntry entry, CancellationToken cancellationToken = default)
        {
            LastEntry = entry;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotificationChannel>> ListChannelsAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<NotificationChannel>> ListEnabledChannelsAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<NotificationChannel?> GetChannelAsync(Guid id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<NotificationChannel> CreateChannelAsync(NotificationChannelDraft draft,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<NotificationChannel?> UpdateChannelAsync(Guid id, NotificationChannelDraft draft,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> DeleteChannelAsync(Guid id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<NotificationLogEntry>> ListLogAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}