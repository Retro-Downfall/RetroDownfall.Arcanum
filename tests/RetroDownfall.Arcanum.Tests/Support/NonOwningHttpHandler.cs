namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Forwards every request to a test-owned handler without ever disposing it, so a scripted server handler
/// outlives the transport client that sends through it.
/// </summary>
internal sealed class NonOwningHttpHandler(HttpMessageHandler inner) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _invoker = new(inner, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _invoker.SendAsync(request, cancellationToken);
}
