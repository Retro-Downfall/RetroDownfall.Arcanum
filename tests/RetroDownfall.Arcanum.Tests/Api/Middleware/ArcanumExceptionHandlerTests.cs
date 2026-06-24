using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Middleware;

namespace RetroDownfall.Arcanum.Tests.Api.Middleware;

public sealed class ArcanumExceptionHandlerTests
{

    [Fact]
    public async Task TryHandleAsync_V1Path_WithResponseStarted_ReturnsFalse()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext(responseStarted: true);

        httpContext.Request.Path = "/v1/chat/completions";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_JsonException_V1Path_ReturnsOpenAiInvalidJson()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/v1/chat/completions";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new JsonException("bad json"),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(400, httpContext.Response.StatusCode);

    }

    [Fact]
    public async Task TryHandleAsync_JsonException_ResponseStarted_ReturnsFalse()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext(responseStarted: true);

        httpContext.Request.Path = "/api/spells/execute";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new JsonException("bad json"),
            CancellationToken.None);

        Assert.False(handled);

    }

    [Fact]
    public async Task TryHandleAsync_JsonException_NonV1Path_ReturnsInvalidBody()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/spells/execute";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new JsonException("bad json"),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(400, httpContext.Response.StatusCode);

    }

    [Fact]
    public async Task TryHandleAsync_NonJsonException_V1Path_ReturnsOpenAiUnhandledError()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/v1/models";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.True(handled);

    }

    [Fact]
    public async Task TryHandleAsync_NonJsonException_NonV1Path_ReturnsInternalError()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/spells/execute";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(500, httpContext.Response.StatusCode);

    }

    [Fact]
    public async Task TryHandleAsync_RequestAbortedOperationCanceled_ReturnsFalse()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        using CancellationTokenSource cts = new();

        cts.Cancel();

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.RequestAborted = cts.Token;

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new OperationCanceledException(),
            CancellationToken.None);

        Assert.False(handled);

    }

    private static DefaultHttpContext CreateHttpContext(bool responseStarted = false)
    {
        ServiceCollection services = new();

        services.AddRouting();

        services.AddLogging();

        ServiceProvider provider = services.BuildServiceProvider();

        MemoryStream body = new();

        TestResponseFeature responseFeature = new()
        {
            HasStarted = responseStarted,
            Body = body,
        };

        TestResponseBodyFeature bodyFeature = new(body, responseFeature);

        TestRequestFeature requestFeature = new();

        FeatureCollection features = new();

        features.Set<IHttpRequestFeature>(requestFeature);

        features.Set<IHttpResponseFeature>(responseFeature);

        features.Set<IHttpResponseBodyFeature>(bodyFeature);

        DefaultHttpContext httpContext = new(features);

        httpContext.RequestServices = provider;

        return httpContext;
    }

    private sealed class TestRequestFeature : IHttpRequestFeature
    {

        public string Protocol { get; set; } = "HTTP/1.1";

        public string Scheme { get; set; } = "http";

        public string Method { get; set; } = "POST";

        public string PathBase { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string QueryString { get; set; } = string.Empty;

        public string RawTarget { get; set; } = string.Empty;

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = new MemoryStream();

    }

    private sealed class TestResponseFeature : IHttpResponseFeature
    {

        public int StatusCode { get; set; } = 200;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = new MemoryStream();

        public bool HasStarted { get; set; }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

    }

    private sealed class TestResponseBodyFeature : IHttpResponseBodyFeature
    {

        private readonly Stream _stream;

        private readonly IHttpResponseFeature _responseFeature;

        public TestResponseBodyFeature(Stream stream, IHttpResponseFeature responseFeature)
        {

            _stream = stream;

            _responseFeature = responseFeature;

        }

        public Stream Stream => _stream;

        public PipeWriter Writer { get; } = PipeWriter.Create(new MemoryStream());

        public Task CompleteAsync() => Task.CompletedTask;

        public void DisableBuffering()
        {
        }

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {

            return Task.CompletedTask;

        }

    }

}
