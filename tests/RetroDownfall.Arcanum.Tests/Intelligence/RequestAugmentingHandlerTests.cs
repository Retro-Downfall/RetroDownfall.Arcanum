using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Support;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

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

    /// <summary>
    /// The handler's own contract says streaming requests pass through unchanged, and nothing proved
    /// it: a structured-output request that asks for <c>text/event-stream</c> must reach the provider
    /// byte for byte, with no <c>strict</c> flag injected and no retry.
    /// </summary>
    [Fact]
    public async Task OpenAiHandler_StreamingAccept_PassesThroughUnchanged()
    {
        CapturingHandler capturing = new();

        OpenAiRequestAugmentingHandler handler = new(
            NullLogger<OpenAiRequestAugmentingHandler>.Instance)
        {
            InnerHandler = capturing,
        };

        const string json = """
            {"model": "gpt-4o", "stream": true, "messages": [], "response_format": {"type": "json_schema", "json_schema": {"name": "test", "schema": {"type": "object"}}}}
            """;

        using HttpRequestMessage request = CreateJsonRequest(json);

        request.Headers.Accept.ParseAdd("text/event-stream");

        using HttpResponseMessage response = await new HttpClient(handler).SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(Encoding.UTF8.GetBytes(json), capturing.LastBody);

        Assert.Equal(1, capturing.CallCount);
    }

    /// <summary>
    /// The same proof through the real OpenAI client the hub uses, so a transport that reads or
    /// re-buffers the body differently from <see cref="HttpClient"/> cannot slip past. A streaming turn
    /// reaches the provider unchanged and its event stream is delivered intact; the same request sent
    /// buffered is the control that shows the handler would have injected <c>strict</c>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenAiHandler_WithARealChatClient_InjectsStrictOnlyForBufferedTurns(bool streaming)
    {
        ProviderEchoHandler provider = new();

        OpenAiRequestAugmentingHandler handler = new(
            NullLogger<OpenAiRequestAugmentingHandler>.Instance)
        {
            InnerHandler = provider,
        };

        using HttpClient httpClient = new(handler);

        OpenAIClientOptions clientOptions = new()
        {
            Endpoint = new Uri("https://provider.test/v1"),
            Transport = new HttpClientPipelineTransport(httpClient),
        };

        OpenAI.Chat.ChatClient concreteClient = new("m", new ApiKeyCredential("test-key"), clientOptions);

        using IChatClient client = concreteClient.AsIChatClient();

        ChatOptions options = new()
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"answer":{"type":"string"}}}"""),
                "answer",
                schemaDescription: string.Empty),
        };

        MeAiChatMessage[] messages = [new(ChatRole.User, "hi")];

        string text = string.Empty;

        if (streaming)
        {
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options))
            {
                text += update.Text;
            }
        }
        else
        {
            text = (await client.GetResponseAsync(messages, options)).Text;
        }

        Assert.Equal("answer", text);

        Assert.Equal(1, provider.CallCount);

        // The OpenAI client marks a streaming turn with "stream": true in the body and does not send an
        // event-stream Accept header, so the handler must recognise the body.
        Assert.Equal(streaming, provider.StreamRequested);

        Assert.Equal(!streaming, provider.LastBodyText.Contains("\"strict\":true", StringComparison.Ordinal));
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

    /// <summary>
    /// A 400 that has nothing to do with <c>strict</c> is the provider's real verdict, and it is the
    /// only place the reason lives. Inspecting the body for the retry decision must not consume it:
    /// the caller reads the same response afterwards, and a drained one degrades a precise
    /// <c>context_length_exceeded</c> into a bare "Status: 400".
    /// </summary>
    [Fact]
    public async Task OpenAiHandler_BadRequestNotAboutStrict_LeavesTheProviderErrorReadable()
    {
        StreamingBadRequestHandler rejecting = new(
            """{"error":{"code":"context_length_exceeded","message":"This model's maximum context length is 8192 tokens."}}""");

        OpenAiRequestAugmentingHandler handler = new(
            NullLogger<OpenAiRequestAugmentingHandler>.Instance)
        {
            InnerHandler = rejecting,
        };

        using HttpClient client = new(handler);

        using HttpRequestMessage request = CreateJsonRequest("""
            {"model":"test-model","messages":[],"response_format":{"type":"json_schema","json_schema":{"name":"test","schema":{"type":"object"}}}}
            """);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(1, rejecting.CallCount);

        Assert.Contains(
            "context_length_exceeded",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Answers 400 with a body carried on a one-shot stream, the way a real transport does — a
    /// buffered <see cref="StringContent"/> would replay itself and hide a consumed response.
    /// </summary>
    private sealed class StreamingBadRequestHandler(string body) : DelegatingHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;

            HttpResponseMessage response = new(HttpStatusCode.BadRequest)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))),
            };

            return Task.FromResult(response);
        }
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

        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;

            LastBody = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>A provider stand-in that answers either shape and records exactly what it was sent.</summary>
    private sealed class ProviderEchoHandler : DelegatingHandler
    {
        private const string BufferedResponse =
            """
            {"id":"chatcmpl-test","object":"chat.completion","created":1750000000,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """;

        private const string StreamingResponse =
            """
            data: {"id":"chatcmpl-test","object":"chat.completion.chunk","created":1750000000,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"answer"},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        public int CallCount { get; private set; }

        public bool StreamRequested { get; private set; }

        public string LastBodyText { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;

            LastBodyText = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // The body decides the answer's shape, as a real provider does.
            using JsonDocument sent = JsonDocument.Parse(LastBodyText);

            bool streamed = sent.RootElement.TryGetProperty("stream", out JsonElement stream)
                && stream.ValueKind == JsonValueKind.True;

            StreamRequested = streamed;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    streamed ? StreamingResponse : BufferedResponse,
                    Encoding.UTF8,
                    streamed ? "text/event-stream" : "application/json"),
            };
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
