using System.Net;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ChronosyncApiClientTests
{

    [Fact]
    public async Task SynchronizePatternAsync_posts_the_exact_snapshot_and_returns_the_host_report()
    {

        PatternSnapshot snapshot = new(
            DomainType.Research,
            "/workspace",
            ["one", "two"]);

        ChronosyncReport expected = new(
            DateTimeOffset.Parse("2026-08-22T00:00:00Z"),
            ["two"],
            [],
            true,
            DomainType.SoftwareEngineering);

        RecordingHandler handler = new(async request =>
        {

            Assert.Equal(HttpMethod.Post, request.Method);

            Assert.Equal("/api/perception/chronosync", request.RequestUri?.AbsolutePath);

            PatternSnapshot? received = JsonSerializer.Deserialize(
                await request.Content!.ReadAsByteArrayAsync(),
                ArcanumJsonContext.Default.PatternSnapshot);

            Assert.NotNull(received);
            Assert.Equal(snapshot.Domain, received.Domain);
            Assert.Equal(snapshot.RootPath, received.RootPath);
            Assert.Equal(snapshot.Threads, received.Threads);

            byte[] response = JsonSerializer.SerializeToUtf8Bytes(
                new ApiResponse<ChronosyncReport>(expected, true, null),
                ArcanumJsonContext.Default.ApiResponseChronosyncReport);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {

                Content = new ByteArrayContent(response),

            };

        });

        ArcanumApiClient client = new(
            new FakeHttpClientFactory(handler),
            new FakeSecretStore());

        Result<ChronosyncReport> result = await client.SynchronizePatternAsync(
            snapshot,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(expected.PreviousSnapshotTime, result.Value.PreviousSnapshotTime);
        Assert.Equal(expected.NewThreads, result.Value.NewThreads);
        Assert.Equal(expected.MissingThreads, result.Value.MissingThreads);
        Assert.Equal(expected.DomainChanged, result.Value.DomainChanged);
        Assert.Equal(expected.PreviousDomain, result.Value.PreviousDomain);

        Assert.Equal(1, handler.CallCount);

    }

    [Fact]
    public async Task SynchronizePatternAsync_GeneratesANewIdempotencyKeyForEachInvocation()
    {

        List<string?> keys = [];

        RecordingHandler handler = new(request =>
        {

            keys.Add(ReadIdempotencyKey(request));

            return Task.FromResult(SuccessResponse());

        });

        ArcanumApiClient client = new(
            new FakeHttpClientFactory(handler),
            new FakeSecretStore());

        PatternSnapshot snapshot = new(DomainType.Unknown, "/workspace", []);

        Result<ChronosyncReport> first = await client.SynchronizePatternAsync(snapshot);

        Result<ChronosyncReport> second = await client.SynchronizePatternAsync(snapshot);

        Assert.True(first.IsSuccess);

        Assert.True(second.IsSuccess);

        Assert.Equal(2, keys.Count);

        Assert.All(keys, static key =>
        {

            Assert.NotNull(key);

            Assert.StartsWith("chronosync-", key, StringComparison.Ordinal);

            Assert.Equal(43, key.Length);

        });

        Assert.NotEqual(keys[0], keys[1]);

    }

    [Fact]
    public async Task SynchronizePatternAsync_RetriesOneDisconnectedResponseWithTheSameKeyAndBody()
    {

        List<string?> keys = [];

        List<byte[]> bodies = [];

        RecordingHandler handler = new(async request =>
        {

            keys.Add(ReadIdempotencyKey(request));

            bodies.Add(await request.Content!.ReadAsByteArrayAsync());

            return keys.Count == 1
                ? DisconnectedResponse()
                : SuccessResponse();

        });

        ArcanumApiClient client = new(
            new FakeHttpClientFactory(handler),
            new FakeSecretStore());

        PatternSnapshot snapshot = new(DomainType.Research, "/workspace", ["Document: README.md"]);

        Result<ChronosyncReport> result = await client.SynchronizePatternAsync(snapshot);

        Assert.True(result.IsSuccess);

        Assert.Equal(2, handler.CallCount);

        Assert.NotNull(keys[0]);

        Assert.Equal(keys[0], keys[1]);

        Assert.Equal(bodies[0], bodies[1]);

    }

    [Fact]
    public async Task SynchronizePatternAsync_WhenBothResponseBodiesDisconnect_RetriesOnlyOnce()
    {

        List<string?> keys = [];

        RecordingHandler handler = new(request =>
        {

            keys.Add(ReadIdempotencyKey(request));

            return Task.FromResult(DisconnectedResponse());

        });

        ArcanumApiClient client = new(
            new FakeHttpClientFactory(handler),
            new FakeSecretStore());

        PatternSnapshot snapshot = new(DomainType.Unknown, "/workspace", []);

        Result<ChronosyncReport> result = await client.SynchronizePatternAsync(snapshot);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Connection.Unreachable, result.Error.Code);

        Assert.Equal(
            "The connection to the Arcanum API was lost before the response completed.",
            result.Error.Message);

        Assert.Equal(2, handler.CallCount);

        Assert.NotNull(keys[0]);

        Assert.Equal(keys[0], keys[1]);

    }

    [Fact]
    public async Task GetBudgetAsync_DoesNotInheritTheChronosyncResponseBodyRetry()
    {

        RecordingHandler handler = new(request =>
        {

            Assert.Null(ReadIdempotencyKey(request));

            return Task.FromResult(DisconnectedResponse());

        });

        ArcanumApiClient client = new(
            new FakeHttpClientFactory(handler),
            new FakeSecretStore());

        Result<BudgetSummaryDto> result = await client.GetBudgetAsync();

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Connection.Unreachable, result.Error.Code);

        Assert.Equal(1, handler.CallCount);

    }

    private static string? ReadIdempotencyKey(HttpRequestMessage request) =>
        request.Headers.TryGetValues(ArcanumApiHeaders.IdempotencyKey, out IEnumerable<string>? values)
            ? values.Single()
            : null;

    private static HttpResponseMessage SuccessResponse()
    {

        ChronosyncReport report = new(
            DateTimeOffset.Parse("2026-08-22T00:00:00Z"),
            [],
            [],
            false,
            DomainType.Unknown);

        byte[] response = JsonSerializer.SerializeToUtf8Bytes(
            new ApiResponse<ChronosyncReport>(report, true, null),
            ArcanumJsonContext.Default.ApiResponseChronosyncReport);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {

            Content = new ByteArrayContent(response),

        };

    }

    private static HttpResponseMessage DisconnectedResponse() =>
        new(HttpStatusCode.OK)
        {

            Content = new StreamContent(new IOExceptionReadStream()),

        };

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {

                BaseAddress = new Uri("http://localhost:5001"),

                Timeout = Timeout.InfiniteTimeSpan,

            };

    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {

        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            Interlocked.Increment(ref _callCount);

            return responder(request);

        }

    }

    private sealed class IOExceptionReadStream : Stream
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

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("The response body disconnected.");

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Task.FromException<int>(new IOException("The response body disconnected."));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("The response body disconnected."));

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

    }

    private sealed class FakeSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

}
