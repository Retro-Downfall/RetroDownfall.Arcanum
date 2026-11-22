using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.LocalRuntimes;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class OllamaRuntimeAdapterTests
{
    private static LocalRuntimeSettings Runtime => new() { Id = "desktop", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434/proxy" };

    [Fact]
    public void Named_management_transport_uses_the_public_operations_deadline()
    {
        ServiceCollection services = new();

        services.AddArcanumLocalRuntimeAdapters();

        using ServiceProvider provider = services.BuildServiceProvider();

        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(OllamaRuntimeAdapter.HttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public async Task Inspection_reads_version_and_loaded_models_without_claiming_KV_support()
    {
        List<string> paths = [];

        List<CancellationToken> bodyTokens = [];

        using Handler handler = new(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);

            string body = request.RequestUri.AbsolutePath.EndsWith("/version", StringComparison.Ordinal)
                ? """{"version":"0.40.16"}"""
                : """{"models":[{"name":"primary:latest","digest":"abc","expires_at":"2026-11-15T12:00:00Z","size":123}]}""";

            return new(HttpStatusCode.OK) { Content = new TokenTrackingBody(body, bodyTokens.Add) };
        });

        OllamaRuntimeAdapter adapter = new(new FakeHttpClientFactory(handler));

        var result = await adapter.InspectAsync(Runtime, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(["/proxy/api/version", "/proxy/api/ps"], paths);

        Assert.Equal(2, bodyTokens.Count);

        Assert.True(bodyTokens[0].CanBeCanceled);

        Assert.Equal(bodyTokens[0], bodyTokens[1]);

        Assert.Equal("0.40.16", result.Value.RuntimeVersion);

        Assert.True(result.Value.Capabilities.ModelResidencyRetention);

        Assert.False(result.Value.Capabilities.KvSnapshotRestore);

        Assert.False(result.Value.Capabilities.BeforeUnloadNotification);

        Assert.Equal("primary:latest", Assert.Single(result.Value.LoadedModels).Name);
    }

    [Fact]
    public async Task Retention_uses_empty_native_request_and_own_management_credential()
    {
        using Handler handler = new(request =>
        {
            Assert.Equal("/proxy/api/generate", request.RequestUri!.AbsolutePath);

            Assert.Equal("Bearer management-only", request.Headers.Authorization!.ToString());

            using JsonDocument body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

            Assert.Equal("primary", body.RootElement.GetProperty("model").GetString());

            Assert.False(body.RootElement.GetProperty("stream").GetBoolean());

            Assert.Equal(300, body.RootElement.GetProperty("keep_alive").GetDecimal());

            Assert.False(body.RootElement.TryGetProperty("prompt", out _));

            return Json("""{"model":"primary","done":true,"done_reason":"load"}""");
        });

        OllamaRuntimeAdapter adapter = new(new FakeHttpClientFactory(handler), name => name == "MANAGEMENT_KEY" ? "management-only" : throw new InvalidOperationException("Unexpected credential lookup"));

        var result = await adapter.RetainModelAsync(Runtime with { CredentialEnvironmentVariable = "MANAGEMENT_KEY" }, "primary", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("{\"done\":false}", 200)]
    [InlineData("{\"done\":true,\"done_reason\":\"unload\"}", 200)]
    [InlineData("{\"done\":true,\"done_reason\":\"stop\"}", 200)]
    [InlineData("{\"model\":\"different\",\"done\":true,\"done_reason\":\"load\"}", 200)]
    [InlineData("secret error body", 500)]
    [InlineData("{}", 302)]
    [InlineData("malformed", 200)]
    public async Task Failed_or_incomplete_retention_never_reports_success_or_exposes_body(string body, int status)
    {
        using Handler handler = new(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });

        var result = await new OllamaRuntimeAdapter(new FakeHttpClientFactory(handler)).RetainModelAsync(Runtime, "primary", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.DoesNotContain(body, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nonpositive_retention_and_invalid_endpoint_do_not_dispatch()
    {
        using Handler handler = new(_ => throw new InvalidOperationException("Must not dispatch"));

        OllamaRuntimeAdapter adapter = new(new FakeHttpClientFactory(handler));

        Assert.True((await adapter.RetainModelAsync(Runtime, "primary", TimeSpan.Zero, CancellationToken.None)).IsFailure);

        Assert.True((await adapter.RetainModelAsync(Runtime, "primary", TimeSpan.FromSeconds(-2), CancellationToken.None)).IsFailure);

        Assert.True((await adapter.RetainModelAsync(Runtime, "primary", TimeSpan.FromSeconds(-1), CancellationToken.None)).IsFailure);

        Assert.True((await adapter.InspectAsync(Runtime with { ManagementEndpoint = "http://user:secret@localhost" }, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Infinite_retention_uses_native_negative_one_without_claiming_a_hard_pin()
    {
        using Handler handler = new(request =>
        {
            using JsonDocument body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

            Assert.Equal(-1, body.RootElement.GetProperty("keep_alive").GetInt32());

            return Json("""{"model":"primary","done":true,"done_reason":"load"}""");
        });

        Assert.True((await new OllamaRuntimeAdapter(new FakeHttpClientFactory(handler)).RetainModelAsync(Runtime, "primary", Timeout.InfiniteTimeSpan, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Oversized_inspection_body_is_rejected()
    {
        using Handler handler = new(_ => Json(new string('x', 1_048_577)));

        Assert.True((await new OllamaRuntimeAdapter(new FakeHttpClientFactory(handler)).InspectAsync(Runtime, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Body_read_obeys_whole_request_deadline_after_successful_headers()
    {
        using Handler handler = new(_ => new(HttpStatusCode.OK) { Content = new SlowBody() });

        OllamaRuntimeAdapter adapter = new(new FakeHttpClientFactory(handler), requestTimeout: TimeSpan.FromMilliseconds(100));

        var result = await adapter.InspectAsync(Runtime, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LocalRuntime.Timeout, result.Error.Code);
    }

    [Fact]
    public async Task Inspection_shares_one_deadline_across_both_metadata_requests()
    {
        using CompletedBodyUntilCancellation version = new("""{"version":"test-version"}""");

        using Handler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/proxy/api/version" => new(HttpStatusCode.OK) { Content = version },
            "/proxy/api/ps" => Json("""{"models":[]}"""),
            _ => throw new InvalidOperationException("Unexpected metadata request."),
        });

        OllamaRuntimeAdapter adapter = new(new FakeHttpClientFactory(handler), requestTimeout: TimeSpan.FromSeconds(1));

        var result = await adapter.InspectAsync(Runtime, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(version.ReadToEnd);

        Assert.True(version.DisposalObservedCancellation);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LocalRuntime.Timeout, result.Error.Code);
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("caller")]
    [InlineData("both")]
    public async Task Retention_honors_management_timeout_and_caller_cancellation_after_reading_the_body(string cancellation)
    {
        using CancellationTokenSource caller = new();

        using CompletedBodyUntilCancellation body = new("""{"model":"primary","done":true,"done_reason":"load"}""",
            beforeDispose: cancellation == "caller" ? caller.Cancel : null,
            afterCancellation: cancellation == "both" ? caller.Cancel : null);

        using Handler handler = new(request =>
        {
            Assert.Equal("/proxy/api/generate", request.RequestUri!.AbsolutePath);

            return new(HttpStatusCode.OK) { Content = body };
        });

        OllamaRuntimeAdapter adapter = new(new FakeHttpClientFactory(handler),
            requestTimeout: cancellation == "caller" ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(1));

        Task<Result> retention = adapter.RetainModelAsync(Runtime, "primary", TimeSpan.FromMinutes(5), caller.Token)
            .WaitAsync(TimeSpan.FromSeconds(15));

        if (cancellation != "deadline")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retention);
        }
        else
        {
            Result result = await retention;

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.LocalRuntime.Timeout, result.Error.Code);
        }

        Assert.True(body.ReadToEnd);

        Assert.True(body.DisposalObservedCancellation);
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_dispatch()
    {
        using Handler handler = new(_ => throw new InvalidOperationException("Must not dispatch"));

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OllamaRuntimeAdapter(new FakeHttpClientFactory(handler)).InspectAsync(Runtime, cancellation.Token));
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class TokenTrackingBody(string json, Action<CancellationToken> observeToken) : StringContent(json, Encoding.UTF8, "application/json")
    {
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            observeToken(cancellationToken);

            return base.CreateContentReadStreamAsync(cancellationToken);
        }
    }

    private sealed class CompletedBodyUntilCancellation(string json, Action? beforeDispose = null,
        Action? afterCancellation = null) : HttpContent
    {
        internal bool ReadToEnd { get; private set; }

        internal bool DisposalObservedCancellation { get; private set; }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new DeadlineStream(this, Encoding.UTF8.GetBytes(json), cancellationToken, beforeDispose, afterCancellation));

        private sealed class DeadlineStream(CompletedBodyUntilCancellation owner, byte[] bytes, CancellationToken operationToken,
            Action? beforeDispose, Action? afterCancellation) : MemoryStream(bytes, writable: false)
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int read = await base.ReadAsync(buffer, cancellationToken);

                owner.ReadToEnd |= read == 0;

                return read;
            }

            public override async ValueTask DisposeAsync()
            {
                beforeDispose?.Invoke();

                TaskCompletionSource cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);

                using CancellationTokenRegistration registration = operationToken.Register(() => cancellation.TrySetResult());

                await cancellation.Task.WaitAsync(TimeSpan.FromSeconds(12));

                owner.DisposalObservedCancellation = operationToken.IsCancellationRequested;

                afterCancellation?.Invoke();

                await base.DisposeAsync();
            }
        }
    }

    private sealed class SlowBody : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.Delay(Timeout.InfiniteTimeSpan);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new SlowStream());

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new SlowStream());
    }

    private sealed class SlowStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return 0;
        }
    }
}
