using System.Reflection;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Projections;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TurnEngineProjectionCharacterizationTests
{

    [Fact]
    public void ReasoningProjectionContracts_AreAdditiveAndTyped()
    {
        Assert.True(Enum.TryParse("Reasoning", out IntelligenceEventType eventType));
        Assert.Equal("Reasoning", eventType.ToString());

        Assert.Equal(
            typeof(ReasoningContentSegment),
            typeof(IntelligenceEvent).GetProperty("Reasoning")?.PropertyType);
        Assert.Equal(
            typeof(IReadOnlyList<ReasoningContentSegment>),
            typeof(PromptTurnResult).GetProperty("Reasoning")?.PropertyType);

        Type? reasoningDelta = typeof(TurnEvent).Assembly.GetType(
            "RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.ReasoningDelta");
        Assert.NotNull(reasoningDelta);
        Assert.Equal(
            typeof(ReasoningContentSegment),
            reasoningDelta.GetProperty("Reasoning")?.PropertyType);
        Assert.Equal(
            typeof(IReadOnlyList<ReasoningContentSegment>),
            typeof(RunCompleted).GetProperty("Reasoning")?.PropertyType);
    }

    [Fact]
    public void TurnEngineNamespace_HasNoSeedTypesShadowedByWizardIntelligenceProvider()
    {
        // DESIGN §10.7.2's TurnContextSeed / ProviderAttemptContext are the private nested types on
        // WizardIntelligenceProvider — those are the ones the compiler binds. A same-named copy in
        // the TurnEngine namespace is unreachable, so edits to it silently do nothing.
        Assembly api = typeof(TurnEvent).Assembly;

        Assert.Null(api.GetType(
            "RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.TurnContextSeed",
            throwOnError: false));

        Assert.Null(api.GetType(
            "RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.ProviderAttemptContext",
            throwOnError: false));

        Assert.Null(api.GetType(
            "RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.ProviderAttemptState",
            throwOnError: false));

        // The live TurnEngine enums that do have consumers must stay.
        Assert.NotNull(api.GetType(
            "RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.TurnResponseMode",
            throwOnError: false));

        Assert.NotNull(api.GetType(
            "RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.TurnTerminationReason",
            throwOnError: false));
    }

    [Fact]
    public void OpenAiReasoningContracts_HaveOptionalAdditiveFields()
    {
        Assert.Equal(
            typeof(string),
            typeof(OpenAiChatMessage).GetProperty("ReasoningContent")?.PropertyType);
        Assert.Equal(
            typeof(string),
            typeof(OpenAiChatMessage).GetProperty("ReasoningSummary")?.PropertyType);
        Assert.Equal(
            typeof(string),
            typeof(OpenAiChatAssistantMessage).GetProperty("ReasoningContent")?.PropertyType);
        Assert.Equal(
            typeof(string),
            typeof(OpenAiChatAssistantMessage).GetProperty("ReasoningSummary")?.PropertyType);
        Assert.Equal(
            typeof(string),
            typeof(OpenAiDelta).GetProperty("ReasoningContent")?.PropertyType);
        Assert.Equal(
            typeof(string),
            typeof(OpenAiDelta).GetProperty("ReasoningSummary")?.PropertyType);
    }

    [Fact]
    public void OpenAiSseProjection_MapsTextDelta_OmitsWardAndToolResult()
    {
        List<OpenAiChatChunk> chunks = [];
        System.Threading.Channels.Channel<OpenAiChatChunk> channel =
            System.Threading.Channels.Channel.CreateUnbounded<OpenAiChatChunk>();

        OpenAiSseProjection projection = new(channel.Writer, "chatcmpl-test", "gpt-test", createdUnixSeconds: 1);

        TurnEventEmitter emitter = new(Guid.NewGuid());
        TurnEventCorrelation c = emitter.NextCorrelation();

        Assert.Single(projection.Map(new TextDelta(c, "hello")));
        Assert.Empty(projection.Map(new ApprovalRequested(c, "w1", "tool", "{}")));
        Assert.Empty(projection.Map(new ToolInvocationCompleted(
            c,
            "call1",
            "tool",
            "{}",
            "ok",
            Failed: false,
            Denied: false,
            ToleratedFailure: false,
            PublicErrorText: null,
            Duration: TimeSpan.Zero,
            AttachmentPostProcessed: false)));
    }

    [Fact]
    public void OpenAiSseProjection_MapsReasoningKindsInOrderWithoutTerminalDuplicates()
    {
        System.Threading.Channels.Channel<OpenAiChatChunk> channel =
            System.Threading.Channels.Channel.CreateUnbounded<OpenAiChatChunk>();
        OpenAiSseProjection projection = new(
            channel.Writer,
            "chatcmpl-test",
            "gpt-test",
            createdUnixSeconds: 1);
        TurnEventEmitter emitter = new(Guid.NewGuid());
        ReasoningContentSegment summary = new("summary", ReasoningOutputMode.Summary);
        ReasoningContentSegment full = new("full", ReasoningOutputMode.Full);

        List<OpenAiChatChunk> chunks =
        [
            .. projection.Map(new ReasoningDelta(emitter.NextCorrelation(), summary)),
            .. projection.Map(new TextDelta(emitter.NextCorrelation(), "answer")),
            .. projection.Map(new ReasoningDelta(emitter.NextCorrelation(), full)),
            .. projection.Map(new RunCompleted(
                emitter.NextCorrelation(),
                FinalText: "answer",
                Usage: null,
                ToolCalls: null,
                FinishReason: "stop",
                Warnings: [],
                SessionId: null,
                StructuredOutputWarning: false)
            {
                Reasoning = [summary, full],
            }),
        ];

        OpenAiDelta[] deltas = chunks
            .SelectMany(static chunk => chunk.Choices)
            .Select(static choice => choice.Delta)
            .ToArray();
        Assert.Collection(
            deltas,
            delta =>
            {
                Assert.Null(delta.Content);
                Assert.Equal("summary", delta.ReasoningSummary);
                Assert.Null(delta.ReasoningContent);
            },
            delta =>
            {
                Assert.Equal("answer", delta.Content);
                Assert.Null(delta.ReasoningSummary);
                Assert.Null(delta.ReasoningContent);
            },
            delta =>
            {
                Assert.Null(delta.Content);
                Assert.Null(delta.ReasoningSummary);
                Assert.Equal("full", delta.ReasoningContent);
            },
            delta =>
            {
                Assert.Null(delta.Content);
                Assert.Null(delta.ReasoningSummary);
                Assert.Null(delta.ReasoningContent);
            });
    }

    [Theory]
    [InlineData(ErrorCodes.StructuredOutput.ValidationFailed, "validation_failed")]
    [InlineData(ErrorCodes.Guardrails.Blocked, "content_filter")]
    public void OpenAiSseProjection_MapsFailureToTypedErrorChunk(
        string internalCode,
        string expectedOpenAiCode)
    {
        System.Threading.Channels.Channel<OpenAiChatChunk> channel =
            System.Threading.Channels.Channel.CreateUnbounded<OpenAiChatChunk>();
        OpenAiSseProjection projection = new(
            channel.Writer,
            "chatcmpl-test",
            "gpt-test",
            createdUnixSeconds: 1);
        TurnEventEmitter emitter = new(Guid.NewGuid());

        OpenAiChatChunk chunk = Assert.Single(projection.Map(new RunFailed(
            emitter.NextCorrelation(),
            new Error(internalCode, "unsafe internal detail"),
            TurnTerminationReason.ProviderFailure,
            Usage: null,
            Warnings: [],
            Interrupted: false,
            PartialText: null)));

        PropertyInfo? errorProperty = typeof(OpenAiChatChunk).GetProperty("Error");
        Assert.NotNull(errorProperty);
        OpenAiErrorDetail error = Assert.IsType<OpenAiErrorDetail>(
            errorProperty.GetValue(chunk));
        Assert.Equal(expectedOpenAiCode, error.Code);
        Assert.DoesNotContain("unsafe internal detail", error.Message, StringComparison.Ordinal);
        Assert.Equal("error", Assert.Single(chunk.Choices).FinishReason);
    }

    [Theory]
    [InlineData(ErrorCodes.Validation.InvalidReasoningEffort, "invalid_reasoning_effort")]
    [InlineData(ErrorCodes.Validation.InvalidReasoningOutput, "invalid_reasoning_output")]
    [InlineData(ErrorCodes.Validation.ReasoningEffortAndBudgetMutuallyExclusive, "invalid_reasoning_options")]
    [InlineData(ErrorCodes.Validation.InvalidReasoningBudget, "invalid_reasoning_budget")]
    [InlineData(ErrorCodes.Validation.UnsupportedReasoningControl, "unsupported_reasoning_control")]
    [InlineData(ErrorCodes.Validation.ReasoningBudgetExceedsModelLimit, "reasoning_budget_exceeds_model_limit")]
    [InlineData(ErrorCodes.Validation.UnsupportedReasoningOutput, "unsupported_reasoning_output")]
    public void OpenAiSseProjection_MapsReasoningValidationToInvalidRequestError(
        string internalCode,
        string expectedOpenAiCode)
    {
        System.Threading.Channels.Channel<OpenAiChatChunk> channel =
            System.Threading.Channels.Channel.CreateUnbounded<OpenAiChatChunk>();
        OpenAiSseProjection projection = new(
            channel.Writer,
            "chatcmpl-test",
            "gpt-test",
            createdUnixSeconds: 1);
        TurnEventEmitter emitter = new(Guid.NewGuid());

        OpenAiChatChunk chunk = Assert.Single(projection.Map(new RunFailed(
            emitter.NextCorrelation(),
            new Error(internalCode, "candidate detail"),
            TurnTerminationReason.ValidationFailed,
            Usage: null,
            Warnings: [],
            Interrupted: false,
            PartialText: null)));

        Assert.Equal("invalid_request_error", chunk.Error?.Type);
        Assert.Equal(expectedOpenAiCode, chunk.Error?.Code);
    }

    [Fact]
    public void OpenAiSseProjection_MapsAbandonmentToTypedErrorChunk()
    {
        System.Threading.Channels.Channel<OpenAiChatChunk> channel =
            System.Threading.Channels.Channel.CreateUnbounded<OpenAiChatChunk>();
        OpenAiSseProjection projection = new(
            channel.Writer,
            "chatcmpl-test",
            "gpt-test",
            createdUnixSeconds: 1);
        TurnEventEmitter emitter = new(Guid.NewGuid());

        OpenAiChatChunk chunk = Assert.Single(projection.Map(new RunAbandoned(
            emitter.NextCorrelation(),
            Error: null,
            TurnTerminationReason.ClientDisconnected,
            Usage: null,
            Warnings: [],
            Interrupted: true,
            PartialText: null)));

        Assert.Equal("inference_failed", chunk.Error?.Code);
        Assert.Equal("error", Assert.Single(chunk.Choices).FinishReason);
    }

    [Fact]
    public void StreamingIntelligenceMapper_CoalescesHighReasoningDeltaCount()
    {
        const int deltaCount = 10_000;
        Type mapperType = typeof(WizardIntelligenceProvider).GetNestedType(
            "StreamingIntelligenceMapper",
            BindingFlags.NonPublic)!;
        object mapper = Activator.CreateInstance(mapperType, nonPublic: true)!;
        MethodInfo map = mapperType.GetMethod(
            "Map",
            BindingFlags.Instance | BindingFlags.Public)!;
        TurnEventEmitter emitter = new(Guid.NewGuid());

        for (int i = 0; i < deltaCount; i++)
        {
            IntelligenceEvent frame = new(
                IntelligenceEventType.Reasoning,
                "x",
                Reasoning: new ReasoningContentSegment(
                    "x",
                    ReasoningOutputMode.Summary));
            _ = Assert.Single(InvokeMap(mapper, map, frame, emitter));
        }

        RunCompleted completed = Assert.IsType<RunCompleted>(
            Assert.Single(InvokeMap(
                mapper,
                map,
                new IntelligenceEvent(
                    IntelligenceEventType.Result,
                    "answer",
                    "answer",
                    FinishReason: "stop"),
                emitter)));
        ReasoningContentSegment reasoning = Assert.Single(completed.Reasoning);
        Assert.Equal(ReasoningOutputMode.Summary, reasoning.Output);
        Assert.Equal(deltaCount, reasoning.Text.Length);
    }

    [Fact]
    public void StreamingIntelligenceMapper_UsesAccumulatedTokensForFinalText()
    {
        Type mapperType = typeof(WizardIntelligenceProvider).GetNestedType(
            "StreamingIntelligenceMapper",
            BindingFlags.NonPublic)!;
        object mapper = Activator.CreateInstance(mapperType, nonPublic: true)!;
        MethodInfo map = mapperType.GetMethod(
            "Map",
            BindingFlags.Instance | BindingFlags.Public)!;
        TurnEventEmitter emitter = new(Guid.NewGuid());
        ChatCompletionUsage usage = new(11, 7, 18);

        _ = Assert.Single(InvokeMap(
            mapper,
            map,
            new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "answer "),
            emitter));
        _ = Assert.Single(InvokeMap(
            mapper,
            map,
            new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "text"),
            emitter));

        RunCompleted completed = Assert.IsType<RunCompleted>(
            Assert.Single(InvokeMap(
                mapper,
                map,
                new IntelligenceEvent(
                    IntelligenceEventType.Result,
                    "Complete",
                    usage.TotalTokens.ToString(),
                    usage,
                    FinishReason: "stop"),
                emitter)));

        Assert.Equal("answer text", completed.FinalText);
        Assert.Equal(usage, completed.Usage);
    }

    [Fact]
    public async Task IntelligenceEventProjection_MapsReasoningWithoutRepeatingTerminalSegments()
    {
        System.Threading.Channels.Channel<IntelligenceEvent> channel =
            System.Threading.Channels.Channel.CreateUnbounded<IntelligenceEvent>();
        IntelligenceEventProjection projection = new(channel.Writer);
        TurnEventEmitter emitter = new(Guid.NewGuid());
        ReasoningContentSegment reasoning = new("client-safe summary", ReasoningOutputMode.Summary);
        ChatCompletionUsage usage = new(20, 5, 25);

        await projection.ApplyAsync(new ReasoningDelta(emitter.NextCorrelation(), reasoning));
        await projection.ApplyAsync(new RunCompleted(
            emitter.NextCorrelation(),
            FinalText: "answer only",
            Usage: usage,
            ToolCalls: null,
            FinishReason: "stop",
            Warnings: [],
            SessionId: null,
            StructuredOutputWarning: false)
        {
            Reasoning = [reasoning],
        });

        List<IntelligenceEvent> frames = [];
        await foreach (IntelligenceEvent frame in channel.Reader.ReadAllAsync())
        {
            frames.Add(frame);
        }
        IntelligenceEvent reasoningFrame = Assert.Single(
            frames,
            static frame => frame.Type == IntelligenceEventType.Reasoning);
        Assert.Equal(reasoning, reasoningFrame.Reasoning);
        Assert.Equal("client-safe summary", reasoningFrame.Message);
        Assert.Null(reasoningFrame.Data);

        IntelligenceEvent resultFrame = Assert.Single(
            frames,
            static frame => frame.Type == IntelligenceEventType.Result);
        Assert.Equal("answer only", resultFrame.Message);
        Assert.Equal("25", resultFrame.Data);
        Assert.Equal(usage, resultFrame.Usage);
        Assert.DoesNotContain(
            frames,
            static frame => frame.Type == IntelligenceEventType.Token
                && frame.Data?.Contains("client-safe summary", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void BufferedTurnProjection_UsesRunCompletedAsAuthority()
    {
        BufferedTurnProjection projection = new();
        TurnEventEmitter emitter = new(Guid.NewGuid());
        ReasoningContentSegment reasoning = new("separate summary", ReasoningOutputMode.Summary);

        projection.Apply(new RunStarted(emitter.NextCorrelation()));
        projection.Apply(new TextDelta(emitter.NextCorrelation(), "ignored when RunCompleted present"));
        projection.Apply(new RunCompleted(
            emitter.NextCorrelation(),
            FinalText: "final",
            Usage: null,
            ToolCalls: null,
            FinishReason: "stop",
            Warnings: ["w"],
            SessionId: null,
            StructuredOutputWarning: true)
        {
            Reasoning = [reasoning],
        });

        Result<Core.Intelligence.Models.PromptTurnResult> result = projection.ToResult();

        Assert.True(result.IsSuccess);
        Assert.Equal("final", result.Value.Text);
        Assert.Equal("stop", result.Value.FinishReason);
        Assert.Contains("w", result.Value.Warnings);
        Assert.Equal([reasoning], result.Value.Reasoning);
        Assert.DoesNotContain("separate summary", result.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BufferedTurnProjection_RunFailed_SurfacesError()
    {
        BufferedTurnProjection projection = new();
        TurnEventEmitter emitter = new(Guid.NewGuid());

        projection.Apply(new RunFailed(
            emitter.NextCorrelation(),
            new Error(ErrorCodes.Hub.Error, "boom"),
            TurnTerminationReason.ProviderFailure,
            Usage: null,
            Warnings: [],
            Interrupted: false,
            PartialText: null));

        Result<Core.Intelligence.Models.PromptTurnResult> result = projection.ToResult();

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Hub.Error, result.Error.Code);
    }

    [Fact]
    public async Task TurnEngine_UnhandledFailure_EmitsSanitizedCompleteNativeOutput()
    {
        const string canary = "CANARY_TURN_PROVIDER_RESPONSE_BODY";
        TestCapturingLogger<TurnEngine> logger = new();
        TurnEngine engine = new(
            new ThrowingTurnPipelineRunner(new InvalidOperationException(canary)),
            logger);
        List<TurnEvent> events = [];

        await foreach (TurnEvent evt in engine.RunTurnAsync(
            CreateTurnRequest(TurnResponseMode.Streaming),
            CancellationToken.None))
        {
            events.Add(evt);
        }

        RunFailed failed = Assert.IsType<RunFailed>(Assert.Single(events));
        Assert.Equal(ErrorCodes.Hub.Error, failed.Error.Code);
        Assert.Equal(
            "Inference failed. Ensure the provider is running and reachable, then try again. See server logs for details.",
            failed.Error.Message);

        IntelligenceEvent native = Assert.Single(IntelligenceEventProjection.Map(failed));
        string serialized = JsonSerializer.Serialize(
            native,
            ArcanumJsonContext.Default.IntelligenceEvent);
        Assert.DoesNotContain(canary, serialized, StringComparison.Ordinal);

        TestLogEntry log = Assert.Single(logger.Entries);
        Assert.Null(log.Exception);
        Assert.DoesNotContain(canary, log.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), log.Message, StringComparison.Ordinal);
        Assert.Contains(
            failed.Correlation.RunId.ToString("D"),
            log.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurnEngine_NoncancelledProviderOce_EmitsSingleSanitizedRunFailed()
    {
        const string canary = "CANARY_TURN_CANCELLATION_BODY";
        TestCapturingLogger<TurnEngine> logger = new();
        TurnEngine engine = new(new ThrowingTurnPipelineRunner(
            new OperationCanceledException(canary)), logger);
        List<TurnEvent> events = [];

        await foreach (TurnEvent evt in engine.RunTurnAsync(
            CreateTurnRequest(TurnResponseMode.Buffered),
            CancellationToken.None))
        {
            events.Add(evt);
        }

        RunFailed failed = Assert.IsType<RunFailed>(Assert.Single(events));
        Assert.Equal(ErrorCodes.Hub.Error, failed.Error.Code);
        Assert.Equal(
            "Inference failed. Ensure the provider is running and reachable, then try again. See server logs for details.",
            failed.Error.Message);
        Assert.DoesNotContain(canary, failed.Error.Message, StringComparison.Ordinal);

        TestLogEntry log = Assert.Single(logger.Entries);
        Assert.Null(log.Exception);
        Assert.Contains(nameof(OperationCanceledException), log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurnEngine_CancelledOwnedToken_EmitsSingleRunAbandoned()
    {
        using CancellationTokenSource cts = new();
        TurnEngine engine = new(new CancelingTurnPipelineRunner(cts));
        List<TurnEvent> events = [];

        await using IAsyncEnumerator<TurnEvent> enumerator = engine.RunTurnAsync(
                CreateTurnRequest(TurnResponseMode.Streaming),
                cts.Token)
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        events.Add(enumerator.Current);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => enumerator.MoveNextAsync().AsTask());

        RunAbandoned abandoned = Assert.IsType<RunAbandoned>(Assert.Single(events));
        Assert.Equal(TurnTerminationReason.Cancelled, abandoned.Reason);
        Assert.Equal("Turn cancelled.", abandoned.Error?.Message);
    }

    [Fact]
    public async Task TurnEngine_NoncancelledProviderOceAfterTerminal_DoesNotDuplicateOrRethrow()
    {
        TurnEngine engine = new(new TerminalThenThrowingTurnPipelineRunner(
            new OperationCanceledException("provider cancelled")));
        List<TurnEvent> events = [];

        await foreach (TurnEvent evt in engine.RunTurnAsync(
            CreateTurnRequest(TurnResponseMode.Buffered),
            CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.IsType<RunCompleted>(Assert.Single(events));
    }

    [Fact]
    public void PingRequest_HasNoIdempotencyKeyProperty()
    {
        // Forged bodies cannot set HasIdempotencyKey — it is not on the public wire request.
        Assert.Null(typeof(Core.Intelligence.PingRequest).GetProperty("HasIdempotencyKey"));
        Assert.Null(typeof(Core.Intelligence.PingRequest).GetProperty("IdempotencyKey"));
    }

    /// <summary>
    /// The hub emits <c>toolError</c> as (Message = tool name, Data = failure description). Reading
    /// the tool name into <c>PublicErrorText</c> makes the re-projected wire frame carry the name in
    /// both <c>message</c> and <c>data</c>, so the description is lost on the round trip.
    /// </summary>
    [Fact]
    public void StreamingIntelligenceMapper_ToolError_CarriesTheFailureDescriptionNotTheToolName()
    {
        Type mapperType = typeof(WizardIntelligenceProvider).GetNestedType(
            "StreamingIntelligenceMapper",
            BindingFlags.NonPublic)!;
        object mapper = Activator.CreateInstance(mapperType, nonPublic: true)!;
        MethodInfo map = mapperType.GetMethod(
            "Map",
            BindingFlags.Instance | BindingFlags.Public)!;
        TurnEventEmitter emitter = new(Guid.NewGuid());

        const string description =
            "Tool invocation failed and was tolerated; a synthetic error result was returned to the model.";

        Assert.Empty(InvokeMap(
            mapper,
            map,
            new IntelligenceEvent(
                IntelligenceEventType.ToolError,
                "execute_command",
                description),
            emitter));

        ToolInvocationCompleted completed = Assert.IsType<ToolInvocationCompleted>(
            Assert.Single(InvokeMap(
                mapper,
                map,
                new IntelligenceEvent(
                    IntelligenceEventType.ToolResult,
                    "execute_command",
                    "synthetic error result",
                    ToolCall: new IntelligenceToolCallEvent(
                        "call_1",
                        "execute_command",
                        "{}")),
                emitter)));

        Assert.True(completed.Failed);

        Assert.Equal(description, completed.PublicErrorText);
    }

    /// <summary>
    /// The hub sets <c>PreserveProviderCallId</c> on client-forwarded tool calls so <c>/v1</c> echoes
    /// the provider's own <c>tool_call_id</c> back. Production streaming goes through the semantic
    /// round trip, so the flag has to survive both hops or the client gets a fabricated id.
    /// </summary>
    [Fact]
    public void SemanticRoundTrip_PreservesTheProviderToolCallIdFlag()
    {
        Type mapperType = typeof(WizardIntelligenceProvider).GetNestedType(
            "StreamingIntelligenceMapper",
            BindingFlags.NonPublic)!;
        object mapper = Activator.CreateInstance(mapperType, nonPublic: true)!;
        MethodInfo map = mapperType.GetMethod(
            "Map",
            BindingFlags.Instance | BindingFlags.Public)!;
        TurnEventEmitter emitter = new(Guid.NewGuid());

        TurnEvent mapped = Assert.Single(InvokeMap(
            mapper,
            map,
            new IntelligenceEvent(
                IntelligenceEventType.ToolCall,
                "get_weather",
                """{"city":"Oslo"}""",
                ToolCall: new IntelligenceToolCallEvent(
                    "call_provider_abc",
                    "get_weather",
                    """{"city":"Oslo"}""",
                    Index: 0,
                    PreserveProviderCallId: true)),
            emitter));

        IntelligenceEvent reprojected = Assert.Single(IntelligenceEventProjection.Map(mapped));

        Assert.True(reprojected.ToolCall!.PreserveProviderCallId);

        Assert.Equal("call_provider_abc", reprojected.ToolCall.CallId);
    }

    private static IEnumerable<TurnEvent> InvokeMap(
        object mapper,
        MethodInfo map,
        IntelligenceEvent frame,
        TurnEventEmitter emitter) =>
        Assert.IsAssignableFrom<IEnumerable<TurnEvent>>(
            map.Invoke(mapper, [frame, emitter]));

    private static TurnExecutionRequest CreateTurnRequest(TurnResponseMode mode) =>
        new(
            new PingRequest("test"),
            InvocationContexts.AttendedSession(),
            mode,
            TurnPurpose.Interactive,
            HumanInteractionAvailable: mode == TurnResponseMode.Streaming,
            HasIdempotencyKey: false,
            AccountingHandle: null);

    private sealed class ThrowingTurnPipelineRunner(Exception failure) : ITurnPipelineRunner
    {
        public Task RunBufferedIntoEmitterAsync(
            TurnExecutionRequest request,
            TurnEventEmitter emitter,
            InferenceAuditContext? auditContext,
            CancellationToken cancellationToken) =>
            Task.FromException(failure);

        public Task RunStreamingIntoEmitterAsync(
            TurnExecutionRequest request,
            TurnEventEmitter emitter,
            InferenceAuditContext? auditContext,
            CancellationToken cancellationToken) =>
            Task.FromException(failure);
    }

    private sealed class CancelingTurnPipelineRunner(CancellationTokenSource cancellation) : ITurnPipelineRunner
    {
        public Task RunBufferedIntoEmitterAsync(
            TurnExecutionRequest request,
            TurnEventEmitter emitter,
            InferenceAuditContext? auditContext,
            CancellationToken cancellationToken) =>
            CancelAndThrow();

        public Task RunStreamingIntoEmitterAsync(
            TurnExecutionRequest request,
            TurnEventEmitter emitter,
            InferenceAuditContext? auditContext,
            CancellationToken cancellationToken) =>
            CancelAndThrow();

        private Task CancelAndThrow()
        {
            cancellation.Cancel();
            return Task.FromException(new OperationCanceledException(cancellation.Token));
        }
    }

    private sealed class TerminalThenThrowingTurnPipelineRunner(Exception failure) : ITurnPipelineRunner
    {
        public Task RunBufferedIntoEmitterAsync(
            TurnExecutionRequest request,
            TurnEventEmitter emitter,
            InferenceAuditContext? auditContext,
            CancellationToken cancellationToken) =>
            EmitThenThrowAsync(emitter);

        public Task RunStreamingIntoEmitterAsync(
            TurnExecutionRequest request,
            TurnEventEmitter emitter,
            InferenceAuditContext? auditContext,
            CancellationToken cancellationToken) =>
            EmitThenThrowAsync(emitter);

        private async Task EmitThenThrowAsync(TurnEventEmitter emitter)
        {
            await emitter.EmitAsync(
                new RunCompleted(
                    emitter.NextCorrelation(),
                    FinalText: "done",
                    Usage: null,
                    ToolCalls: null,
                    FinishReason: "stop",
                    Warnings: [],
                    SessionId: null,
                    StructuredOutputWarning: false),
                CancellationToken.None);
            throw failure;
        }
    }

}
