using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ModelCallExecutorTests
{

    [Fact]
    public void ModelCallExecutor_contract_keeps_provider_io_without_counter_gate()
    {
        Assert.Null(typeof(IModelCallExecutor).GetMethod("TryBeginModelCall"));
        Assert.Null(typeof(ModelCallExecutor).GetMethod("TryBeginModelCall"));
        Assert.NotNull(typeof(IModelCallExecutor).GetMethod("ExecuteBufferedAsync"));
        Assert.NotNull(typeof(IModelCallExecutor).GetMethod("ExecuteStreamingAsync"));
    }

    [Fact]
    public async Task ExecuteBufferedAsync_AdmitsCallsBeyondFormerCountCeiling()
    {
        const int callCount = 13;
        ScriptingChatClient chat = new(text: "pong");
        TurnBudget budget = new();
        ModelCallExecutor executor = new();

        for (int call = 0; call < callCount; call++)
        {
            ModelCallOutcome result = await executor.ExecuteBufferedAsync(
                chat,
                [new ChatMessage(ChatRole.User, $"ping-{call}")],
                new ChatOptions(),
                budget,
                ModelCallPurpose.MainInference,
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal("pong", result.Value.Response.Text);
        }

        Assert.Equal(callCount, chat.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_AppliesCacheMetadataOnlyToProviderBoundClones(bool streaming)
    {
        ProviderSettings provider = CacheProvider();
        PromptCachePlan plan = EligiblePlan(provider.Name, "gpt-5");
        ChatMessage originalMessage = new(ChatRole.System, "stable");
        List<ChatMessage> messages = [originalMessage, new(ChatRole.User, "solve")];
        ChatOptions options = new();
        ReasoningChatOptionsAdapter.Apply(
            options,
            new ReasoningRequestOptions(BudgetTokens: 256),
            ReasoningWireDialect.OpenRouter);
        ScriptingChatClient chat = new("answer");
        ModelCallExecutor executor = CreateAccountedExecutor(provider);
        ModelCallContext context = new(
            provider,
            "gpt-5",
            ReservedAnswerTokens: 0,
            ReservedReasoningTokens: 0,
            PromptCachePlan: plan);

        if (streaming)
        {
            await foreach (ModelCallUpdate _ in executor.ExecuteStreamingAsync(
                chat,
                messages,
                options,
                new TurnBudget(),
                ModelCallPurpose.MainInference,
                CancellationToken.None,
                context))
            {
            }
        }
        else
        {
            ModelCallOutcome outcome = await executor.ExecuteBufferedAsync(
                chat,
                messages,
                options,
                new TurnBudget(),
                ModelCallPurpose.MainInference,
                CancellationToken.None,
                context);

            Assert.True(outcome.IsSuccess);
        }

        Assert.NotNull(chat.LastOptions?.RawRepresentationFactory);
        Assert.NotSame(options, chat.LastOptions);
        Assert.NotNull(chat.LastMessages);
        Assert.Equal(messages.Count, chat.LastMessages!.Count);
        Assert.NotSame(originalMessage, chat.LastMessages[0]);
        Assert.Same(originalMessage, messages[0]);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_RejectsCachePlanForDifferentProviderBeforeIo()
    {
        ProviderSettings provider = CacheProvider();
        PromptCachePlan plan = EligiblePlan("other-provider", "gpt-5");
        ScriptingChatClient chat = new("answer");

        ModelCallOutcome outcome = await CreateAccountedExecutor(provider).ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.System, "stable")],
            new ChatOptions(),
            new TurnBudget(),
            ModelCallPurpose.MainInference,
            CancellationToken.None,
            new ModelCallContext(
                provider,
                "gpt-5",
                0,
                0,
                PromptCachePlan: plan));

        Assert.True(outcome.IsFailure);
        Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, outcome.Error.Code);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_RecordsPerCallCacheHitTokensAndSavingsOnce()
    {
        string marker = Guid.NewGuid().ToString("N");
        ProviderSettings provider = CacheProvider() with { Name = marker };
        PromptCachePlan plan = EligiblePlan(marker, "gpt-5") with
        {
            EligiblePrefixTokenEstimate = 25,
        };
        ChatResponse response = new(new ChatMessage(ChatRole.Assistant, "answer"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 10,
                TotalTokenCount = 110,
                CachedInputTokenCount = 40,
            },
        };
        ScriptingChatClient chat = new(string.Empty) { BufferedResponse = response };
        PricingSettings pricing = new()
        {
            ModelPricing =
            {
                ["gpt-5"] = new ModelPricingEntry
                {
                    InputPer1M = 10m,
                    CachedPer1M = 2m,
                },
            },
        };
        ConcurrentDictionary<string, ConcurrentQueue<double>> captured =
            new(StringComparer.Ordinal);
        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) =>
                activeListener.EnableMeasurementEvents(instrument),
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (TagsContainMarker(tags, marker))
            {
                captured
                    .GetOrAdd(instrument.Name, static _ => new ConcurrentQueue<double>())
                    .Enqueue(measurement);
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            if (TagsContainMarker(tags, marker))
            {
                captured
                    .GetOrAdd(instrument.Name, static _ => new ConcurrentQueue<double>())
                    .Enqueue(measurement);
            }
        });
        listener.Start();

        ModelCallOutcome outcome = await CreateAccountedExecutor(provider, pricing)
            .ExecuteBufferedAsync(
                chat,
                [new ChatMessage(ChatRole.System, "stable")],
                new ChatOptions(),
                new TurnBudget(),
                ModelCallPurpose.MainInference,
                CancellationToken.None,
                new ModelCallContext(
                    provider,
                    "gpt-5",
                    0,
                    0,
                    PromptCachePlan: plan));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(1, Assert.Single(captured["arcanum_prompt_cache_calls_total"]));
        Assert.Equal(40, Assert.Single(captured["arcanum_prompt_cache_tokens_total"]));
        Assert.Equal(1, Assert.Single(captured["arcanum_prompt_cache_hits_total"]));
        Assert.Equal(
            0.0002,
            Assert.Single(captured["arcanum_prompt_cache_potential_savings_usd_total"]),
            8);
        Assert.Equal(
            0.00032,
            Assert.Single(captured["arcanum_prompt_cache_actual_savings_usd_total"]),
            8);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_UnknownCatalogProfileDoesNotClaimCachedUsage()
    {
        string marker = Guid.NewGuid().ToString("N");
        ProviderSettings provider = new()
        {
            Name = marker,
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://unknown.example/v1",
            Models = ["unknown-model"],
        };
        ChatResponse response = new(new ChatMessage(ChatRole.Assistant, "answer"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 10,
                CachedInputTokenCount = 40,
            },
        };
        ConcurrentQueue<long> captured = new();
        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) =>
                activeListener.EnableMeasurementEvents(instrument),
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "arcanum_prompt_cache_tokens_total"
                && TagsContainMarker(tags, marker))
            {
                captured.Enqueue(measurement);
            }
        });
        listener.Start();

        ModelCallOutcome outcome = await CreateAccountedExecutor(provider).ExecuteBufferedAsync(
            new ScriptingChatClient(string.Empty) { BufferedResponse = response },
            [new ChatMessage(ChatRole.System, "stable")],
            new ChatOptions(),
            new TurnBudget(),
            ModelCallPurpose.MainInference,
            CancellationToken.None,
            new ModelCallContext(
                provider,
                "unknown-model",
                0,
                0));

        Assert.True(outcome.IsSuccess);
        Assert.Empty(captured);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_PreservesTypedProviderFailure()
    {
        HttpRequestException connectivityFailure = new("connection refused");
        ScriptingChatClient chat = new(text: string.Empty)
        {
            BufferedException = connectivityFailure,
        };

        ModelCallOutcome outcome = await new ModelCallExecutor().ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "ping")],
            new ChatOptions(),
            new TurnBudget(),
            ModelCallPurpose.MainInference,
            CancellationToken.None);

        Assert.True(outcome.IsFailure);
        Assert.Same(connectivityFailure, outcome.Failure.Cause);
        Assert.Equal("connection refused", outcome.Error.Message);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_YieldsTextDeltas()
    {
        ScriptingChatClient chat = new(text: "ab");

        TurnBudget budget = new();

        ModelCallExecutor executor = new();

        List<ModelCallUpdate> updates = [];

        await foreach (ModelCallUpdate update in executor.ExecuteStreamingAsync(
            chat,
            [new ChatMessage(ChatRole.User, "ping")],
            new ChatOptions(),
            budget,
            ModelCallPurpose.MainInference,
            CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Contains(updates, u => u is ModelCallTextDelta);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_ExtractsReasoningWithoutContaminatingAnswer()
    {
        TextReasoningContent reasoning = new("visible summary")
        {
            ProtectedData = "provider-opaque",
        };
        UsageDetails usage = new()
        {
            InputTokenCount = 3,
            OutputTokenCount = 5,
            ReasoningTokenCount = 2,
        };
        ChatResponse response = new(new ChatMessage(
            ChatRole.Assistant,
            [
                reasoning,
                new TextContent("final answer"),
            ]))
        {
            Usage = usage,
        };
        ScriptingChatClient chat = new(text: string.Empty)
        {
            BufferedResponse = response,
        };

        ModelCallExecutor executor = new();

        ModelCallOutcome result = await executor.ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "reason")],
            new ChatOptions
            {
                Reasoning = new ReasoningOptions { Output = ReasoningOutput.Summary },
            },
            new TurnBudget(),
            ModelCallPurpose.MainInference,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("final answer", result.Value.Response.Text);
        Assert.Same(response, result.Value.Response);
        Assert.Same(usage, result.Value.Usage);
        Assert.Equal(2, result.Value.Usage?.ReasoningTokenCount);
        Assert.True(result.Value.Reasoning.HasProviderContent);
        Assert.True(result.Value.Reasoning.HasProtectedData);
        Assert.Equal(ReasoningOutputMode.Summary, result.Value.Reasoning.RequestedOutput);
        Assert.Equal(ReasoningOutputMode.Summary, result.Value.Reasoning.EffectiveOutput);
        ModelCallReasoningSegment segment = Assert.Single(result.Value.Reasoning.Segments);
        Assert.Equal("visible summary", segment.VisibleText);
        Assert.Equal(ReasoningOutputMode.Summary, segment.RequestedOutput);
        Assert.Equal(ReasoningOutputMode.Summary, segment.EffectiveOutput);
        Assert.True(segment.HasProtectedData);
        Assert.Same(reasoning, Assert.Single(result.Value.Response.Messages[0].Contents.OfType<TextReasoningContent>()));
    }

    [Fact]
    public async Task ExecuteBufferedAsync_ProviderIgnoringDisabledOutput_CommitsButDoesNotExposeText()
    {
        ChatResponse response = new(new ChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent("must remain hidden"),
                new TextContent("answer"),
            ]));
        ScriptingChatClient chat = new(text: string.Empty)
        {
            BufferedResponse = response,
        };

        ModelCallOutcome result = await new ModelCallExecutor().ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "reason")],
            new ChatOptions
            {
                Reasoning = new ReasoningOptions { Output = ReasoningOutput.None },
            },
            new TurnBudget(),
            ModelCallPurpose.MainInference,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("answer", result.Value.Response.Text);
        Assert.True(result.Value.Reasoning.HasProviderContent);
        Assert.False(result.Value.Reasoning.HasProtectedData);
        Assert.Equal(ReasoningOutputMode.None, result.Value.Reasoning.RequestedOutput);
        Assert.Equal(ReasoningOutputMode.None, result.Value.Reasoning.EffectiveOutput);
        Assert.Empty(result.Value.Reasoning.Segments);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_AuxiliaryPurposeNeverExposesReasoning()
    {
        ScriptingChatClient chat = new(text: string.Empty)
        {
            BufferedResponse = new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                [new TextReasoningContent("auxiliary secret"), new TextContent("{}")])),
        };

        ModelCallOutcome result = await new ModelCallExecutor().ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "route")],
            new ChatOptions
            {
                Reasoning = new ReasoningOptions { Output = ReasoningOutput.Summary },
            },
            new TurnBudget(),
            ModelCallPurpose.SpellRouting,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Reasoning.HasProviderContent);
        Assert.Equal(ReasoningOutputMode.Summary, result.Value.Reasoning.RequestedOutput);
        Assert.Equal(ReasoningOutputMode.None, result.Value.Reasoning.EffectiveOutput);
        Assert.Empty(result.Value.Reasoning.Segments);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_StructuredOutputRetryExposesReplacementReasoning()
    {
        ScriptingChatClient chat = new(text: string.Empty)
        {
            BufferedResponse = new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                [
                    new TextReasoningContent("replacement reasoning"),
                    new TextContent("""{"name":"fixed"}"""),
                ])),
        };

        ModelCallOutcome result = await new ModelCallExecutor().ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "repair")],
            new ChatOptions
            {
                Reasoning = new ReasoningOptions { Output = ReasoningOutput.Summary },
            },
            new TurnBudget(),
            ModelCallPurpose.StructuredOutputRetry,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        ModelCallReasoningSegment segment = Assert.Single(result.Value.Reasoning.Segments);
        Assert.Equal("replacement reasoning", segment.VisibleText);
        Assert.Equal(ReasoningOutputMode.Summary, segment.EffectiveOutput);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_CoalescesOnlyAdjacentCompatibleReasoningSegments()
    {
        ChatResponse response = new(new ChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent("one"),
                new TextReasoningContent(" two"),
                new TextReasoningContent("three") { ProtectedData = "opaque" },
                new TextReasoningContent(" four") { ProtectedData = "opaque-2" },
                new TextReasoningContent("five"),
                new TextContent("answer"),
            ]));
        ScriptingChatClient chat = new(text: string.Empty)
        {
            BufferedResponse = response,
        };

        ModelCallOutcome outcome = await new ModelCallExecutor().ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "reason")],
            new ChatOptions
            {
                Reasoning = new ReasoningOptions { Output = ReasoningOutput.Summary },
            },
            new TurnBudget(),
            ModelCallPurpose.MainInference,
            CancellationToken.None);

        Assert.True(outcome.IsSuccess);
        Assert.Collection(
            outcome.Value.Reasoning.Segments,
            segment =>
            {
                Assert.Equal("one two", segment.VisibleText);
                Assert.False(segment.HasProtectedData);
            },
            segment =>
            {
                Assert.Equal("three four", segment.VisibleText);
                Assert.True(segment.HasProtectedData);
            },
            segment =>
            {
                Assert.Equal("five", segment.VisibleText);
                Assert.False(segment.HasProtectedData);
            });
    }

    [Fact]
    public async Task ExecuteBufferedAsync_CoalescesHighReasoningDeltaCountIntoOneSegment()
    {
        const int deltaCount = 10_000;
        AIContent[] contents =
        [
            .. Enumerable.Range(0, deltaCount)
                .Select(static _ => (AIContent)new TextReasoningContent("x")),
            new TextContent("answer"),
        ];
        ScriptingChatClient chat = new(text: string.Empty)
        {
            BufferedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)),
        };

        ModelCallOutcome outcome = await new ModelCallExecutor().ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "reason")],
            new ChatOptions
            {
                Reasoning = new ReasoningOptions { Output = ReasoningOutput.Summary },
            },
            new TurnBudget(),
            ModelCallPurpose.MainInference,
            CancellationToken.None);

        Assert.True(outcome.IsSuccess);
        ModelCallReasoningSegment segment = Assert.Single(outcome.Value.Reasoning.Segments);
        Assert.Equal(deltaCount, segment.VisibleText.Length);
        Assert.All(segment.VisibleText, static character => Assert.Equal('x', character));
    }

    [Fact]
    public void ModelCallOutcome_FactoriesRejectNull()
    {
        Assert.Throws<ArgumentNullException>(() => ModelCallOutcome.Success(null!));
        Assert.Throws<ArgumentNullException>(() => ModelCallOutcome.Failed(null!));
    }

    [Fact]
    public void ModelCallOutcome_ExposesExactlyOneArm()
    {
        ModelCallResult value = new(
            ModelCallPurpose.MainInference,
            "success-call",
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")),
            Usage: null,
            new ModelCallReasoningResult(
                [],
                RequestedOutput: null,
                ReasoningOutputMode.None,
                HasProviderContent: false,
                HasProtectedData: false));
        ModelCallFailure failure = new(
            ModelCallPurpose.MainInference,
            "failed-call",
            new Error(ErrorCodes.Hub.Error, "failed"),
            new HttpRequestException("failed"));

        ModelCallOutcome success = ModelCallOutcome.Success(value);
        ModelCallOutcome failed = ModelCallOutcome.Failed(failure);

        Assert.True(success.IsSuccess);
        Assert.False(success.IsFailure);
        Assert.Same(value, success.Value);
        Assert.Throws<InvalidOperationException>(() => success.Failure);

        Assert.True(failed.IsFailure);
        Assert.False(failed.IsSuccess);
        Assert.Same(failure, failed.Failure);
        Assert.Throws<InvalidOperationException>(() => failed.Value);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_EmitsInterleavedSemanticUpdatesInContentOrder()
    {
        ChatResponseUpdate mixed = new(
            ChatRole.Assistant,
            [
                new TextReasoningContent("think-1"),
                new TextContent("answer-1"),
                new TextReasoningContent("think-2"),
                new TextContent("answer-2"),
            ]);
        ScriptingChatClient chat = new(text: string.Empty)
        {
            StreamingUpdates = [mixed],
        };
        List<ModelCallUpdate> updates = [];

        await foreach (ModelCallUpdate update in new ModelCallExecutor().ExecuteStreamingAsync(
            chat,
            [new ChatMessage(ChatRole.User, "reason")],
            new ChatOptions
            {
                Reasoning = new ReasoningOptions { Output = ReasoningOutput.Full },
            },
            new TurnBudget(),
            ModelCallPurpose.MainInference,
            CancellationToken.None))
        {
            updates.Add(update);
        }

        ModelCallUpdate[] semantic = updates
            .Where(static update => update is ModelCallTextDelta or ModelCallReasoningUpdate)
            .ToArray();
        Assert.Collection(
            semantic,
            update => Assert.Equal("think-1", Assert.IsType<ModelCallReasoningUpdate>(update).VisibleText),
            update => Assert.Equal("answer-1", Assert.IsType<ModelCallTextDelta>(update).Text),
            update => Assert.Equal("think-2", Assert.IsType<ModelCallReasoningUpdate>(update).VisibleText),
            update => Assert.Equal("answer-2", Assert.IsType<ModelCallTextDelta>(update).Text));
        Assert.All(
            semantic.OfType<ModelCallReasoningUpdate>(),
            update =>
            {
                Assert.Equal(ReasoningOutputMode.Full, update.RequestedOutput);
                Assert.Equal(ReasoningOutputMode.Full, update.EffectiveOutput);
                Assert.False(update.HasProtectedData);
            });
        Assert.Same(mixed, Assert.Single(updates.OfType<ModelCallResponseUpdate>()).Update);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_EmitsProtectedOnlyReasoningBeforeRawResponse()
    {
        TextReasoningContent protectedReasoning = new(string.Empty)
        {
            ProtectedData = "opaque-roundtrip",
        };
        ChatResponseUpdate raw = new(ChatRole.Assistant, [protectedReasoning]);
        ScriptingChatClient chat = new(text: string.Empty)
        {
            StreamingUpdates = [raw],
        };
        List<ModelCallUpdate> updates = [];

        await foreach (ModelCallUpdate update in new ModelCallExecutor().ExecuteStreamingAsync(
            chat,
            [new ChatMessage(ChatRole.User, "reason")],
            new ChatOptions(),
            new TurnBudget(),
            ModelCallPurpose.MainInference,
            CancellationToken.None))
        {
            updates.Add(update);
        }

        ModelCallReasoningUpdate reasoning = Assert.Single(updates.OfType<ModelCallReasoningUpdate>());
        ModelCallResponseUpdate response = Assert.Single(updates.OfType<ModelCallResponseUpdate>());
        Assert.Empty(reasoning.VisibleText);
        Assert.Null(reasoning.RequestedOutput);
        Assert.Equal(ReasoningOutputMode.Full, reasoning.EffectiveOutput);
        Assert.True(reasoning.HasProtectedData);
        Assert.True(updates.IndexOf(reasoning) < updates.IndexOf(response));
        Assert.Same(protectedReasoning, Assert.Single(response.Update.Contents.OfType<TextReasoningContent>()));
    }

    [Fact]
    public async Task ExecuteStreamingAsync_SurfacesReasoningUsage()
    {
        UsageDetails usage = new()
        {
            OutputTokenCount = 11,
            ReasoningTokenCount = 7,
        };
        ScriptingChatClient chat = new(text: string.Empty)
        {
            StreamingUpdates =
            [
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("answer")]),
                new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)]),
                new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("hidden")]),
            ],
        };
        List<ModelCallUpdate> updates = [];

        await foreach (ModelCallUpdate update in new ModelCallExecutor().ExecuteStreamingAsync(
            chat,
            [new ChatMessage(ChatRole.User, "reason")],
            new ChatOptions(),
            new TurnBudget(),
            ModelCallPurpose.MainInference,
            CancellationToken.None))
        {
            updates.Add(update);
        }

        ModelCallUsageUpdate usageUpdate = Assert.Single(updates.OfType<ModelCallUsageUpdate>());
        Assert.Same(usage, usageUpdate.Usage);
        Assert.Equal(7, usageUpdate.Usage?.ReasoningTokenCount);
        Assert.Single(updates.OfType<ModelCallReasoningUpdate>());
    }

    [Fact]
    public async Task ExecuteStreamingAsync_UsageFreeUpdatesDoNotEmitUsageOrCrash()
    {
        ScriptingChatClient chat = new(text: string.Empty)
        {
            StreamingUpdates =
            [
                new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("thinking")]),
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("answer")]),
            ],
        };
        List<ModelCallUpdate> updates = [];

        await foreach (ModelCallUpdate update in new ModelCallExecutor().ExecuteStreamingAsync(
            chat,
            [new ChatMessage(ChatRole.User, "reason")],
            new ChatOptions(),
            new TurnBudget(),
            ModelCallPurpose.MainInference,
            CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Empty(updates.OfType<ModelCallUsageUpdate>());
        Assert.Single(updates.OfType<ModelCallReasoningUpdate>());
        Assert.Single(updates.OfType<ModelCallTextDelta>());
    }

    private sealed class ScriptingChatClient(string text) : IChatClient
    {

        public int CallCount { get; private set; }

        public ChatResponse? BufferedResponse { get; init; }

        public Exception? BufferedException { get; init; }

        public IReadOnlyList<ChatResponseUpdate>? StreamingUpdates { get; init; }

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastMessages = chatMessages.ToList();
            LastOptions = options;

            if (BufferedException is not null)
            {
                throw BufferedException;
            }

            return Task.FromResult(
                BufferedResponse ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastMessages = chatMessages.ToList();
            LastOptions = options;

            if (StreamingUpdates is not null)
            {
                foreach (ChatResponseUpdate update in StreamingUpdates)
                {
                    yield return update;

                    await Task.Yield();
                }

                yield break;
            }

            foreach (char c in text)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, c.ToString());

                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

    }

    private static ProviderSettings CacheProvider()
    {
        return new ProviderSettings
        {
            Name = "provider",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.openai.com/v1",
            Models = ["gpt-5"],
        };
    }

    private static PromptCachePlan EligiblePlan(string provider, string model) =>
        new(
            "arcanum-pc-v1-test",
            provider,
            model,
            PromptCacheSemanticNamespace.Main,
            PromptCacheRetentionPolicy.ProviderDefault,
            [new PromptCacheBoundary(0, 0)],
            "stable",
            string.Empty,
            1,
            PromptCacheEligibility.Eligible,
            PromptCacheNonEligibilityReason.None);

    private static ModelCallExecutor CreateAccountedExecutor(
        ProviderSettings provider,
        PricingSettings? pricing = null)
    {
        ArcanumSettings settings = new()
        {
            Providers = [provider],
            Cost = new CostSettings
            {
                Pricing = pricing ?? new PricingSettings(),
            },
        };
        InferenceTokenizerResolver tokenizer = new(
            NullLogger<InferenceTokenizerResolver>.Instance);
        ModelTokenEstimator estimator = new(
            tokenizer,
            new TestOptionsMonitor<ArcanumSettings>(settings));

        TestOptionsMonitor<ArcanumSettings> monitor = new(settings);

        return new ModelCallExecutor(estimator, monitor);
    }

    private static bool TagsContainMarker(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        string marker)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Value is string value && string.Equals(value, marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

}
