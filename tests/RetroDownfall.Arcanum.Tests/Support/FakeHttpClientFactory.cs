using System.Net.Http;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Test <see cref="IHttpClientFactory"/> that hands out clients over an optional fixed
/// <see cref="HttpMessageHandler"/> (so outbound requests can be recorded / stubbed). When no
/// handler is supplied, a default <see cref="HttpClient"/> is returned for callers that never
/// actually issue a request.
/// </summary>
public sealed class FakeHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
{

    public HttpClient CreateClient(string name) =>
        handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

}
