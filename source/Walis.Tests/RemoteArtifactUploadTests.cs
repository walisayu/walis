using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Walis.Api.Services;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

public class RemoteArtifactUploadTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    [Fact]
    public void DownloadResolutionRejectsSymlinkSwappedAfterSaving()
    {
        if (OperatingSystem.IsWindows()) return;

        var store = factory.Services.GetRequiredService<ArtifactStore>();
        var buildId = Guid.NewGuid();
        var directory = store.DirectoryFor(buildId);
        var outside = Path.Combine(Path.GetTempPath(), $"walis-outside-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(directory);
        File.WriteAllText(outside, "secret");
        File.WriteAllText(Path.Combine(directory, "normal.txt"), "artifact");
        var link = Path.Combine(directory, "result.txt");

        try
        {
            Assert.True(store.TryOpenFile(buildId, "normal.txt", out var normal));
            using (normal)
            using (var reader = new StreamReader(normal))
            {
                Assert.Equal("artifact", reader.ReadToEnd());
            }

            File.CreateSymbolicLink(link, outside);
            Assert.Equal(string.Empty, store.ResolveFile(buildId, "result.txt"));
            Assert.False(store.TryOpenFile(buildId, "result.txt", out var stream));
            Assert.Null(stream);
        }
        finally
        {
            File.Delete(link);
            File.Delete(outside);
            store.Remove(buildId);
        }
    }

    [Fact]
    public async Task DeclaredRequestBodyOverLimitIsRejectedWithoutReadingContent()
    {
        var service = factory.Services.GetRequiredService<RemoteArtifactUploadService>();
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=walis-test";
        context.Request.ContentLength = RemoteArtifactUploadService.MaximumRequestBytes + 1;
        context.Request.Body = new ThrowOnReadStream();

        var result = await service.UploadAsync(
            Guid.NewGuid(),
            "unused-runner",
            context.Request,
            CancellationToken.None);

        Assert.Equal(RemoteArtifactUploadStatus.PayloadTooLarge, result.Status);
    }

    [Fact]
    public async Task OnlyOwningRunnerCanSubmitOneCompleteArtifactBatchWhileRunning()
    {
        var setup = await CreateBuildAsync(false);

        try
        {
            var queued = await UploadAsync(
                setup.Client,
                setup.Token,
                setup.Build.Id,
                ("queued.txt", "queued"u8.ToArray()));
            Assert.Equal(HttpStatusCode.Conflict, queued.StatusCode);

            Assert.True(await setup.Jobs.MarkBuildRunningAsync(setup.Build.Id, setup.RunnerName));

            var otherKey = await factory.Services.GetRequiredService<IApiKeyRepository>().CreateAsync(
                new ApiKeyDraft("other-artifact-runner", UserRole.Operator, null, $"other-{Guid.NewGuid():N}"),
                "test");
            var wrongRunner = await UploadAsync(
                setup.Client,
                otherKey.Token,
                setup.Build.Id,
                ("wrong.txt", "wrong"u8.ToArray()));
            Assert.Equal(HttpStatusCode.Forbidden, wrongRunner.StatusCode);

            // This is a runner-only data entry point; even an interactive admin cannot pose as a runner to write files.
            var administrator = await UploadAsync(
                factory.CreateClient(),
                null,
                setup.Build.Id,
                ("admin.txt", "admin"u8.ToArray()));
            Assert.Equal(HttpStatusCode.Forbidden, administrator.StatusCode);

            var saved = await UploadAsync(
                setup.Client,
                setup.Token,
                setup.Build.Id,
                ("dist/app.txt", "artifact-data"u8.ToArray()));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

            var artifacts = await setup.Jobs.ListArtifactsAsync(setup.Build.Id);
            var artifact = Assert.Single(artifacts);
            Assert.Equal("dist/app.txt", artifact.RelativePath);
            Assert.Equal(13, artifact.SizeBytes);
            Assert.Equal(
                "artifact-data",
                await File.ReadAllTextAsync(setup.Store.ResolveFile(setup.Build.Id, artifact.RelativePath)));

            await setup.Jobs.CompleteBuildAsync(
                setup.Build.Id,
                new BuildCompletion(BuildStatus.Succeeded, 0, "done"));

            var historical = await UploadAsync(
                setup.Client,
                setup.Token,
                setup.Build.Id,
                ("late.txt", "late"u8.ToArray()));
            Assert.Equal(HttpStatusCode.Conflict, historical.StatusCode);
            Assert.Single(await setup.Jobs.ListArtifactsAsync(setup.Build.Id));
            Assert.False(File.Exists(setup.Store.ResolveFile(setup.Build.Id, "late.txt")));
        }
        finally
        {
            setup.Store.Remove(setup.Build.Id);
        }
    }

    [Fact]
    public async Task InvalidPathsOrTooManyFilesRejectWholeBatchWithoutLeftovers()
    {
        var invalid = await CreateBuildAsync(true);

        try
        {
            var rejected = await UploadAsync(
                invalid.Client,
                invalid.Token,
                invalid.Build.Id,
                ("ok.txt", "ok"u8.ToArray()),
                ("../escape.txt", "escape"u8.ToArray()));

            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Empty(await invalid.Jobs.ListArtifactsAsync(invalid.Build.Id));
            Assert.False(Directory.Exists(invalid.Store.DirectoryFor(invalid.Build.Id)));

            var deviceName = await UploadAsync(
                invalid.Client,
                invalid.Token,
                invalid.Build.Id,
                ("CON.txt", "device"u8.ToArray()));
            Assert.Equal(HttpStatusCode.BadRequest, deviceName.StatusCode);
            Assert.Empty(await invalid.Jobs.ListArtifactsAsync(invalid.Build.Id));
            Assert.False(Directory.Exists(invalid.Store.DirectoryFor(invalid.Build.Id)));
        }
        finally
        {
            invalid.Store.Remove(invalid.Build.Id);
        }

        var excessive = await CreateBuildAsync(true);

        try
        {
            var files = Enumerable.Range(0, 201)
                .Select(index => ($"f{index:D3}.txt", Array.Empty<byte>()))
                .ToArray();
            var rejected = await UploadAsync(
                excessive.Client,
                excessive.Token,
                excessive.Build.Id,
                files);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
            Assert.Empty(await excessive.Jobs.ListArtifactsAsync(excessive.Build.Id));
            Assert.False(Directory.Exists(excessive.Store.DirectoryFor(excessive.Build.Id)));
        }
        finally
        {
            excessive.Store.Remove(excessive.Build.Id);
        }
    }

    [Fact]
    public async Task AtomicCommitIsRefusedWithoutHistoryWhenBuildEndsDuringStreaming()
    {
        var setup = await CreateBuildAsync(true);

        try
        {
            var (body, contentType) = await MultipartBytesAsync(
                ("dist/result.txt", Encoding.UTF8.GetBytes(new string('x', 2048))));
            await using var gated = new GatedReadStream(body, 64, 16);
            var context = new DefaultHttpContext();
            context.Request.ContentType = contentType;
            context.Request.Body = gated;

            var service = factory.Services.GetRequiredService<RemoteArtifactUploadService>();
            var uploading = service.UploadAsync(
                setup.Build.Id,
                setup.RunnerName,
                context.Request,
                CancellationToken.None);

            await gated.Paused.WaitAsync(TimeSpan.FromSeconds(10));
            await setup.Jobs.CompleteBuildAsync(
                setup.Build.Id,
                new BuildCompletion(BuildStatus.Succeeded, 0, "finished while uploading"));
            gated.Release();

            var result = await uploading;
            Assert.Equal(RemoteArtifactUploadStatus.Conflict, result.Status);
            Assert.Empty(await setup.Jobs.ListArtifactsAsync(setup.Build.Id));
            Assert.False(Directory.Exists(setup.Store.DirectoryFor(setup.Build.Id)));
        }
        finally
        {
            setup.Store.Remove(setup.Build.Id);
        }
    }

    private async Task<BuildSetup> CreateBuildAsync(bool markRunning)
    {
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
        var runnerName = $"artifact-{Guid.NewGuid():N}"[..20];
        var job = await jobs.CreateJobAsync(new JobDraft(
            $"artifact upload {Guid.NewGuid():N}",
            null,
            null,
            null,
            "main",
            runnerName,
            null,
            "echo artifact",
            30,
            "dist",
            TriggerMode.Manual,
            null,
            true,
            false,
            ScriptModes.Source,
            null));
        var build = await jobs.QueueBuildAsync(job.Id, "test", runnerName);

        if (markRunning) Assert.True(await jobs.MarkBuildRunningAsync(build.Id, runnerName));

        var issued = await factory.Services.GetRequiredService<IApiKeyRepository>().CreateAsync(
            new ApiKeyDraft($"artifact-key-{Guid.NewGuid():N}", UserRole.Operator, null, runnerName),
            "test");
        var client = factory.CreateClient();

        return new BuildSetup(
            jobs,
            factory.Services.GetRequiredService<ArtifactStore>(),
            client,
            issued.Token,
            runnerName,
            build);
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        string? token,
        Guid buildId,
        params (string Name, byte[] Contents)[] files)
    {
        using var content = new MultipartFormDataContent();

        foreach (var file in files) content.Add(new ByteArrayContent(file.Contents), "files", file.Name);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/builds/{buildId}/artifacts")
        {
            Content = content
        };

        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    private static async Task<(byte[] Body, string ContentType)> MultipartBytesAsync(
        params (string Name, byte[] Contents)[] files)
    {
        using var content = new MultipartFormDataContent();

        foreach (var file in files) content.Add(new ByteArrayContent(file.Contents), "files", file.Name);

        using var stream = new MemoryStream();
        await content.CopyToAsync(stream);
        return (stream.ToArray(), content.Headers.ContentType!.ToString());
    }

    private sealed record BuildSetup(
        IJobRepository Jobs,
        ArtifactStore Store,
        HttpClient Client,
        string Token,
        string RunnerName,
        Build Build);

    private sealed class GatedReadStream(
        byte[] contents,
        int pauseAfter,
        int maximumChunk) : Stream
    {
        private readonly MemoryStream _inner = new(contents, false);
        private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _didPause;

        public Task Paused => _paused.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public void Release()
        {
            _resume.TrySetResult();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            PauseAsync(CancellationToken.None).GetAwaiter().GetResult();
            return _inner.Read(buffer, offset, Math.Min(count, maximumChunk));
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await PauseAsync(cancellationToken);
            return await _inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunk)], cancellationToken);
        }

        private async Task PauseAsync(CancellationToken cancellationToken)
        {
            if (_didPause || _inner.Position < pauseAfter) return;

            _didPause = true;
            _paused.TrySetResult();
            await _resume.Task.WaitAsync(cancellationToken);
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();

            base.Dispose(disposing);
        }
    }

    private sealed class ThrowOnReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new InvalidOperationException("超額 Content-Length 不該讀 request body。");
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}