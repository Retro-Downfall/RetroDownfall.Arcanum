using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class RequestAugmentingHandlerTests
{

    [Fact]
    public async Task OpenAiHandler_JsonSchemaRequest_AddsStrictTrue()
    {
        CapturingHandler capturing = new();

        OpenAiRequestAugmentingHandler handler = new(
            NullLogger<OpenAiRequestAugmentingHandler>.Instance)
        {

            InnerHandler = capturing

        };

        HttpRequestMessage request = CreateJsonRequest("""
            {"model": "gpt-4o", "messages": [], "response_format": {"type": "json_schema", "json_schema": {"name": "test", "schema": {"type": "object"}}}}
            """);

        HttpResponseMessage response = await new HttpClient(handler).SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(capturing.LastBody);

        using JsonDocument body = JsonDocument.Parse(Encoding.UTF8.GetString(capturing.LastBody!));

        Assert.True(body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());

    }

    [Fact]
    public async Task OpenAiHandler_NonJsonRequest_PassesThroughUnchanged()
    {
        CapturingHandler capturing = new();

        OpenAiRequestAugmentingHandler handler = new(
            NullLogger<OpenAiRequestAugmentingHandler>.Instance)
        {

            InnerHandler = capturing

        };

        HttpRequestMessage request = new(HttpMethod.Get, "http://example.com/v1/models");

        HttpResponseMessage response = await new HttpClient(handler).SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Null(capturing.LastBody);

    }

    [Fact]
    public async Task OpenAiHandler_StrictRetry_PreservesContentTypeHeader()
    {
        StrictRejectingHandler rejecting = new();

        OpenAiRequestAugmentingHandler handler = new(
            NullLogger<OpenAiRequestAugmentingHandler>.Instance)
        {
            InnerHandler = rejecting,
        };

        using HttpClient client = new(handler);

        string json = """
            {
              "model": "test-model",
              "messages": [{"role": "user", "content": "hi"}],
              "response_format": {"type": "json_schema", "json_schema": {"name": "test", "schema": {"type": "object"}}}
            }
            """;

        using StringContent content = new(json, Encoding.UTF8, "application/json");

        HttpRequestMessage request = new(HttpMethod.Post, "http://example.com/v1/chat/completions")
        {
            Content = content,
        };

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(rejecting.RetryBody);

        Assert.NotNull(rejecting.RetryContentType);

        Assert.Equal("application/json", rejecting.RetryContentType!.MediaType);

    }

    [Fact]
    public async Task OpenAiHandler_StrictRetry_InspectsOnlyBoundedErrorPrefix()
    {

        OversizedStrictRejectingHandler rejecting = new();

        OpenAiRequestAugmentingHandler handler = new(
            NullLogger<OpenAiRequestAugmentingHandler>.Instance)
        {

            InnerHandler = rejecting,

        };

        using HttpClient client = new(handler);

        using HttpRequestMessage request = CreateJsonRequest("""
            {"model":"test-model","messages":[],"response_format":{"type":"json_schema","json_schema":{"name":"test","schema":{"type":"object"}}}}
            """);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(2, rejecting.CallCount);

    }

    private static HttpRequestMessage CreateJsonRequest(string json)
    {

        HttpRequestMessage request = new(HttpMethod.Post, "http://example.com/v1/chat/completions")
        {

            Content = new StringContent(json, Encoding.UTF8, "application/json")

        };

        return request;

    }

    private sealed class CapturingHandler : DelegatingHandler
    {

        public byte[]? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {

            LastBody = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK);

        }

    }

    private sealed class StrictRejectingHandler : DelegatingHandler
    {

        public byte[]? RetryBody { get; private set; }

        public System.Net.Http.Headers.MediaTypeHeaderValue? RetryContentType { get; private set; }

        private int _callCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {

            _callCount++;

            if (_callCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error": {"message": "strict mode not supported"}}"""),
                };
            }

            RetryBody = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            RetryContentType = request.Content?.Headers.ContentType;

            return new HttpResponseMessage(HttpStatusCode.OK);

        }

    }

    private sealed class OversizedStrictRejectingHandler : DelegatingHandler
    {

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            CallCount++;

            if (CallCount > 1)
            {

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            }

            byte[] prefix = Encoding.UTF8.GetBytes(
                "{\"error\":{\"message\":\"strict mode unsupported\"},\"padding\":\""
                + new string('x', 70_000));

            StreamContent content = new(new ThrowAfterPayloadStream(prefix));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {

                Content = content,

            });

        }

    }

    private sealed class ThrowAfterPayloadStream(byte[] payload) : Stream
    {

        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {

            get => _offset;

            set => throw new NotSupportedException();

        }

        public override int Read(byte[] buffer, int offset, int count)
        {

            if (_offset >= payload.Length)
            {

                throw new IOException("The bounded reader consumed beyond the permitted prefix.");

            }

            int read = Math.Min(count, payload.Length - _offset);

            payload.AsSpan(_offset, read).CopyTo(buffer.AsSpan(offset, read));

            _offset += read;

            return read;

        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(ReadPayload(buffer.Span));

        }

        private int ReadPayload(Span<byte> buffer)
        {

            if (_offset >= payload.Length)
            {

                throw new IOException("The bounded reader consumed beyond the permitted prefix.");

            }

            int read = Math.Min(buffer.Length, payload.Length - _offset);

            payload.AsSpan(_offset, read).CopyTo(buffer);

            _offset += read;

            return read;

        }

        public override void Flush()
        {

        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    }

}
