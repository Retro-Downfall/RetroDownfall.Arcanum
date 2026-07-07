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

        ArcanumSettings settings = new()
        {

            StructuredOutput = new StructuredOutputSettings
            {

                Enabled = true,

                UseProviderConstrainedDecoding = true

            }

        };

        CapturingHandler capturing = new();

        OpenAiRequestAugmentingHandler handler = new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
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

        ArcanumSettings settings = new()
        {

            StructuredOutput = new StructuredOutputSettings
            {

                Enabled = true,

                UseProviderConstrainedDecoding = true

            }

        };

        CapturingHandler capturing = new();

        OpenAiRequestAugmentingHandler handler = new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
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
    public async Task LlamaCppHandler_JsonSchemaRequest_AddsGrammar()
    {

        ArcanumSettings settings = new()
        {

            StructuredOutput = new StructuredOutputSettings
            {

                Enabled = true,

                UseProviderConstrainedDecoding = true

            }

        };

        CapturingHandler capturing = new();

        LlamaCppRequestAugmentingHandler handler = new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<LlamaCppRequestAugmentingHandler>.Instance)
        {

            InnerHandler = capturing

        };

        HttpRequestMessage request = CreateJsonRequest("""
            {"model": "llama-3.1-70b", "messages": [], "response_format": {"type": "json_schema", "json_schema": {"name": "test", "schema": {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}}}}
            """);

        HttpResponseMessage response = await new HttpClient(handler).SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(capturing.LastBody);

        using JsonDocument body = JsonDocument.Parse(Encoding.UTF8.GetString(capturing.LastBody!));

        Assert.True(body.RootElement.TryGetProperty("grammar", out JsonElement grammar));

        Assert.Contains("root ::=", grammar.GetString() ?? string.Empty, StringComparison.Ordinal);

    }

    [Fact]
    public async Task LlamaCppHandler_DisabledConstrainedDecoding_PassesThroughUnchanged()
    {

        ArcanumSettings settings = new()
        {

            StructuredOutput = new StructuredOutputSettings
            {

                Enabled = true,

                UseProviderConstrainedDecoding = false

            }

        };

        CapturingHandler capturing = new();

        LlamaCppRequestAugmentingHandler handler = new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<LlamaCppRequestAugmentingHandler>.Instance)
        {

            InnerHandler = capturing

        };

        HttpRequestMessage request = CreateJsonRequest("""
            {"model": "llama-3.1-70b", "messages": [], "response_format": {"type": "json_schema", "json_schema": {"name": "test", "schema": {"type": "object"}}}}
            """);

        HttpResponseMessage response = await new HttpClient(handler).SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(capturing.LastBody);

        using JsonDocument body = JsonDocument.Parse(Encoding.UTF8.GetString(capturing.LastBody!));

        Assert.False(body.RootElement.TryGetProperty("grammar", out _));

    }

    [Fact]
    public async Task LlamaCppHandler_CacheEnabledAndLargePrompt_AddsCachePrompt()
    {

        ArcanumSettings settings = new()
        {

            StructuredOutput = new StructuredOutputSettings
            {

                Enabled = true,

                UseProviderConstrainedDecoding = false

            },

            Cache = new CacheSettings
            {

                Enabled = true,

                MinCacheableTokens = 1

            }

        };

        CapturingHandler capturing = new();

        LlamaCppRequestAugmentingHandler handler = new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<LlamaCppRequestAugmentingHandler>.Instance,
            tokenizerResolver: null)
        {

            InnerHandler = capturing

        };

        HttpRequestMessage request = CreateJsonRequest("""
            {"model": "llama-3.1-70b", "messages": [{"role": "system", "content": "you are a helpful assistant"}, {"role": "user", "content": "hello"}]}
            """);

        HttpResponseMessage response = await new HttpClient(handler).SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(capturing.LastBody);

        using JsonDocument body = JsonDocument.Parse(Encoding.UTF8.GetString(capturing.LastBody!));

        Assert.True(body.RootElement.TryGetProperty("cache_prompt", out JsonElement cachePrompt));

        Assert.True(cachePrompt.GetBoolean());

    }

    [Fact]
    public async Task LlamaCppHandler_CacheEnabledButShortPrompt_DoesNotAddCachePrompt()
    {

        ArcanumSettings settings = new()
        {

            StructuredOutput = new StructuredOutputSettings
            {

                Enabled = true,

                UseProviderConstrainedDecoding = false

            },

            Cache = new CacheSettings
            {

                Enabled = true,

                MinCacheableTokens = 10_000

            }

        };

        CapturingHandler capturing = new();

        LlamaCppRequestAugmentingHandler handler = new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<LlamaCppRequestAugmentingHandler>.Instance,
            tokenizerResolver: null)
        {

            InnerHandler = capturing

        };

        HttpRequestMessage request = CreateJsonRequest("""
            {"model": "llama-3.1-70b", "messages": [{"role": "user", "content": "hi"}]}
            """);

        HttpResponseMessage response = await new HttpClient(handler).SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(capturing.LastBody);

        using JsonDocument body = JsonDocument.Parse(Encoding.UTF8.GetString(capturing.LastBody!));

        Assert.False(body.RootElement.TryGetProperty("cache_prompt", out _));

    }

    [Fact]
    public async Task LlamaCppHandler_CacheDisabled_DoesNotAddCachePrompt()
    {

        ArcanumSettings settings = new()
        {

            StructuredOutput = new StructuredOutputSettings
            {

                Enabled = true,

                UseProviderConstrainedDecoding = false

            },

            Cache = new CacheSettings { Enabled = false }

        };

        CapturingHandler capturing = new();

        LlamaCppRequestAugmentingHandler handler = new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<LlamaCppRequestAugmentingHandler>.Instance,
            tokenizerResolver: null)
        {

            InnerHandler = capturing

        };

        HttpRequestMessage request = CreateJsonRequest("""
            {"model": "llama-3.1-70b", "messages": [{"role": "user", "content": "hello world this is a long enough prompt to exceed a tiny threshold"}]}
            """);

        HttpResponseMessage response = await new HttpClient(handler).SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(capturing.LastBody);

        using JsonDocument body = JsonDocument.Parse(Encoding.UTF8.GetString(capturing.LastBody!));

        Assert.False(body.RootElement.TryGetProperty("cache_prompt", out _));

    }

    [Fact]
    public async Task LlamaHandler_NonObjectJsonBody_PassesThroughUnchanged()
    {

        ArcanumSettings settings = new()
        {
            StructuredOutput = new StructuredOutputSettings { Enabled = true, UseProviderConstrainedDecoding = true },
        };

        CapturingHandler capturing = new();

        LlamaCppRequestAugmentingHandler handler = new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<LlamaCppRequestAugmentingHandler>.Instance,
            tokenizerResolver: null)
        {
            InnerHandler = capturing,
        };

        using HttpClient client = new(handler);

        using StringContent content = new("\"just a string\"", Encoding.UTF8, "application/json");

        HttpRequestMessage request = new(HttpMethod.Post, "http://example.com/v1/chat/completions")
        {
            Content = content,
        };

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(capturing.LastBody);

        string bodyText = Encoding.UTF8.GetString(capturing.LastBody!);

        Assert.Equal("\"just a string\"", bodyText);

    }

    [Fact]
    public async Task OpenAiHandler_StrictRetry_PreservesContentTypeHeader()
    {

        ArcanumSettings settings = new()
        {
            StructuredOutput = new StructuredOutputSettings
            {
                Enabled = true,
                UseProviderConstrainedDecoding = true,
            },
        };

        StrictRejectingHandler rejecting = new();

        OpenAiRequestAugmentingHandler handler = new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
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

}
