using System.Net;
using RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class MemoryEvaluationTransportTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task OversizedSuccessOrErrorBodyIsRefusedBeforeUnboundedRead(bool declared, bool error)
    {
        using var stream = new CountingStream(1024 * 1024 + 5000);

        using var http = new HttpClient(new MemoryEvaluationBoundedResponseHandler(new ResponseHandler(stream, declared, error)));

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://provider.test/v1"));

        Assert.InRange(stream.ReadBytes, 0, 1024 * 1024 + 1);

        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ExactLimitBodyIsAvailableToSdk()
    {
        const string json = """{"model":"model","choices":[{"message":{"role":"assistant","content":"yes"},"finish_reason":"stop"}],"usage":{"prompt_tokens":0,"completion_tokens":0}}""";

        string body = json.PadRight(1024 * 1024);

        using var http = new HttpClient(new MemoryEvaluationBoundedResponseHandler(new ExactResponseHandler(body)));

        using var response = await http.GetAsync("https://provider.test/v1");

        Assert.Equal(1024 * 1024, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    private sealed class ExactResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private sealed class ResponseHandler(CountingStream stream, bool declared, bool error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(error ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = new StreamContent(stream) };

            if (declared) response.Content.Headers.ContentLength = stream.Length;

            return Task.FromResult(response);
        }
    }

    private sealed class CountingStream(int length) : Stream
    {
        public int ReadBytes { get; private set; }

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => ReadBytes; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = Math.Min(count, length - ReadBytes);

            Array.Fill(buffer, (byte)'x', offset, take);

            ReadBytes += take;

            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int take = Math.Min(buffer.Length, length - ReadBytes);

            buffer.Span[..take].Fill((byte)'x');

            ReadBytes += take;

            return ValueTask.FromResult(take);
        }

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
