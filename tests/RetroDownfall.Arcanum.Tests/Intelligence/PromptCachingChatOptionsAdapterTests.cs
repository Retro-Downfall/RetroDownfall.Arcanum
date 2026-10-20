using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Tests.Support;
using OpenAI;
using OpenAI.Chat;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class PromptCachingChatOptionsAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAiPromptCacheRetention_KeyOnly_EmitsExactRootField(bool streaming)
    {
        ChatOptions options = new();

        PromptCachingChatOptionsAdapter.Apply(
            options,
            ExplicitProfile(PromptCacheRetentionPolicy.ProviderDefault),
            EligiblePlan(PromptCacheRetentionPolicy.ProviderDefault));

        byte[] body = await SendProviderRequestAsync(options, streaming);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        Assert.Equal("arcanum-pc-v1-opaque", root.GetProperty("prompt_cache_key").GetString());
        Assert.False(root.TryGetProperty("prompt_cache_retention", out _));
        Assert.False(root.TryGetProperty("prompt_cache_options", out _));
        Assert.False(body.AsSpan().IndexOf("\"prompt_cache_breakpoint\""u8) >= 0);
    }

    [Theory]
    [InlineData(PromptCacheRetentionPolicy.InMemory, "in_memory", false)]
    [InlineData(PromptCacheRetentionPolicy.InMemory, "in_memory", true)]
    [InlineData(PromptCacheRetentionPolicy.TwentyFourHours, "24h", false)]
    [InlineData(PromptCacheRetentionPolicy.TwentyFourHours, "24h", true)]
    public async Task OpenAiPromptCacheRetention_EmitsVerifiedRetentionSpelling(
        PromptCacheRetentionPolicy retention,
        string expected,
        bool streaming)
    {
        ChatOptions options = new();

        PromptCachingChatOptionsAdapter.Apply(
            options,
            ExplicitProfile(retention),
            EligiblePlan(retention));

        byte[] body = await SendProviderRequestAsync(options, streaming);

        using JsonDocument document = JsonDocument.Parse(body);

        Assert.Equal(
            expected,
            document.RootElement.GetProperty("prompt_cache_retention").GetString());
    }

    [Fact]
    public async Task Apply_ComposesWithExistingReasoningRawRepresentationFactory()
    {
        ChatOptions options = new();

        ReasoningChatOptionsAdapter.Apply(
            options,
            new ReasoningRequestOptions(BudgetTokens: 2048),
            ReasoningWireDialect.OpenRouter);
        PromptCachingChatOptionsAdapter.Apply(
            options,
            ExplicitProfile(PromptCacheRetentionPolicy.InMemory),
            EligiblePlan(PromptCacheRetentionPolicy.InMemory));

        byte[] body = await SendProviderRequestAsync(options, streaming: false);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        Assert.Equal(
            2048,
            root.GetProperty("reasoning").GetProperty("max_tokens").GetInt32());
        Assert.Equal("arcanum-pc-v1-opaque", root.GetProperty("prompt_cache_key").GetString());
        Assert.Equal("in_memory", root.GetProperty("prompt_cache_retention").GetString());
    }

    [Theory]
    [InlineData(PromptCacheEligibility.ProviderManaged, false)]
    [InlineData(PromptCacheEligibility.ProviderManaged, true)]
    [InlineData(PromptCacheEligibility.NonCacheable, false)]
    [InlineData(PromptCacheEligibility.NonCacheable, true)]
    public async Task Apply_IneligiblePlan_LeavesProviderBodyByteForByteUnchanged(
        PromptCacheEligibility eligibility,
        bool streaming)
    {
        ChatOptions baseline = new() { Temperature = 0.25f, MaxOutputTokens = 64 };
        ChatOptions mapped = baseline.Clone();
        PromptCachePlan plan = PromptCachePlan.NonCacheable(
            "provider",
            "cache-model",
            PromptCacheSemanticNamespace.Main,
            eligibility,
            eligibility == PromptCacheEligibility.ProviderManaged
                ? PromptCacheNonEligibilityReason.ProviderManaged
                : PromptCacheNonEligibilityReason.DisabledByProfile);

        PromptCachingChatOptionsAdapter.Apply(
            mapped,
            new PromptCachingProfile { ControlMode = PromptCachingControlMode.ProviderManaged },
            plan);

        byte[] baselineBody = await SendProviderRequestAsync(baseline, streaming);
        byte[] mappedBody = await SendProviderRequestAsync(mapped, streaming);

        Assert.Equal(baselineBody, mappedBody);
        Assert.Null(mapped.RawRepresentationFactory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executor_protected_history_without_prompt_markers_suppresses_cache_directives(bool streaming)
    {
        ChatOptions options = new();

        ReasoningChatOptionsAdapter.Apply(options, new ReasoningRequestOptions(BudgetTokens: 2048), ReasoningWireDialect.OpenRouter);

        byte[] body = await SendProviderRequestAsync(options, streaming, protectedHistory: true);

        using JsonDocument document = JsonDocument.Parse(body);

        Assert.False(document.RootElement.TryGetProperty("prompt_cache_key", out _));

        Assert.False(document.RootElement.TryGetProperty("prompt_cache_retention", out _));

        Assert.Equal(2048, document.RootElement.GetProperty("reasoning").GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData("gpt-5.6")]
    [InlineData("gpt-5.6-mini")]
    [InlineData("gpt-6")]
    public void New_model_generation_has_no_unverified_explicit_cache_contract(string model)
    {
        ProviderSettings provider = new() { Name = "openai", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://api.openai.com/v1", Models = [model] };

        Assert.Null(ModelCapabilityCatalog.ResolvePromptCaching(provider, model));

        if (model.StartsWith("gpt-5.", StringComparison.Ordinal))
        {
            Assert.Equal("o200k_base", ModelCapabilityCatalog.Resolve(provider, model)?.Tokenization.TokenizerId);
        }
    }

    private static PromptCachingProfile ExplicitProfile(PromptCacheRetentionPolicy retention) =>
        new()
        {
            ControlMode = PromptCachingControlMode.Explicit,
            WireDialect = PromptCachingWireDialect.OpenAiPromptCacheRetention,
            CacheKeysSupported = true,
            EmitCacheKey = true,
            RetentionSelectionSupported = retention != PromptCacheRetentionPolicy.ProviderDefault,
            Retention = retention,
        };

    private static PromptCachePlan EligiblePlan(PromptCacheRetentionPolicy retention) =>
        new(
            "arcanum-pc-v1-opaque",
            "provider",
            "cache-model",
            PromptCacheSemanticNamespace.Main,
            retention,
            [new PromptCacheBoundary(0, 0)],
            "stable-digest",
            string.Empty,
            20,
            PromptCacheEligibility.Eligible,
            PromptCacheNonEligibilityReason.None);

    private static async Task<byte[]> SendProviderRequestAsync(
        ChatOptions options,
        bool streaming,
        bool protectedHistory = false)
    {
        using CapturingProviderHandler handler = new();
        using HttpClient httpClient = new(handler);
        OpenAIClientOptions clientOptions = new()
        {
            Endpoint = new Uri("https://provider.test/v1"),
            Transport = new HttpClientPipelineTransport(httpClient),
        };
        OpenAI.Chat.ChatClient concreteClient = new(
            "cache-model",
            new ApiKeyCredential("test-key"),
            clientOptions);
        using IChatClient client = concreteClient.AsIChatClient();
        MeAiChatMessage[] messages = [new(ChatRole.System, "stable"), new(ChatRole.User, "solve")];

        if (protectedHistory)
        {
            ProviderSettings provider = new() { Name = "provider", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://api.openai.com/v1", Models = ["gpt-5"] };

            ModelCallExecutor executor = new(new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)));

            ModelCallContext context = new(provider, "gpt-5", 0, 0,
                PromptCachePlan: EligiblePlan(PromptCacheRetentionPolicy.ProviderDefault) with { Model = "gpt-5" },
                Sensitivity: ContentSensitivity.CovenantDerived);

            if (streaming)
            {
                await foreach (var update in executor.ExecuteStreamingAsync(client, messages, options, UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context))
                {
                    Assert.IsNotType<ModelCallFailureUpdate>(update);
                }
            }
            else
            {
                Assert.True((await executor.ExecuteBufferedAsync(client, messages, options, UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None, context)).IsSuccess);
            }
        }
        else if (streaming)
        {
            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(messages, options))
            {
            }
        }
        else
        {
            _ = await client.GetResponseAsync(messages, options);
        }

        return Assert.Single(handler.RequestBodies);
    }

    private sealed class CapturingProviderHandler : HttpMessageHandler
    {
        private const string BufferedResponse =
            """
            {
              "id": "chatcmpl-test",
              "object": "chat.completion",
              "created": 1750000000,
              "model": "cache-model",
              "choices": [
                {
                  "index": 0,
                  "message": {"role": "assistant", "content": "answer"},
                  "finish_reason": "stop"
                }
              ],
              "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2}
            }
            """;

        private const string StreamingResponse =
            """
            data: {"id":"chatcmpl-test","object":"chat.completion.chunk","created":1750000000,"model":"cache-model","choices":[{"index":0,"delta":{"role":"assistant","content":"answer"},"finish_reason":"stop"}]}

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
}
