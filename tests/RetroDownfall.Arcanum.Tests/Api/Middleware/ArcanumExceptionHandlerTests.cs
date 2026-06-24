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

    private static DefaultHttpContext CreateHttpContext(bool responseStarted = false)
    {
        ServiceCollection services = new();

        services.AddRouting();

        services.AddLogging();

        ServiceProvider provider = services.BuildServiceProvider();

        TestResponseFeature responseFeature = new()
        {
            HasStarted = responseStarted,
            Body = new MemoryStream(),
        };

        TestRequestFeature requestFeature = new();

        FeatureCollection features = new();

        features.Set<IHttpRequestFeature>(requestFeature);

        features.Set<IHttpResponseFeature>(responseFeature);

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

}
