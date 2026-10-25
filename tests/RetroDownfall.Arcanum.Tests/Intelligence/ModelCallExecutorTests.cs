using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ModelCallExecutorTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoked_capsule_output_guard_refuses_the_physical_provider_send(bool streaming)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        var consumed = await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest);
        Assert.True(consumed.IsSuccess);
        var binding = new RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation.MemoryDelegationRootBinding(
            new PingRequest("parent"), h.Source.Invocation, h.RootId, root, h.Coordinator);
        var protection = binding.CaptureOutputProtection();
        await using (await h.Coordinator.BeginMutationAsync()) { }
        var provider = CacheProvider();
        var executor = CreateAccountedExecutor(provider);
        ScriptingChatClient chat = new("answer");
        var context = new ModelCallContext(provider, "gpt-5", 0, 0, ProtectedBinding: protection);
        if (streaming)
        {
            await foreach (var _ in executor.ExecuteStreamingAsync(chat, [new ChatMessage(ChatRole.User, "derived input")],
                new ChatOptions(), UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, default, context)) { }
        }
        else
        {
            await executor.ExecuteBufferedAsync(chat, [new ChatMessage(ChatRole.User, "derived input")],
                new ChatOptions(), UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, default, context);
        }
        Assert.Equal(0, chat.CallCount);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_provider_effect_drains_only_after_consumer_teardown(bool streaming)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).IsSuccess);
        var binding = new RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation.MemoryDelegationRootBinding(
            new PingRequest("parent"), h.Source.Invocation, h.RootId, root, h.Coordinator);
        var provider = CacheProvider();
        var executor = CreateAccountedExecutor(provider);
        var chat = new TeardownChatClient();
        var context = new ModelCallContext(provider, "gpt-5", 0, 0, ProtectedBinding: binding.CaptureOutputProtection());
        async Task Call()
        {
            if (streaming)
            {
                await foreach (var _ in executor.ExecuteStreamingAsync(chat, [new(ChatRole.User, "derived")], new(),
                    UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, default, context)) { }
            }
            else await executor.ExecuteBufferedAsync(chat, [new(ChatRole.User, "derived")], new(),
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, default, context);
        }

        var running = Call();
        await chat.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var mutation = h.Coordinator.BeginMutationAsync().AsTask();
        try
        {
            await chat.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(mutation.IsCompleted);
            Assert.False(running.IsCompleted);
        }
        finally { chat.Teardown.TrySetResult(); }
        try { await running.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        await using var writer = await mutation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(chat.Disposed);
    }

    private sealed class TeardownChatClient : IChatClient
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Teardown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            await Teardown.Task;
            Disposed = true;
            cancellationToken.ThrowIfCancellationRequested();
            return new(new ChatMessage(ChatRole.Assistant, "answer"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            try
            {
                Entered.TrySetResult();
                await Cancelled.Task;
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(ChatRole.Assistant, "answer");
            }
            finally { await Teardown.Task; Disposed = true; }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [SkippableFact]
    public async Task Ordinary_sensitivity_capsule_provider_failure_never_retains_exception_content()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).IsSuccess);
        var binding = new RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation.MemoryDelegationRootBinding(
            new PingRequest("parent"), h.Source.Invocation, h.RootId, root, h.Coordinator);
        var provider = CacheProvider();
        var executor = CreateAccountedExecutor(provider);
        var chat = new ScriptingChatClient("answer") { BufferedException = new InvalidOperationException("private source echo") };
        var outcome = await executor.ExecuteBufferedAsync(chat, [new(ChatRole.User, "derived")], new(), UnrestrictedTurnBudget.Instance,
            ModelCallPurpose.MainInference, default, new(provider, "gpt-5", 0, 0, ProtectedBinding: binding.CaptureOutputProtection()));
        Assert.False(outcome.IsSuccess);
        Assert.Null(outcome.Failure.Cause);
        Assert.DoesNotContain("private source echo", outcome.Failure.Error.Message);
    }

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
        ITurnBudget budget = UnrestrictedTurnBudget.Instance;
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
                UnrestrictedTurnBudget.Instance,
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
                UnrestrictedTurnBudget.Instance,
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
            UnrestrictedTurnBudget.Instance,
            ModelCallPurpose.MainInference,
            CancellationToken.None,
            new ModelCallContext(
                provider,
                "gpt-5",
                0,
                0,
                PromptCachePlan: plan));

        Assert.True(outcome.IsFailure);
        Assert.Equal("Hub.InvalidAdmission", outcome.Error.Code);
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
                UnrestrictedTurnBudget.Instance,
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
            UnrestrictedTurnBudget.Instance,
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
    public async Task ExecuteBufferedAsync_ProviderFailure_PreservesCauseButSanitizesPublicError()
    {
        const string canary = "CANARY_PROVIDER_SECRET_RESPONSE_BODY";
        HttpRequestException connectivityFailure = new(canary);
        ScriptingChatClient chat = new(text: string.Empty)
        {
            BufferedException = connectivityFailure,
        };
        TestCapturingLogger<ModelCallExecutor> logger = new();

        ModelCallOutcome outcome = await new ModelCallExecutor(logger: logger).ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "ping")],
            new ChatOptions(),
            UnrestrictedTurnBudget.Instance,
            ModelCallPurpose.MainInference,
            CancellationToken.None);

        Assert.True(outcome.IsFailure);
        Assert.Same(connectivityFailure, outcome.Failure.Cause);
        Assert.Equal(
            "Inference failed. Ensure the provider is running and reachable, then try again. See server logs for details.",
            outcome.Error.Message);

        string serialized = JsonSerializer.Serialize(
            outcome.Error,
            ArcanumJsonContext.Default.Error);
        Assert.DoesNotContain(canary, serialized, StringComparison.Ordinal);

        TestLogEntry log = Assert.Single(logger.Entries);
        Assert.Null(log.Exception);
        Assert.DoesNotContain(canary, log.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(HttpRequestException), log.Message, StringComparison.Ordinal);
        Assert.Contains(outcome.Failure.ModelCallId, log.Message, StringComparison.Ordinal);
        Assert.Contains(
            nameof(ModelCallPurpose.MainInference),
            log.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_NoncancelledProviderOce_ReturnsSanitizedFailure()
    {
        const string canary = "CANARY_PROVIDER_CANCELLATION_BODY";
        ScriptingChatClient chat = new(text: string.Empty)
        {
            BufferedException = new OperationCanceledException(canary),
        };
        TestCapturingLogger<ModelCallExecutor> logger = new();

        ModelCallOutcome outcome = await new ModelCallExecutor(logger: logger).ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "ping")],
            new ChatOptions(),
            UnrestrictedTurnBudget.Instance,
            ModelCallPurpose.MainInference,
            CancellationToken.None);

        Assert.True(outcome.IsFailure);
        Assert.Equal(
            "Inference failed. Ensure the provider is running and reachable, then try again. See server logs for details.",
            outcome.Failure?.Error.Message);
        Assert.DoesNotContain(canary, outcome.Failure?.Error.Message, StringComparison.Ordinal);

        TestLogEntry log = Assert.Single(logger.Entries);
        Assert.Null(log.Exception);
        Assert.Contains(nameof(OperationCanceledException), log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_CancelledOwnedToken_Propagates()
    {
        ScriptingChatClient chat = new(text: string.Empty)
        {
            BufferedException = new OperationCanceledException("provider cancelled"),
        };
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ModelCallExecutor().ExecuteBufferedAsync(
                chat,
                [new ChatMessage(ChatRole.User, "ping")],
                new ChatOptions(),
                UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference,
                cts.Token));
    }

    [Fact]
    public async Task ExecuteStreamingAsync_YieldsTextDeltas()
    {
        ScriptingChatClient chat = new(text: "ab");

        ITurnBudget budget = UnrestrictedTurnBudget.Instance;

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
            UnrestrictedTurnBudget.Instance,
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
            UnrestrictedTurnBudget.Instance,
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
            UnrestrictedTurnBudget.Instance,
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
            UnrestrictedTurnBudget.Instance,
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
            UnrestrictedTurnBudget.Instance,
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
            UnrestrictedTurnBudget.Instance,
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
            UnrestrictedTurnBudget.Instance,
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
            UnrestrictedTurnBudget.Instance,
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
            UnrestrictedTurnBudget.Instance,
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
            UnrestrictedTurnBudget.Instance,
            ModelCallPurpose.MainInference,
            CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Empty(updates.OfType<ModelCallUsageUpdate>());
        Assert.Single(updates.OfType<ModelCallReasoningUpdate>());
        Assert.Single(updates.OfType<ModelCallTextDelta>());
    }

    [SkippableTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Protected_stream_split_usage_charges_full_delegated_cost(bool protectedStream, bool hasUsageCommit)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        await using var child = (await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).Value;

        var binding = new RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation.MemoryDelegationRootBinding(
            new PingRequest("parent"), h.Source.Invocation, h.RootId, root, h.Coordinator);

        var protection = protectedStream ? binding.CaptureOutputProtection() : null;

        UsageDetails? committed = null;

        if (hasUsageCommit)
        {
            protection = protection!.WithUsageCommit(new(usage =>
            {
                committed = usage;

                return Task.CompletedTask;
            }));
        }

        DelegatedManaTracker tracker = new(maxTokens: 100, maxCostUsd: 0.000020m);

        using var scope = SubagentExecutionAmbient.EnterChild(tracker, protection: protection);

        var provider = CacheProvider();

        var executor = CreateAccountedExecutor(provider, new PricingSettings
        {
            DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
        });

        ScriptingChatClient chat = new(string.Empty)
        {
            StreamingUpdates =
            [
                new(ChatRole.Assistant, [new UsageContent(new() { InputTokenCount = 11 })]),
                new(ChatRole.Assistant, [new UsageContent(new() { OutputTokenCount = 7, TotalTokenCount = 18 })]),
            ],
        };

        await foreach (var _ in executor.ExecuteStreamingAsync(chat, [new(ChatRole.User, "derived input")], new(),
            UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, default,
            new(provider, "gpt-5", 0, 0, ProtectedBinding: protection))) { }

        var recorded = tracker.GetUsage();

        Assert.Equal(18, recorded.Tokens);

        Assert.Equal(protectedStream ? 0.000025m : 0.000014m, recorded.CostUsd);

        Assert.Equal(1, recorded.ModelCalls);

        Assert.Equal(protectedStream, recorded.Exhausted);

        if (hasUsageCommit)
        {
            Assert.NotNull(committed);

            Assert.Equal((11L, 7L, 18L), (committed.InputTokenCount, committed.OutputTokenCount, committed.TotalTokenCount));
        }
        else Assert.Null(committed);
    }

    /// <summary>
    /// The delegated ceiling is provider-usage-based: a call whose provider reports no usage is
    /// counted as a model call but charges nothing, so it can never trip the token or cost ceiling.
    /// </summary>
    [Fact]
    public async Task RecordDelegatedUsage_WhenProviderReportsNoUsage_LeavesTheTrackerUncharged()
    {
        DelegatedManaTracker tracker = new(maxTokens: 5, maxCostUsd: 0.000001m);
        ScriptingChatClient chat = new("a long answer from a provider that omits usage");
        ProviderSettings provider = CacheProvider();

        using IDisposable scope = SubagentExecutionAmbient.EnterChild(tracker);

        ModelCallOutcome outcome = await CreateAccountedExecutor(provider).ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "go")],
            new ChatOptions(),
            UnrestrictedTurnBudget.Instance,
            ModelCallPurpose.MainInference,
            CancellationToken.None,
            new ModelCallContext(provider, "gpt-5", 0, 0));

        Assert.True(outcome.IsSuccess);

        DelegatedManaUsage usage = tracker.GetUsage();

        Assert.Equal(0, usage.Tokens);
        Assert.Equal(0m, usage.CostUsd);
        Assert.Equal(1, usage.ModelCalls);
        Assert.False(usage.Exhausted);
    }

    /// <summary>
    /// An unpriced model is deliberately free: reported tokens still count against the token
    /// ceiling, but the cost ceiling is never charged for it.
    /// </summary>
    [Fact]
    public async Task RecordDelegatedUsage_WhenModelHasNoPricing_ChargesTokensButNoCost()
    {
        DelegatedManaTracker tracker = new(maxTokens: 1_000, maxCostUsd: 0.000001m);
        ChatResponse response = new(new ChatMessage(ChatRole.Assistant, "answer"))
        {
            Usage = new UsageDetails { InputTokenCount = 60, OutputTokenCount = 40, TotalTokenCount = 100 },
        };
        ScriptingChatClient chat = new(string.Empty) { BufferedResponse = response };
        ProviderSettings provider = CacheProvider();

        using IDisposable scope = SubagentExecutionAmbient.EnterChild(tracker);

        ModelCallOutcome outcome = await CreateAccountedExecutor(provider).ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "go")],
            new ChatOptions(),
            UnrestrictedTurnBudget.Instance,
            ModelCallPurpose.MainInference,
            CancellationToken.None,
            new ModelCallContext(provider, "gpt-5", 0, 0));

        Assert.True(outcome.IsSuccess);

        DelegatedManaUsage usage = tracker.GetUsage();

        Assert.Equal(100, usage.Tokens);
        Assert.Equal(0m, usage.CostUsd);
        Assert.False(usage.Exhausted);
    }

    /// <summary>
    /// The ceiling is post-hoc: the call that crosses it is still delivered, and the tracker only
    /// refuses the next call.
    /// </summary>
    [Fact]
    public async Task RecordDelegatedUsage_WhenUsageCrossesTheCeiling_DeliversThatCallAndRefusesTheNext()
    {
        DelegatedManaTracker tracker = new(maxTokens: 10, maxCostUsd: null);
        ChatResponse response = new(new ChatMessage(ChatRole.Assistant, "answer"))
        {
            Usage = new UsageDetails { TotalTokenCount = 50 },
        };
        ScriptingChatClient chat = new(string.Empty) { BufferedResponse = response };
        ModelCallExecutor executor = new();

        using IDisposable scope = SubagentExecutionAmbient.EnterChild(tracker);

        ModelCallOutcome first = await executor.ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "go")],
            new ChatOptions(),
            UnrestrictedTurnBudget.Instance,
            ModelCallPurpose.MainInference,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(tracker.GetUsage().Exhausted);

        ModelCallOutcome second = await executor.ExecuteBufferedAsync(
            chat,
            [new ChatMessage(ChatRole.User, "again")],
            new ChatOptions(),
            UnrestrictedTurnBudget.Instance,
            ModelCallPurpose.MainInference,
            CancellationToken.None);

        Assert.True(second.IsFailure);
        Assert.Equal(1, chat.CallCount);
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
        ModelTokenEstimator estimator = new(tokenizer);

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
