using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ReasoningChatOptionsAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandardDialect_BufferedAndStreaming_EmitEffortAndPreserveTypedOutput(bool streaming)
    {
        ChatOptions options = new();

        ReasoningChatOptionsAdapter.Apply(
            options,
            new ReasoningRequestOptions(
                Effort: ReasoningEffortLevel.High,
                Output: ReasoningOutputMode.Full),
            ReasoningWireDialect.Standard);

        Assert.Equal(ReasoningEffort.High, options.Reasoning?.Effort);
        Assert.Equal(ReasoningOutput.Full, options.Reasoning?.Output);
        Assert.Null(options.RawRepresentationFactory);

        (string answer, byte[] body) = await SendProviderRequestAsync(options, streaming);

        Assert.Equal("normal answer", answer);

        using JsonDocument document = JsonDocument.Parse(body);

        JsonElement root = document.RootElement;

        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("reasoning_output", out _));
        Assert.False(root.TryGetProperty("reasoning", out _));
        Assert.False(root.TryGetProperty("reasoning_budget", out _));
        Assert.False(root.TryGetProperty("thinking", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MinimalEffort_BufferedAndStreaming_UsesConcreteOpenAiMinimal(bool streaming)
    {
        ChatOptions options = new();

        ReasoningChatOptionsAdapter.Apply(
            options,
            new ReasoningRequestOptions(
                Effort: ReasoningEffortLevel.Minimal,
                Output: ReasoningOutputMode.Summary),
            ReasoningWireDialect.Standard);

        Assert.NotNull(options.Reasoning);
        Assert.Null(options.Reasoning.Effort);
        Assert.Equal(ReasoningOutput.Summary, options.Reasoning.Output);
        Assert.NotNull(options.RawRepresentationFactory);

        (string answer, byte[] body) = await SendProviderRequestAsync(options, streaming);

        Assert.Equal("normal answer", answer);

        using JsonDocument document = JsonDocument.Parse(body);

        Assert.Equal(
            "minimal",
            document.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Theory]
    [InlineData(ReasoningWireDialect.OpenRouter, false)]
    [InlineData(ReasoningWireDialect.OpenRouter, true)]
    [InlineData(ReasoningWireDialect.TopLevelReasoningBudget, false)]
    [InlineData(ReasoningWireDialect.TopLevelReasoningBudget, true)]
    [InlineData(ReasoningWireDialect.AnthropicThinking, false)]
    [InlineData(ReasoningWireDialect.AnthropicThinking, true)]
    public async Task NumericBudgetDialects_BufferedAndStreaming_EmitOnlyConfiguredShape(
        ReasoningWireDialect dialect,
        bool streaming)
    {
        const int budgetTokens = 4096;

        ChatOptions options = new();

        ReasoningChatOptionsAdapter.Apply(
            options,
            new ReasoningRequestOptions(
                BudgetTokens: budgetTokens,
                Output: ReasoningOutputMode.Summary),
            dialect);

        Assert.Null(options.Reasoning?.Effort);
        Assert.Equal(ReasoningOutput.Summary, options.Reasoning?.Output);
        Assert.NotNull(options.RawRepresentationFactory);

        (string answer, byte[] body) = await SendProviderRequestAsync(options, streaming);

        Assert.Equal("normal answer", answer);

        using JsonDocument document = JsonDocument.Parse(body);

        JsonElement root = document.RootElement;

        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.False(root.TryGetProperty("reasoning_output", out _));

        switch (dialect)
        {
            case ReasoningWireDialect.OpenRouter:
                JsonElement reasoning = root.GetProperty("reasoning");

                Assert.Equal(budgetTokens, reasoning.GetProperty("max_tokens").GetInt32());
                Assert.Single(reasoning.EnumerateObject());
                Assert.False(root.TryGetProperty("reasoning_budget", out _));
                Assert.False(root.TryGetProperty("thinking", out _));
                break;

            case ReasoningWireDialect.TopLevelReasoningBudget:
                Assert.Equal(budgetTokens, root.GetProperty("reasoning_budget").GetInt32());
                Assert.False(root.TryGetProperty("reasoning", out _));
                Assert.False(root.TryGetProperty("thinking", out _));
                break;

            case ReasoningWireDialect.AnthropicThinking:
                JsonElement thinking = root.GetProperty("thinking");

                Assert.Equal("enabled", thinking.GetProperty("type").GetString());
                Assert.Equal(budgetTokens, thinking.GetProperty("budget_tokens").GetInt32());
                Assert.Equal(2, thinking.EnumerateObject().Count());
                Assert.False(root.TryGetProperty("reasoning", out _));
                Assert.False(root.TryGetProperty("reasoning_budget", out _));
                break;

            default:
                throw new InvalidOperationException($"Unexpected test dialect '{dialect}'.");
        }
    }

    [Fact]
    public void ClonedRawRepresentationFactory_ReturnsFreshOpenAiOptionsForEveryCall()
    {
        ChatOptions options = new();

        ReasoningChatOptionsAdapter.Apply(
            options,
            new ReasoningRequestOptions(BudgetTokens: 2048),
            ReasoningWireDialect.OpenRouter);

        ChatOptions clone = options.Clone();

        Func<IChatClient, object?> originalFactory = Assert.IsType<Func<IChatClient, object?>>(
            options.RawRepresentationFactory);

        Func<IChatClient, object?> clonedFactory = Assert.IsType<Func<IChatClient, object?>>(
            clone.RawRepresentationFactory);

        using NullChatClient client = new();

        ChatCompletionOptions originalFirst = Assert.IsType<ChatCompletionOptions>(originalFactory(client));
        ChatCompletionOptions originalSecond = Assert.IsType<ChatCompletionOptions>(originalFactory(client));
        ChatCompletionOptions cloneFirst = Assert.IsType<ChatCompletionOptions>(clonedFactory(client));
        ChatCompletionOptions cloneSecond = Assert.IsType<ChatCompletionOptions>(clonedFactory(client));

        Assert.NotSame(originalFirst, originalSecond);
        Assert.NotSame(cloneFirst, cloneSecond);
        Assert.NotSame(originalFirst, cloneFirst);
        Assert.NotSame(originalSecond, cloneSecond);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoReasoning_BufferedAndStreaming_LeavesProviderJsonByteForByteUnchanged(bool streaming)
    {
        ChatOptions baseline = new()
        {
            Temperature = 0.25f,
            MaxOutputTokens = 64,
        };

        ChatOptions mapped = new()
        {
            Temperature = 0.25f,
            MaxOutputTokens = 64,
        };

        ReasoningChatOptionsAdapter.Apply(
            mapped,
            reasoning: null,
            ReasoningWireDialect.AnthropicThinking);

        (_, byte[] baselineBody) = await SendProviderRequestAsync(baseline, streaming);
        (_, byte[] mappedBody) = await SendProviderRequestAsync(mapped, streaming);

        Assert.Null(mapped.Reasoning);
        Assert.Null(mapped.RawRepresentationFactory);
        Assert.Equal(baselineBody, mappedBody);
    }

    private static async Task<(string Answer, byte[] Body)> SendProviderRequestAsync(
        ChatOptions options,
        bool streaming)
    {
        using CapturingProviderHandler handler = new();
        using HttpClient httpClient = new(handler);

        OpenAIClientOptions clientOptions = new()
        {
            Endpoint = new Uri("https://provider.test/v1"),
            Transport = new HttpClientPipelineTransport(httpClient),
        };

        OpenAI.Chat.ChatClient concreteClient = new(
            "reasoning-test-model",
            new ApiKeyCredential("test-key"),
            clientOptions);

        using IChatClient client = concreteClient.AsIChatClient();

        MeAiChatMessage[] messages = [new(ChatRole.User, "solve")];

        string answer;

        if (streaming)
        {
            StringBuilder builder = new();

            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options))
            {
                builder.Append(update.Text);
            }

            answer = builder.ToString();
        }
        else
        {
            ChatResponse response = await client.GetResponseAsync(messages, options);

            answer = response.Text;
        }

        return (answer, Assert.Single(handler.RequestBodies));
    }

    private sealed class CapturingProviderHandler : HttpMessageHandler
    {
        private const string BufferedResponse =
            """
            {
              "id": "chatcmpl-test",
              "object": "chat.completion",
              "created": 1750000000,
              "model": "reasoning-test-model",
              "choices": [
                {
                  "index": 0,
                  "message": {
                    "role": "assistant",
                    "content": "normal answer"
                  },
                  "finish_reason": "stop"
                }
              ],
              "usage": {
                "prompt_tokens": 1,
                "completion_tokens": 2,
                "total_tokens": 3
              }
            }
            """;

        private const string StreamingResponse =
            """
            data: {"id":"chatcmpl-test","object":"chat.completion.chunk","created":1750000000,"model":"reasoning-test-model","choices":[{"index":0,"delta":{"role":"assistant","content":"normal answer"},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        public List<byte[]> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            byte[] body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            RequestBodies.Add(body);

            using JsonDocument document = JsonDocument.Parse(body);

            bool streaming = document.RootElement.TryGetProperty("stream", out JsonElement stream)
                && stream.ValueKind == JsonValueKind.True;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    streaming ? StreamingResponse : BufferedResponse,
                    Encoding.UTF8,
                    streaming ? "text/event-stream" : "application/json"),
            };
        }
    }

    private sealed class NullChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;

            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
