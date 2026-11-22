using System.Net.Http;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public async Task Authored_handler_can_install_disposable_content_on_an_originally_empty_GET_request()
    {
        int disposed = 0;

        using var handler = new InstallingRequestContentHandler(() => disposed++);

        using var client = new HttpClient(handler, disposeHandler: false);

        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://fixture.invalid"))
        {
            Assert.Null(request.Content);

            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            Assert.IsType<InstalledRequestContent>(request.Content);

            Assert.Equal(0, disposed);
        }

        Assert.Equal(1, disposed);
    }

    private sealed class InstallingRequestContentHandler(Action onContentDispose) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Content = new InstalledRequestContent(onContentDispose);

            return Task.FromResult(new HttpResponseMessage());
        }
    }

    private sealed class InstalledRequestContent(Action onDispose) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) onDispose();

            base.Dispose(disposing);
        }
    }
}
