using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Resilience;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Direct-construction fallback-loop tests for <see cref="WizardIntelligenceProvider"/>, mirroring the
/// pattern in <c>WizardIntelligenceProviderTests.cs</c> (own scripted <see cref="IChatClientFactory"/>
/// double + a real <see cref="ProviderHealthTracker"/>) rather than the HTTP <c>ArcanumWebApplicationFactory</c>
/// path, since that fixture unconditionally substitutes <see cref="IArcanumIntelligenceProvider"/> with a fake.
/// </summary>
public sealed class WizardIntelligenceProviderFallbackTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    private const string ModelName = "wizard-fallback-test-model";

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public async Task ExecutePromptAsync_retries_on_connectivity_failure()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        RecordingChatClientFactory factory = new();

        factory.CandidateExceptions[providerA.Name] = new HttpRequestException("connection refused");

        ScriptingChatClient chatB = new();

        chatB.EnqueueText("answer from B");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 1);

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("answer from B", result.Value!.Text);

        Assert.Equal([providerA.Name, providerB.Name], factory.CandidateCallOrder);

        Assert.False(tracker.IsHealthy(providerA.Name));

        Assert.True(tracker.IsHealthy(providerB.Name));

    }

    [Fact]
    public async Task ExecutePromptAsync_stops_after_max_attempts()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        ProviderSettings providerC = MakeProvider("provider-c");

        RecordingChatClientFactory factory = new();

        factory.CandidateExceptions[providerA.Name] = new HttpRequestException("connection refused");

        factory.CandidateExceptions[providerB.Name] = new HttpRequestException("connection refused");

        factory.CandidateExceptions[providerC.Name] = new HttpRequestException("connection refused");

        ProviderHealthTracker tracker = CreateTracker();

        WizardIntelligenceProvider wizard = CreateWizard(
            factory,
            tracker,
            maxFallbackAttempts: 2,
            providerA,
            providerB,
            providerC);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal([providerA.Name, providerB.Name], factory.CandidateCallOrder);

    }

    [Fact]
    public async Task ExecutePromptAsync_does_not_retry_on_model_error()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        RecordingChatClientFactory factory = new();

        ScriptingChatClient chatA = new();

        chatA.EnqueueException(new InvalidOperationException("content filter rejected the request"));

        factory.CandidateResolvers[providerA.Name] = () => MakeLease(chatA, providerA);

        ScriptingChatClient chatB = new();

        chatB.EnqueueText("should never be reached");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker();

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal([providerA.Name], factory.CandidateCallOrder);

        Assert.True(tracker.IsHealthy(providerA.Name));

    }

    [Fact]
    public async Task ExecutePromptAsync_retries_buffered_connectivity_failure_before_commit()
    {
        ProviderSettings providerA = MakeProvider("provider-a");
        ProviderSettings providerB = MakeProvider("provider-b");
        ScriptingChatClient chatA = new();
        chatA.EnqueueException(new HttpRequestException("connection refused during buffered inference"));
        ScriptingChatClient chatB = new();
        chatB.EnqueueText("answer from B");
        RecordingChatClientFactory factory = new();
        factory.CandidateResolvers[providerA.Name] = () => MakeLease(chatA, providerA);
        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);
        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 1);
        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("answer from B", result.Value.Text);
        Assert.Equal([providerA.Name, providerB.Name], factory.CandidateCallOrder);
        Assert.False(tracker.IsHealthy(providerA.Name));
        Assert.True(tracker.IsHealthy(providerB.Name));
    }

    [Fact]
    public async Task ExecutePromptAsync_LeaseBuildFailure_NonConnectivity_DoesNotMarkProviderUnhealthy_OrRetry()
    {

        // A lease-construction failure that is not a connectivity error (e.g. a local
        // misconfiguration or a transient concurrency-slot overload) must not mark the provider
        // unhealthy or trigger fallback to the next candidate — mirrors
        // ExecutePromptAsync_does_not_retry_on_model_error, but for the lease-build step
        // (ResolveClientAsync) rather than the inference call itself.
        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        RecordingChatClientFactory factory = new();

        factory.CandidateExceptions[providerA.Name] = new InvalidOperationException("Provider overloaded");

        ScriptingChatClient chatB = new();

        chatB.EnqueueText("should never be reached");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker();

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal([providerA.Name], factory.CandidateCallOrder);

        Assert.True(tracker.IsHealthy(providerA.Name));

    }

    [Fact]
    public async Task StreamPromptAsync_retries_pre_stream_failure()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        RecordingChatClientFactory factory = new();

        factory.CandidateExceptions[providerA.Name] = new HttpRequestException("connection refused");

        ScriptingChatClient chatB = new();

        chatB.EnqueueStreamTokens("he", "llo");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 1);

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        List<IntelligenceEvent> events = await CollectStreamAsync(wizard, BaseRequest());

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Token && e.Data == "he");

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Token && e.Data == "llo");

        Assert.DoesNotContain(events, static e => e.Type == IntelligenceEventType.Error);

        Assert.Equal([providerA.Name, providerB.Name], factory.CandidateCallOrder);

        Assert.False(tracker.IsHealthy(providerA.Name));

        Assert.True(tracker.IsHealthy(providerB.Name));

    }

    [Fact]
    public async Task Marks_provider_unhealthy_on_failure()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        RecordingChatClientFactory factory = new();

        factory.CandidateExceptions[providerA.Name] = new HttpRequestException("connection refused");

        ScriptingChatClient chatB = new();

        chatB.EnqueueText("answer from B");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 1);

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        await wizard.ExecutePromptAsync(BaseRequest(), CancellationToken.None);

        Assert.False(tracker.IsHealthy(providerA.Name));

    }

    [Fact]
    public async Task Marks_provider_healthy_on_success()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        RecordingChatClientFactory factory = new();

        factory.CandidateExceptions[providerA.Name] = new HttpRequestException("connection refused");

        ScriptingChatClient chatB = new();

        chatB.EnqueueText("answer from B");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 3);

        // Pre-seed providerB with a prior (degraded, still-healthy) failure to prove success resets it.
        tracker.MarkFailed(providerB.Name);

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(BaseRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess);

        ProviderHealthStatus statusB = Assert.Single(tracker.GetAllStatuses(), s => s.ProviderName == providerB.Name);

        Assert.True(statusB.IsHealthy);

        Assert.Equal(0, statusB.ConsecutiveFailures);

    }

    [Fact]
    public async Task Resilience_disabled_skips_fallback()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        RecordingChatClientFactory factory = new();

        // The non-resilience single-resolution path only catches InvalidOperationException (the
        // ProviderResolver "no model resolved" contract) — matches the disabled-path production code.
        factory.SingleCallException = new InvalidOperationException("No AI model could be resolved.");

        ProviderHealthTracker tracker = CreateTracker();

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, resilienceEnabled: false, providerA);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(BaseRequest(), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, factory.SingleCallCount);

        Assert.Empty(factory.CandidateCallOrder);

        Assert.Empty(tracker.GetAllStatuses());

    }

    [Fact]
    public async Task ExecutePromptAsync_resilience_disabled_rejects_unsupported_reasoning_before_chat_call_and_disposes_lease()
    {

        ProviderSettings provider = MakeProvider("provider-a");

        provider.Models =
        [
            new ModelEntry(ModelName)
            {
                Reasoning = new ReasoningCapabilities
                {
                    ControlSupport = ReasoningControlSupport.Effort,
                    WireDialect = ReasoningWireDialect.Standard,
                },
            },
        ];

        ScriptingChatClient chat = new();

        chat.EnqueueText("must not be called");

        RecordingChatClientFactory factory = new()
        {
            SingleResolver = () => MakeLease(chat, provider),
        };

        WizardIntelligenceProvider wizard = CreateWizard(
            factory,
            CreateTracker(),
            resilienceEnabled: false,
            provider);

        PingRequest request = BaseRequest() with
        {
            Reasoning = new ReasoningRequestOptions(BudgetTokens: 1024),
        };

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation.UnsupportedReasoningControl, result.Error.Code);
        Assert.Contains(provider.Name, result.Error.Message, StringComparison.Ordinal);
        Assert.Contains(ModelName, result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(1, factory.SingleCallCount);
        Assert.Equal(0, chat.BufferedCallCount);
        Assert.Equal(1, chat.DisposeCount);

    }

    [Fact]
    public async Task StreamPromptAsync_resilience_disabled_rejects_unsupported_reasoning_before_chat_call_and_disposes_lease()
    {

        ProviderSettings provider = MakeProvider("provider-a");

        provider.Models =
        [
            new ModelEntry(ModelName)
            {
                Reasoning = new ReasoningCapabilities
                {
                    ControlSupport = ReasoningControlSupport.Budget,
                    WireDialect = ReasoningWireDialect.OpenRouter,
                },
            },
        ];

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("must not be called");

        RecordingChatClientFactory factory = new()
        {
            SingleResolver = () => MakeLease(chat, provider),
        };

        WizardIntelligenceProvider wizard = CreateWizard(
            factory,
            CreateTracker(),
            resilienceEnabled: false,
            provider);

        PingRequest request = BaseRequest() with
        {
            Reasoning = new ReasoningRequestOptions(Effort: ReasoningEffortLevel.High),
        };

        List<IntelligenceEvent> events = await CollectStreamAsync(wizard, request);

        IntelligenceEvent error = Assert.Single(events, static e => e.Type == IntelligenceEventType.Error);

        Assert.Equal(ErrorCodes.Validation.UnsupportedReasoningControl, error.Data);
        Assert.Contains(provider.Name, error.Message, StringComparison.Ordinal);
        Assert.Contains(ModelName, error.Message, StringComparison.Ordinal);
        Assert.Equal(1, factory.SingleCallCount);
        Assert.Equal(0, chat.StreamingCallCount);
        Assert.Equal(1, chat.DisposeCount);

    }

    [Fact]
    public async Task ExecutePromptAsync_rejects_unsupported_reasoning_before_fallback_provider_io()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        providerA.Models =
        [
            new ModelEntry(ModelName)
            {
                Reasoning = new ReasoningCapabilities
                {
                    ControlSupport = ReasoningControlSupport.Budget,
                    WireDialect = ReasoningWireDialect.OpenRouter,
                },
            },
        ];

        ProviderSettings providerB = MakeProvider("provider-b");

        RecordingChatClientFactory factory = new();

        factory.CandidateExceptions[providerA.Name] = new HttpRequestException("connection refused");

        ScriptingChatClient chatB = new();

        chatB.EnqueueText("must not be called");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 1);

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        PingRequest request = BaseRequest() with
        {
            Reasoning = new ReasoningRequestOptions(BudgetTokens: 1024),
        };

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation.UnsupportedReasoningControl, result.Error.Code);
        Assert.Contains(providerB.Name, result.Error.Message, StringComparison.Ordinal);
        Assert.Contains(ModelName, result.Error.Message, StringComparison.Ordinal);
        Assert.Equal([providerA.Name], factory.CandidateCallOrder);

    }

    [Fact]
    public async Task StreamPromptAsync_rejects_unsupported_reasoning_before_fallback_provider_io()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        providerA.Models =
        [
            new ModelEntry(ModelName)
            {
                Reasoning = new ReasoningCapabilities
                {
                    ControlSupport = ReasoningControlSupport.Effort,
                    WireDialect = ReasoningWireDialect.Standard,
                },
            },
        ];

        ProviderSettings providerB = MakeProvider("provider-b");

        RecordingChatClientFactory factory = new();

        factory.CandidateExceptions[providerA.Name] = new HttpRequestException("connection refused");

        ScriptingChatClient chatB = new();

        chatB.EnqueueStreamTokens("must not be called");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 1);

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        PingRequest request = BaseRequest() with
        {
            Reasoning = new ReasoningRequestOptions(Effort: ReasoningEffortLevel.High),
        };

        List<IntelligenceEvent> events = await CollectStreamAsync(wizard, request);

        IntelligenceEvent error = Assert.Single(events, static e => e.Type == IntelligenceEventType.Error);

        Assert.Equal(ErrorCodes.Validation.UnsupportedReasoningControl, error.Data);
        Assert.Contains("does not declare support", error.Message, StringComparison.Ordinal);
        Assert.Contains(providerB.Name, error.Message, StringComparison.Ordinal);
        Assert.Contains(ModelName, error.Message, StringComparison.Ordinal);
        Assert.Equal([providerA.Name], factory.CandidateCallOrder);

    }

    [Fact]
    public async Task ExecutePromptAsync_uses_healthy_compatible_candidate_when_first_configured_provider_is_unhealthy_and_incompatible()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        providerB.Models =
        [
            new ModelEntry(ModelName)
            {
                Reasoning = new ReasoningCapabilities
                {
                    ControlSupport = ReasoningControlSupport.Budget,
                    WireDialect = ReasoningWireDialect.OpenRouter,
                },
            },
        ];

        RecordingChatClientFactory factory = new();

        ScriptingChatClient chatB = new();

        chatB.EnqueueText("answer from compatible B");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 1);

        tracker.MarkFailed(providerA.Name);

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        PingRequest request = BaseRequest() with
        {
            Reasoning = new ReasoningRequestOptions(BudgetTokens: 1024),
        };

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("answer from compatible B", result.Value.Text);
        Assert.Equal([providerB.Name], factory.CandidateCallOrder);

    }

    [Fact]
    public async Task StreamPromptAsync_uses_healthy_compatible_candidate_when_first_configured_provider_is_unhealthy_and_incompatible()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        providerB.Models =
        [
            new ModelEntry(ModelName)
            {
                Reasoning = new ReasoningCapabilities
                {
                    ControlSupport = ReasoningControlSupport.Effort,
                    WireDialect = ReasoningWireDialect.Standard,
                },
            },
        ];

        RecordingChatClientFactory factory = new();

        ScriptingChatClient chatB = new();

        chatB.EnqueueStreamTokens("answer ", "from compatible B");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 1);

        tracker.MarkFailed(providerA.Name);

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        PingRequest request = BaseRequest() with
        {
            Reasoning = new ReasoningRequestOptions(Effort: ReasoningEffortLevel.High),
        };

        List<IntelligenceEvent> events = await CollectStreamAsync(wizard, request);

        Assert.DoesNotContain(events, static e => e.Type == IntelligenceEventType.Error);
        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Result);
        Assert.Equal([providerB.Name], factory.CandidateCallOrder);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamPromptAsync_does_not_fallback_after_visible_or_protected_reasoning_commit(
        bool protectedOnly)
    {
        ReasoningCapabilities capabilities = new()
        {
            SupportsSummary = true,
            SupportsStreaming = true,
            AllowsClientOutput = true,
            WireDialect = ReasoningWireDialect.Standard,
        };
        ProviderSettings providerA = MakeProvider("provider-a");
        providerA.Models = [new ModelEntry(ModelName, Reasoning: capabilities)];
        ProviderSettings providerB = MakeProvider("provider-b");
        providerB.Models = [new ModelEntry(ModelName, Reasoning: capabilities)];

        TextReasoningContent reasoning = new(protectedOnly ? string.Empty : "visible reasoning");
        if (protectedOnly)
        {
            reasoning.ProtectedData = "opaque-provider-state";
        }

        ScriptingChatClient chatA = new();
        chatA.EnqueueReasoningThenStreamFailure(
            reasoning,
            new HttpRequestException("connection dropped after reasoning"));
        ScriptingChatClient chatB = new();
        chatB.EnqueueStreamTokens("must not be reached");
        RecordingChatClientFactory factory = new();
        factory.CandidateResolvers[providerA.Name] = () => MakeLease(chatA, providerA);
        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);
        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 1);
        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Reasoning = new ReasoningRequestOptions(
                    Output: protectedOnly ? ReasoningOutputMode.None : ReasoningOutputMode.Summary),
            });

        Assert.Contains(events, static evt => evt.Type == IntelligenceEventType.Error);
        Assert.DoesNotContain(events, static evt => evt.Type == IntelligenceEventType.Result);
        if (protectedOnly)
        {
            Assert.DoesNotContain(
                events,
                static evt => evt.Type == IntelligenceEventType.Reasoning);
        }
        else
        {
            IntelligenceEvent reasoningFrame = Assert.Single(
                events,
                static evt => evt.Type == IntelligenceEventType.Reasoning);
            Assert.Equal("visible reasoning", reasoningFrame.Reasoning?.Text);
            Assert.True(
                events.IndexOf(reasoningFrame)
                < events.FindIndex(static evt => evt.Type == IntelligenceEventType.Error));
        }
        Assert.Equal([providerA.Name], factory.CandidateCallOrder);
        Assert.Equal(0, chatB.StreamingCallCount);
        Assert.False(tracker.IsHealthy(providerA.Name));
        ProviderHealthStatus failed = Assert.Single(
            tracker.GetAllStatuses(),
            status => status.ProviderName == providerA.Name);
        Assert.Equal(1, failed.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task StreamPromptAsync_uses_resolved_fallback_streaming_reasoning_capability(
        bool supportsStreaming,
        bool expectsReasoning)
    {
        ProviderSettings providerA = MakeProvider("provider-a");
        ProviderSettings providerB = MakeProvider("provider-b");
        providerB.Models =
        [
            new ModelEntry(
                ModelName,
                Reasoning: new ReasoningCapabilities
                {
                    SupportsSummary = true,
                    SupportsFull = false,
                    SupportsStreaming = supportsStreaming,
                    AllowsClientOutput = true,
                    WireDialect = ReasoningWireDialect.Standard,
                }),
        ];
        ScriptingChatClient chatB = new();
        chatB.EnqueueStreamUpdates(new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                new TextReasoningContent("fallback summary"),
                new TextContent("fallback answer"),
            ]));
        RecordingChatClientFactory factory = new();
        factory.CandidateExceptions[providerA.Name] = new HttpRequestException("connection refused");
        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);
        WizardIntelligenceProvider wizard = CreateWizard(
            factory,
            CreateTracker(healthFailureThreshold: 1),
            providerA,
            providerB);

        List<IntelligenceEvent> events = await CollectStreamAsync(wizard, BaseRequest());

        Assert.Equal([providerA.Name, providerB.Name], factory.CandidateCallOrder);
        Assert.Equal(
            expectsReasoning,
            events.Any(static evt => evt.Type == IntelligenceEventType.Reasoning));
        if (expectsReasoning)
        {
            ReasoningContentSegment? reasoning = Assert.Single(
                events,
                static evt => evt.Type == IntelligenceEventType.Reasoning).Reasoning;
            Assert.Equal("fallback summary", reasoning?.Text);
            Assert.Equal(ReasoningOutputMode.Summary, reasoning?.Output);
        }
        Assert.Contains(
            events,
            static evt => evt.Type == IntelligenceEventType.Token
                && evt.Data == "fallback answer");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutePromptAsync_does_not_fallback_after_buffered_reasoning_commit(
        bool protectedOnly)
    {
        ReasoningCapabilities capabilities = new()
        {
            SupportsSummary = true,
            AllowsClientOutput = true,
            WireDialect = ReasoningWireDialect.Standard,
        };
        ProviderSettings providerA = MakeProvider("provider-a");
        providerA.Models = [new ModelEntry(ModelName, Reasoning: capabilities)];
        ProviderSettings providerB = MakeProvider("provider-b");
        providerB.Models = [new ModelEntry(ModelName, Reasoning: capabilities)];
        TextReasoningContent reasoning = new(protectedOnly ? string.Empty : "visible reasoning");
        if (protectedOnly)
        {
            reasoning.ProtectedData = "opaque-provider-state";
        }

        ScriptingChatClient chatA = new();
        chatA.EnqueueResponse(new ChatResponse(new MeAiChatMessage(
            ChatRole.Assistant,
            [
                reasoning,
                new FunctionCallContent(
                    ArcanumLocalTimeTool.ToolName,
                    ArcanumLocalTimeTool.ToolName,
                    new Dictionary<string, object?>()),
            ])));
        chatA.EnqueueException(new HttpRequestException("connection dropped during tool continuation"));
        ScriptingChatClient chatB = new();
        chatB.EnqueueText("must not be reached");
        RecordingChatClientFactory factory = new();
        factory.CandidateResolvers[providerA.Name] = () => MakeLease(chatA, providerA);
        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);
        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 1);
        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                DisableMcpTools = false,
                Reasoning = new ReasoningRequestOptions(
                    Output: protectedOnly ? ReasoningOutputMode.None : ReasoningOutputMode.Summary),
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal([providerA.Name], factory.CandidateCallOrder);
        Assert.Equal(0, chatB.BufferedCallCount);
        Assert.False(tracker.IsHealthy(providerA.Name));
        ProviderHealthStatus failed = Assert.Single(
            tracker.GetAllStatuses(),
            status => status.ProviderName == providerA.Name);
        Assert.Equal(1, failed.ConsecutiveFailures);
    }

    // ===== Helpers =====

    private static ProviderHealthTracker CreateTracker(int healthFailureThreshold = 3) =>
        new(
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings
            {
                Resilience = new ResilienceSettings { HealthFailureThreshold = healthFailureThreshold },
            }),
            NullLogger<ProviderHealthTracker>.Instance);

    private static ProviderSettings MakeProvider(string name) => new()
    {
        Name = name,
        Type = AiProviderKind.OpenAICompatible,
        Endpoint = "https://example.test/v1",
        Models = [ModelName],
        ContextWindowLimit = 8192,
    };

    private static ChatClientLease MakeLease(ScriptingChatClient chatClient, ProviderSettings provider) =>
        new(chatClient, provider, ModelName, ownedHttpClient: null);

    private static PingRequest BaseRequest() =>
        new(Prompt: "hello", Model: ModelName, WorkingDirectory: string.Empty, SkipSpellRouting: true, DisableMcpTools: true);

    private static InferenceContextBuilder CreateInferenceContextBuilder(
        IGrimoireRepository grimoire,
        ArcanumSettings settings)
    {

        ManaPreflight manaPreflight = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        InferenceTokenizerResolver tokenizerResolver = new(NullLogger<InferenceTokenizerResolver>.Instance);

        IContextCompressionService compression = new ContextCompressionService(
            grimoire,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            manaPreflight,
            tokenizerResolver,
            NullLogger<ContextCompressionService>.Instance);

        return new InferenceContextBuilder(
            grimoire,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            NullLogger<InferenceContextBuilder>.Instance,
            manaPreflight,
            compression);

    }

    private static WizardIntelligenceProvider CreateWizard(
        IChatClientFactory factory,
        IProviderHealthTracker? healthTracker,
        params ProviderSettings[] providers) =>
        CreateWizard(factory, healthTracker, resilienceEnabled: true, maxFallbackAttempts: 3, providers);

    private static WizardIntelligenceProvider CreateWizard(
        IChatClientFactory factory,
        IProviderHealthTracker? healthTracker,
        int maxFallbackAttempts,
        params ProviderSettings[] providers) =>
        CreateWizard(factory, healthTracker, resilienceEnabled: true, maxFallbackAttempts, providers);

    private static WizardIntelligenceProvider CreateWizard(
        IChatClientFactory factory,
        IProviderHealthTracker? healthTracker,
        bool resilienceEnabled,
        params ProviderSettings[] providers) =>
        CreateWizard(factory, healthTracker, resilienceEnabled, maxFallbackAttempts: 3, providers);

    private static WizardIntelligenceProvider CreateWizard(
        IChatClientFactory factory,
        IProviderHealthTracker? healthTracker,
        bool resilienceEnabled,
        int maxFallbackAttempts,
        params ProviderSettings[] providers)
    {

        ArcanumSettings settings = new()
        {

            DefaultModel = ModelName,

            Providers = providers,

            Resilience = new ResilienceSettings
            {
                Enabled = resilienceEnabled,
                MaxFallbackAttempts = maxFallbackAttempts,
            },

            Intelligence = new IntelligenceSettings { EnableLexiconSystem = false },

        };

        FakeGrimoireRepository grimoire = new();

        FakeWard ward = new();

        FakeMcpConnectionManager mcp = new();

        FakeCampaignRepository campaignRepository = new();

        ConfigurableSanctumGuard sanctumGuard = new();

        return new WizardIntelligenceProvider(
            factory,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            NullLogger<WizardIntelligenceProvider>.Instance,
            new FakeHttpClientFactory(),
            grimoire,
            mcp,
            campaignRepository,
            new ToolExecutionPipeline(
                new TestOptionsSnapshot<ArcanumSettings>(settings),
                ward,
                sanctumGuard,
                new NoOpSessionAttachmentStore(),
                NullLogger<ToolExecutionPipeline>.Instance),
            new GrimoireTurnWriter(
                grimoire,
                new SessionEventHub(new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<SessionEventHub>.Instance),
                NullLogger<GrimoireTurnWriter>.Instance),
            CreateInferenceContextBuilder(grimoire, settings),
            sanctumGuard,
            new ProcessResourceLimiter(),
            new NoopWeaveService(),
            new NoopDivinationService(),
            new NoopWorkspaceIndexingService(),
            new NoopSagaMemoryStore(),
            new SagaExtractionService(
                new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                new TestOptionsMonitor<ArcanumSettings>(settings),
                NullLogger<SagaExtractionService>.Instance),
            new SemanticSpellRouter(
                new SpellWeaveCache(new NoopWeaveService(), new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<SpellWeaveCache>.Instance),
                new NoopWeaveService(),
                new TestOptionsSnapshot<ArcanumSettings>(settings),
                NullLogger<SemanticSpellRouter>.Instance),
            new FakeLexiconService(),
            CreateUnusedDbContext(),
            new FakeInferenceAuditLogger(),
            new StructuredOutputValidator(),
            new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance),
            new BudgetMonitor(
                CreateBudgetMonitorScopeFactory(new FakeGrimoireRepository(), new FakeBudgetAlertRepository()),
                new FakeCommLinkDispatcher(),
                new TestOptionsMonitor<ArcanumSettings>(settings),
                NullLogger<BudgetMonitor>.Instance),
            new NoOpSessionAttachmentStore(),
            new HumanPromptRegistry(),
            new ManaPreflight(new TestOptionsMonitor<ArcanumSettings>(settings)),
            healthTracker);
    }

    private static IServiceScopeFactory CreateBudgetMonitorScopeFactory(
        IGrimoireRepository grimoire,
        IBudgetAlertRepository budgetAlerts)
    {

        ServiceCollection services = new();

        services.AddScoped(_ => grimoire);

        services.AddScoped(_ => budgetAlerts);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    }

    /// <summary>
    /// RAG Phase 3 — this fallback-loop test file never enables
    /// <c>Arcanum:Embeddings</c>, so <c>WizardIntelligenceProvider.RetrieveSemanticContextAsync</c>
    /// short-circuits before ever touching these dependencies. A syntactically-valid but never-opened
    /// <see cref="ArcanumDbContext"/> is sufficient (see the identical pattern and rationale in
    /// <c>WizardIntelligenceProviderTests.CreateUnusedDbContext</c>).
    /// </summary>
    private static ArcanumDbContext CreateUnusedDbContext()
    {
        DbContextOptions<ArcanumDbContext> options = new DbContextOptionsBuilder<ArcanumDbContext>()
            .UseSqlite("Data Source=:memory:")
            .UseModel(ArcanumDbContextModel.Instance)
            .Options;

        return new ArcanumDbContext(options, new NoopSecretStore(), new NoopPassphraseSource());
    }

    private static async Task<List<IntelligenceEvent>> CollectStreamAsync(
        WizardIntelligenceProvider wizard,
        PingRequest request)
    {
        List<IntelligenceEvent> events = [];

        await foreach (IntelligenceEvent evt in wizard.StreamPromptAsync(request, CancellationToken.None))
        {
            events.Add(evt);
        }

        return events;
    }

    // ===== Test doubles =====

    private sealed class NoopWeaveService : IWeaveService
    {

        public bool IsAvailable => false;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Embeddings stays disabled in this test file.");

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Embeddings stays disabled in this test file.");

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Embeddings stays disabled in this test file.");

    }

    private sealed class NoopDivinationService : IDivinationService
    {

        public Task<Result<DivinationResult[]>> SearchAsync(
            string tableName,
            string primaryKeyColumn,
            string embeddingColumn,
            Embedding<float> queryEmbedding,
            int maxResults,
            float similarityThreshold,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Embeddings stays disabled in this test file.");

        public Task<Result<DivinationResult[]>> SearchScopedAsync(
            string tableName,
            string primaryKeyColumn,
            string embeddingColumn,
            string scopeTableName,
            string scopeJoinColumn,
            string scopeFilterColumn,
            string scopeFilterValue,
            Embedding<float> queryEmbedding,
            int maxResults,
            float similarityThreshold,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Embeddings stays disabled in this test file.");

    }

    private sealed class NoopWorkspaceIndexingService : IWorkspaceIndexingService
    {

        public void RegisterWorkspace(string workspacePath)
        {
        }

        public Task IndexNowAsync(string workspacePath, CancellationToken cancellationToken) => Task.CompletedTask;

    }

    /// <summary>RAG Phase 4 — Saga stays disabled in this test file, so <see cref="ISagaMemoryStore"/> is never touched.</summary>
    private sealed class NoopSagaMemoryStore : ISagaMemoryStore
    {

        public Task InsertAsync(
            string id,
            string content,
            DateTimeOffset createdAt,
            Guid? sessionId,
            string? tags,
            string? source,
            float[] embedding,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Saga stays disabled in this test file.");

        public Task<int> CountAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Saga stays disabled in this test file.");

        public Task<int> CountBySessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Saga stays disabled in this test file.");

        public Task<SagaMemoryDto[]> ListAsync(string? query, Guid? sessionId, int limit, int offset, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Saga stays disabled in this test file.");

        public Task<IReadOnlyDictionary<string, SagaMemoryDto>> GetByIdsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Saga stays disabled in this test file.");

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Saga stays disabled in this test file.");

        public Task DeleteAllAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Saga stays disabled in this test file.");

        public Task<SagaStats> GetStatsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Saga stays disabled in this test file.");

        public Task<DateTimeOffset?> GetWatermarkAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Saga stays disabled in this test file.");

        public Task SetWatermarkAsync(Guid sessionId, DateTimeOffset lastExtractedEntryCreatedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Unused: Saga stays disabled in this test file.");

    }

    private sealed class NoopSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => throw new NotSupportedException("Unused in RAG-disabled scenarios.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() => throw new NotSupportedException("Unused in RAG-disabled scenarios.");

        public Task SaveApiKeyAsync(string apiKey) => throw new NotSupportedException("Unused in RAG-disabled scenarios.");

        public Task<string?> GetGrimoireEncryptionSecretAsync() => throw new NotSupportedException("Unused in RAG-disabled scenarios.");

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => throw new NotSupportedException("Unused in RAG-disabled scenarios.");

    }

    private sealed class NoopPassphraseSource : IGrimoireDbPassphraseSource
    {

        public string Passphrase => throw new NotSupportedException("Unused: DbContextOptions is pre-configured so OnConfiguring never reads this.");

        public void SetPassphrase(string passphrase) =>
            throw new NotSupportedException("Unused: DbContextOptions is pre-configured so OnConfiguring never reads this.");

    }

    private sealed class RecordingChatClientFactory : IChatClientFactory
    {

        public List<string> CandidateCallOrder { get; } = [];

        public Dictionary<string, Func<ChatClientLease>> CandidateResolvers { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Exception> CandidateExceptions { get; } = new(StringComparer.Ordinal);

        public int SingleCallCount { get; private set; }

        public Exception? SingleCallException { get; set; }

        public Func<ChatClientLease>? SingleResolver { get; set; }

        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken)
        {

            SingleCallCount++;

            if (SingleCallException is not null)
            {
                throw SingleCallException;
            }

            if (SingleResolver is not null)
            {
                return Task.FromResult(SingleResolver());
            }

            throw new InvalidOperationException("No AI model could be resolved.");

        }

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken)
        {

            CandidateCallOrder.Add(provider.Name);

            if (CandidateExceptions.TryGetValue(provider.Name, out Exception? ex))
            {
                throw ex;
            }

            if (CandidateResolvers.TryGetValue(provider.Name, out Func<ChatClientLease>? resolver))
            {
                return Task.FromResult(resolver());
            }

            throw new InvalidOperationException($"No resolver configured for provider '{provider.Name}'.");

        }

    }

    private sealed class ScriptingChatClient : IChatClient
    {

        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _buffered = new();

        private readonly Queue<Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>> _streaming = new();

        public int BufferedCallCount { get; private set; }

        public int StreamingCallCount { get; private set; }

        public int DisposeCount { get; private set; }

        public void EnqueueText(string text) =>
            _buffered.Enqueue(_ => Task.FromResult(new ChatResponse(new MeAiChatMessage(ChatRole.Assistant, text))));

        public void EnqueueResponse(ChatResponse response) =>
            _buffered.Enqueue(_ => Task.FromResult(response));

        public void EnqueueException(Exception ex) =>
            _buffered.Enqueue(_ => throw ex);

        public void EnqueueStreamTokens(params string[] tokens) =>
            _streaming.Enqueue(_ => StreamTokens(tokens));

        public void EnqueueStreamUpdates(params ChatResponseUpdate[] updates) =>
            _streaming.Enqueue(_ => StreamUpdates(updates));

        public void EnqueueReasoningThenStreamFailure(
            TextReasoningContent reasoning,
            Exception exception) =>
            _streaming.Enqueue(_ => ReasoningThenFail(reasoning, exception));

        public void Dispose()
        {
            DisposeCount++;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BufferedCallCount++;

            if (_buffered.Count == 0)
            {
                throw new InvalidOperationException("No scripted buffered response remaining.");
            }

            return _buffered.Dequeue()(cancellationToken);

        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            StreamingCallCount++;

            if (_streaming.Count == 0)
            {
                throw new InvalidOperationException("No scripted streaming response remaining.");
            }

            return _streaming.Dequeue()(cancellationToken);

        }

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamTokens(
            IEnumerable<string> tokens,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (string token in tokens)
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return new ChatResponseUpdate(ChatRole.Assistant, token);

                await Task.Yield();
            }
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamUpdates(
            IEnumerable<ChatResponseUpdate> updates,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (ChatResponseUpdate update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> ReasoningThenFail(
            TextReasoningContent reasoning,
            Exception exception,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new ChatResponseUpdate(ChatRole.Assistant, [reasoning]);

            await Task.Yield();

            throw exception;
        }

    }

    private sealed class FakeGrimoireRepository : IGrimoireRepository
    {

        public Task<Session?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Session?>(null);

        public Task<Session?> GetSessionHeaderAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Session?>(null);

        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
            Guid? sessionId,
            string prompt,
            string model,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((sessionId ?? Guid.NewGuid(), Guid.NewGuid()));

        public Task FinalizeAssistantEntryAsync(Guid assistantEntryId, string fullContent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DiscardAssistantEntryAsync(Guid assistantEntryId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task AppendToolInteractionAsync(
            Guid sessionId,
            string toolName,
            string arguments,
            string result,
            string modelUsed,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveCompletedExchangeAsync(string userPrompt, string assistantText, string modelUsed, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> PurgeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<List<GrimoireEntryDto>?>(null);

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(Guid sessionId, int takeLast, CancellationToken cancellationToken = default) =>
            Task.FromResult<List<GrimoireEntryDto>?>(null);

        public Task<GrimoireEntryDto?> GetEntryByIdAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GrimoireEntryDto?>(null);

        public Task<bool> DeleteEntryAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> SetEntryPinnedAsync(Guid sessionId, Guid entryId, bool pinned, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> GetPinnedEntryCountAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync(int threshold, DateTime idleCutoff, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Guid>());

        public Task<List<Entry>> GetUnsummarizedEntriesAsync(Guid sessionId, DateTime watermark, int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Entry>());

        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task IncrementSessionTokensAsync(Guid sessionId, long totalTokens, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task IncrementSessionTokensAndCostAsync(Guid sessionId, long totalTokens, decimal costUsd, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateSessionCampaignRollupAsync(Guid sessionId, string summary, DateTime lastSummarizedMessageAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LoreDto(key, value, DateTime.UtcNow));

        public Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<ListPageResult<LoreDto>> ListLoreAsync(int? limit = null, int offset = 0, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<LoreDto>([], false));

        public Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<LoreDto?>(null);

        public Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(string workspacePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkspaceContext?>(null);

    }

    private sealed class FakeWard : IWard
    {

        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WardResolution(true, null, DateTimeOffset.UtcNow));

        public ResolveStatus Resolve(string wardId, bool allow, string? reason) =>
            ResolveStatus.Success;

        public IReadOnlyList<ActiveWard> GetActiveWards() => [];

    }

    private sealed class FakeMcpConnectionManager : IMcpConnectionManager
    {

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> StartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> StopAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> RestartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<McpServerInfo?> GetStatusAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<McpServerInfo?>(null);

        public Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<McpServerInfo>());

        public Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AITool>>([]);

        public Task<AIFunction?> GetToolAsync(
            string serverName,
            string toolName,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AIFunction?>(null);

        public Task<List<McpServerStatusDto>> GetServerStatusesAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<McpServerStatusDto>());

        public Task ReloadAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> TrustWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

    }

    private sealed class FakeCampaignRepository : ICampaignRepository
    {

        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            Core.Workspaces.WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Campaign> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

    }

    private sealed class ConfigurableSanctumGuard : ISanctumGuard
    {

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(string campaignId, string toolName, CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(string? workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        
        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            Core.Platform.ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;

    }

}
