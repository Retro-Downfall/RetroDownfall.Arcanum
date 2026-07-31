using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class OpenAiCompatApiClientReliabilityTests
{
    [Fact]
    public async Task DeleteNoContentAsync_does_not_buffer_an_ignored_response_body()
    {
        ArcanumApiClient client = new(
            new StaticHttpClientFactory(new FaultingDownloadHandler()),
            new StaticTheForgeSettingsMonitor(new TheForgeSettings
            {
                BaseUrl = "http://localhost:5001",
            }),
            new StaticApiKeyProvider(),
            NullLogger<ArcanumApiClient>.Instance);

        bool result = await client.DeleteNoContentAsync(
            "/api/campaigns/test",
            CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task GetAsync_response_read_failure_returns_connection_failure()
    {
        OpenAiCompatApiClient client = CreateClient(new FaultingDownloadHandler());

        var result = await client.GetAsync(
            "/v1/files/file-1",
            TheForgeJsonContext.Default.OpenAiFileObject,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Connection.Failed", result.ErrorCode);
    }

    [Fact]
    public async Task OpenContentStreamAsync_response_read_failure_disposes_the_response_stream()
    {
        FaultingDownloadHandler handler = new(HttpStatusCode.BadGateway);
        OpenAiCompatApiClient client = CreateClient(handler);

        var result = await client.OpenContentStreamAsync(
            "/v1/files/file-1/content",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Connection.Failed", result.ErrorCode);
        Assert.True(handler.LastStream?.WasDisposed);
    }

    [Fact]
    public async Task DownloadToFileAsync_failed_transfer_preserves_existing_destination()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"forge-download-{Guid.NewGuid():N}");
        string destination = Path.Combine(directory, "result.jsonl");

        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(destination, "original");

        try
        {
            OpenAiCompatApiClient client = CreateClient(new FaultingDownloadHandler());

            var result = await client.DownloadToFileAsync(
                "/v1/files/file-1/content",
                destination,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("original", await File.ReadAllTextAsync(destination));
            Assert.Single(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static OpenAiCompatApiClient CreateClient(HttpMessageHandler handler) =>
        new(
            new StaticHttpClientFactory(handler),
            new StaticTheForgeSettingsMonitor(new TheForgeSettings
            {
                BaseUrl = "http://localhost:5001",
            }),
            new StaticApiKeyProvider(),
            NullLogger<OpenAiCompatApiClient>.Instance);

    private sealed class StaticApiKeyProvider : ITheForgeApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>("test-key");

        public Task PersistPastedKeyAsync(string apiKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ClearPasteDecline()
        {
        }
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001"),
            };
    }

    private sealed class FaultingDownloadHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public FaultingDownloadHandler(HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _statusCode = statusCode;
        }

        public FaultingReadStream? LastStream { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastStream = new FaultingReadStream();

            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StreamContent(LastStream),
            });
        }
    }

    private sealed class FaultingReadStream : Stream
    {
        private bool _returnedPrefix;

        public bool WasDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_returnedPrefix)
            {
                _returnedPrefix = true;
                "partial"u8.CopyTo(buffer.Span);

                return ValueTask.FromResult("partial"u8.Length);
            }

            return ValueTask.FromException<int>(new IOException("simulated connection loss"));
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
