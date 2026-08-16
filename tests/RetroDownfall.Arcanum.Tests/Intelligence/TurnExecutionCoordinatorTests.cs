using System.Runtime.CompilerServices;

using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Projections;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TurnExecutionCoordinatorTests
{
    [Fact]
    public void Constructor_NullEventSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TurnExecutionCoordinator(null!));
    }

    [Fact]
    public async Task ExecuteBufferedAsync_CompletedTurn_ProjectsAuthoritativeResultAndRequestSemantics()
    {
        ChatCompletionUsage usage = new(10, 4, 14);
        PromptToolCall toolCall = new("call-1", "clock", "{}");
        ScriptedTurnEventSource source = new(
            new TextDelta(Correlation(1), "incremental"),
            new RunCompleted(
                Correlation(2),
                FinalText: "final",
                Usage: usage,
                ToolCalls: [toolCall],
                FinishReason: "tool_calls",
                Warnings: ["warning"],
                SessionId: Guid.NewGuid(),
                StructuredOutputWarning: false));
        TurnExecutionCoordinator coordinator = new(source);

        Result<PromptTurnResult> result = await coordinator.ExecuteBufferedAsync(
            new PingRequest("prompt"),
            InvocationContexts.AttendedSession(),
            hasIdempotencyKey: true,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("final", result.Value.Text);
        Assert.Equal(usage, result.Value.Usage);
        Assert.Equal([toolCall], result.Value.ToolCalls);
        Assert.Equal("tool_calls", result.Value.FinishReason);
        Assert.Equal(["warning"], result.Value.Warnings);
        Assert.NotNull(source.CapturedRequest);
        Assert.Equal(TurnResponseMode.Buffered, source.CapturedRequest.ResponseMode);
        Assert.False(source.CapturedRequest.HumanInteractionAvailable);
        Assert.True(source.CapturedRequest.HasIdempotencyKey);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_AbandonedTurnWithoutError_UsesPublicFallback()
    {
        ScriptedTurnEventSource source = new(
            new RunAbandoned(
                Correlation(1),
                Error: null,
                TurnTerminationReason.ClientDisconnected,
                Usage: null,
                Warnings: [],
                Interrupted: true,
                PartialText: "partial"));
        TurnExecutionCoordinator coordinator = new(source);

        Result<PromptTurnResult> result = await coordinator.ExecuteBufferedAsync(
            new PingRequest("prompt"),
            InvocationContexts.AttendedSession(),
            hasIdempotencyKey: false,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Hub.Error, result.Error.Code);
        Assert.Equal("Turn abandoned.", result.Error.Message);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_AbandonedTurnWithError_PreservesSemanticError()
    {
        Error expected = new(ErrorCodes.Guardrails.Blocked, "blocked");
        TurnExecutionCoordinator coordinator = new(
            new ScriptedTurnEventSource(
                new RunAbandoned(
                    Correlation(1),
                    expected,
                    TurnTerminationReason.GuardrailsBlocked,
                    Usage: null,
                    Warnings: [],
                    Interrupted: false,
                    PartialText: null)));

        Result<PromptTurnResult> result = await coordinator.ExecuteBufferedAsync(
            new PingRequest("prompt"),
            InvocationContexts.AttendedSession(),
            hasIdempotencyKey: false,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_NoTerminalEvent_ReturnsExplicitFailure()
    {
        TurnExecutionCoordinator coordinator = new(
            new ScriptedTurnEventSource(new RunStarted(Correlation(1))));

        Result<PromptTurnResult> result = await coordinator.ExecuteBufferedAsync(
            new PingRequest("prompt"),
            InvocationContexts.AttendedSession(),
            hasIdempotencyKey: false,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Hub.Error, result.Error.Code);
        Assert.Contains("without a terminal event", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_CancelledEnumeration_PropagatesCancellation()
    {
        TurnExecutionCoordinator coordinator = new(new ScriptedTurnEventSource());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.ExecuteBufferedAsync(
                new PingRequest("prompt"),
                InvocationContexts.AttendedSession(),
                hasIdempotencyKey: false,
                cancellation.Token));
    }

    [Fact]
    public async Task ExecuteIntelligenceStreamAsync_FailedTurn_EmitsErrorAndCompletes()
    {
        Error failure = new(ErrorCodes.Hub.Error, "provider unavailable");
        ScriptedTurnEventSource source = new(
            new TurnStatusChanged(Correlation(1), "working"),
            new RunFailed(
                Correlation(2),
                failure,
                TurnTerminationReason.ProviderFailure,
                Usage: null,
                Warnings: [],
                Interrupted: false,
                PartialText: null));
        TurnExecutionCoordinator coordinator = new(source);

        List<IntelligenceEvent> frames = await ReadAllAsync(
            coordinator.ExecuteIntelligenceStreamAsync(
                new PingRequest("prompt"),
                InvocationContexts.AttendedSession(),
                hasIdempotencyKey: true,
                CancellationToken.None));

        Assert.Collection(
            frames,
            frame =>
            {
                Assert.Equal(IntelligenceEventType.Status, frame.Type);
                Assert.Equal("working", frame.Message);
            },
            frame =>
            {
                Assert.Equal(IntelligenceEventType.Error, frame.Type);
                Assert.Equal(failure.Message, frame.Message);
                Assert.Equal(failure.Code, frame.Data);
            });
        Assert.NotNull(source.CapturedRequest);
        Assert.Equal(TurnResponseMode.Streaming, source.CapturedRequest.ResponseMode);
        Assert.True(source.CapturedRequest.HumanInteractionAvailable);
    }

    [Fact]
    public async Task ExecuteIntelligenceStreamAsync_SourceEndsWithoutTerminal_CompletesReader()
    {
        TurnExecutionCoordinator coordinator = new(
            new ScriptedTurnEventSource(
                new TurnStatusChanged(Correlation(1), "working")));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));

        List<IntelligenceEvent> frames = await ReadAllAsync(
                coordinator.ExecuteIntelligenceStreamAsync(
                    new PingRequest("prompt"),
                    InvocationContexts.AttendedSession(),
                    hasIdempotencyKey: false,
                    timeout.Token))
            .WaitAsync(TimeSpan.FromSeconds(5));

        IntelligenceEvent frame = Assert.Single(frames);
        Assert.Equal(IntelligenceEventType.Status, frame.Type);
        Assert.Equal("working", frame.Message);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task ExecuteIntelligenceStreamAsync_SourceThrows_CompletesReaderWithSourceError()
    {
        InvalidOperationException expected = new("source failed");
        ScriptedTurnEventSource source = new(
            new TurnStatusChanged(Correlation(1), "working"))
        {
            ExceptionAfterEvents = expected,
        };
        TurnExecutionCoordinator coordinator = new(source);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAllAsync(
                    coordinator.ExecuteIntelligenceStreamAsync(
                        new PingRequest("prompt"),
                        InvocationContexts.AttendedSession(),
                        hasIdempotencyKey: false,
                        timeout.Token))
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(expected, actual);
        Assert.False(timeout.IsCancellationRequested);
    }

    /// <summary>
    /// A client that disconnects mid-stream disposes this iterator at its <c>yield return</c>. The
    /// producer is still unwinding the whole turn pipeline, whose finally blocks finalize the
    /// Grimoire entry, reconcile the budget reservation, and close the InferenceRun against
    /// request-scoped services on <c>CancellationToken.None</c>. Returning without joining it hands
    /// those writes a scope ASP.NET is about to dispose.
    /// </summary>
    [Fact]
    public async Task ExecuteIntelligenceStreamAsync_ConsumerBreaksEarly_JoinsProducerCleanup()
    {
        CleanupTrackingTurnEventSource source = new();
        TurnExecutionCoordinator coordinator = new(source);

        await foreach (IntelligenceEvent frame in coordinator
            .ExecuteIntelligenceStreamAsync(
                new PingRequest("prompt"),
                InvocationContexts.AttendedSession(),
                hasIdempotencyKey: false,
                CancellationToken.None)
            .WithCancellation(CancellationToken.None))
        {
            Assert.Equal(IntelligenceEventType.Status, frame.Type);
            break;
        }

        Assert.True(source.CleanupCompleted);
    }

    [Fact]
    public async Task ExecuteIntelligenceStreamAsync_ConsumerCancels_JoinsProducerCleanup()
    {
        CleanupTrackingTurnEventSource source = new();
        TurnExecutionCoordinator coordinator = new(source);
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (IntelligenceEvent _ in coordinator
                .ExecuteIntelligenceStreamAsync(
                    new PingRequest("prompt"),
                    InvocationContexts.AttendedSession(),
                    hasIdempotencyKey: false,
                    cancellation.Token)
                .WithCancellation(CancellationToken.None))
            {
                await cancellation.CancelAsync();
            }
        }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(source.CleanupCompleted);
    }

    [Fact]
    public async Task ExecuteOpenAiSseAsync_ConsumerBreaksEarly_JoinsProducerCleanup()
    {
        CleanupTrackingTurnEventSource source = new(new TextDelta(Correlation(1), "partial"));
        TurnExecutionCoordinator coordinator = new(source);

        await foreach (OpenAiChatChunk _ in coordinator
            .ExecuteOpenAiSseAsync(
                new PingRequest("prompt"),
                InvocationContexts.AttendedSession(),
                hasIdempotencyKey: false,
                completionId: "chatcmpl-disconnect",
                model: "test-model",
                CancellationToken.None)
            .WithCancellation(CancellationToken.None))
        {
            break;
        }

        Assert.True(source.CleanupCompleted);
    }

    [Fact]
    public async Task ExecuteOpenAiSseAsync_CompletedTurn_StreamsReasoningTextAndTerminalUsage()
    {
        ReasoningContentSegment reasoning = new("summary", ReasoningOutputMode.Summary);
        ChatCompletionUsage usage = new(7, 3, 10);
        ScriptedTurnEventSource source = new(
            new RunCompleted(
                Correlation(1),
                FinalText: "answer",
                Usage: usage,
                ToolCalls: null,
                FinishReason: null,
                Warnings: [],
                SessionId: null,
                StructuredOutputWarning: false)
            {
                Reasoning = [reasoning],
            });
        TurnExecutionCoordinator coordinator = new(source);

        List<OpenAiChatChunk> chunks = await ReadAllAsync(
            coordinator.ExecuteOpenAiSseAsync(
                new PingRequest("prompt"),
                InvocationContexts.AttendedSession(),
                hasIdempotencyKey: false,
                completionId: "chatcmpl-coordinator",
                model: "test-model",
                CancellationToken.None));

        Assert.Collection(
            chunks,
            chunk =>
            {
                Assert.Equal("chatcmpl-coordinator", chunk.Id);
                Assert.Equal("test-model", chunk.Model);
                Assert.Equal("summary", Assert.Single(chunk.Choices).Delta.ReasoningSummary);
            },
            chunk =>
            {
                Assert.Equal(usage, chunk.Usage);
                Assert.Equal("stop", Assert.Single(chunk.Choices).FinishReason);
            });
        Assert.NotNull(source.CapturedRequest);
        Assert.Equal(TurnResponseMode.Streaming, source.CapturedRequest.ResponseMode);
        Assert.True(source.CapturedRequest.HumanInteractionAvailable);
    }

    [Fact]
    public async Task ExecuteOpenAiSseAsync_SourceEndsWithoutTerminal_CompletesReader()
    {
        TurnExecutionCoordinator coordinator = new(
            new ScriptedTurnEventSource(new TextDelta(Correlation(1), "partial")));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));

        List<OpenAiChatChunk> chunks = await ReadAllAsync(
                coordinator.ExecuteOpenAiSseAsync(
                    new PingRequest("prompt"),
                    InvocationContexts.AttendedSession(),
                    hasIdempotencyKey: false,
                    completionId: "chatcmpl-no-terminal",
                    model: "test-model",
                    timeout.Token))
            .WaitAsync(TimeSpan.FromSeconds(5));

        OpenAiChatChunk chunk = Assert.Single(chunks);
        Assert.Equal("partial", Assert.Single(chunk.Choices).Delta.Content);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task ExecuteOpenAiSseAsync_SourceThrows_CompletesReaderWithSourceError()
    {
        InvalidOperationException expected = new("source failed");
        ScriptedTurnEventSource source = new(new TextDelta(Correlation(1), "partial"))
        {
            ExceptionAfterEvents = expected,
        };
        TurnExecutionCoordinator coordinator = new(source);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAllAsync(
                    coordinator.ExecuteOpenAiSseAsync(
                        new PingRequest("prompt"),
                        InvocationContexts.AttendedSession(),
                        hasIdempotencyKey: false,
                        completionId: "chatcmpl-source-error",
                        model: "test-model",
                        timeout.Token))
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(expected, actual);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task ExecuteBufferedCoreAsync_StreamingRequest_RejectsModeMismatch()
    {
        TurnExecutionCoordinator coordinator = new(new ScriptedTurnEventSource());

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.ExecuteBufferedCoreAsync(
                Request(TurnResponseMode.Streaming),
                CancellationToken.None));

        Assert.Equal("request", error.ParamName);
    }

    [Fact]
    public async Task ExecuteIntelligenceStreamCoreAsync_BufferedRequest_RejectsModeMismatch()
    {
        TurnExecutionCoordinator coordinator = new(new ScriptedTurnEventSource());
        await using IAsyncEnumerator<IntelligenceEvent> enumerator = coordinator
            .ExecuteIntelligenceStreamCoreAsync(
                Request(TurnResponseMode.Buffered),
                CancellationToken.None)
            .GetAsyncEnumerator();

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => enumerator.MoveNextAsync().AsTask());

        Assert.Equal("request", error.ParamName);
    }

    [Fact]
    public async Task ExecuteOpenAiSseCoreAsync_BufferedRequest_RejectsModeMismatch()
    {
        TurnExecutionCoordinator coordinator = new(new ScriptedTurnEventSource());
        await using IAsyncEnumerator<OpenAiChatChunk> enumerator = coordinator
            .ExecuteOpenAiSseCoreAsync(
                Request(TurnResponseMode.Buffered),
                "chatcmpl-test",
                "test-model",
                CancellationToken.None)
            .GetAsyncEnumerator();

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => enumerator.MoveNextAsync().AsTask());

        Assert.Equal("request", error.ParamName);
    }

    [Fact]
    public void BufferedTurnProjection_NullEvent_Throws()
    {
        BufferedTurnProjection projection = new();

        Assert.Throws<ArgumentNullException>(() => projection.Apply(null!));
    }

    private static TurnExecutionRequest Request(TurnResponseMode responseMode) =>
        new(
            new PingRequest("prompt"),
            InvocationContexts.AttendedSession(),
            responseMode,
            TurnPurpose.Interactive,
            HumanInteractionAvailable: responseMode == TurnResponseMode.Streaming,
            HasIdempotencyKey: false,
            AccountingHandle: null);

    private static TurnEventCorrelation Correlation(long sequence) =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            sequence,
            ProviderAttempt: 0,
            ModelRound: 0,
            ModelCallId: null,
            ToolCallId: null,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence));

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        List<T> items = [];
        await foreach (T item in source)
        {
            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// Emits one frame, then blocks until the producer's own token is cancelled, and records that
    /// its cleanup ran. Stands in for the Wizard pipeline's finally blocks, which do their durable
    /// work on <see cref="CancellationToken.None"/> and therefore keep running after the turn's
    /// execution token is gone.
    /// </summary>
    private sealed class CleanupTrackingTurnEventSource(TurnEvent? first = null) : ITurnEventSource
    {
        private readonly TurnEvent _first = first ?? new TurnStatusChanged(Correlation(1), "working");

        public bool CleanupCompleted { get; private set; }

        public async IAsyncEnumerable<TurnEvent> RunTurnAsync(
            TurnExecutionRequest request,
            [EnumeratorCancellation] CancellationToken executionToken)
        {
            try
            {
                yield return _first;

                // Bounded so an orphaned producer cannot outlive the test run.
                await Task.Delay(TimeSpan.FromSeconds(10), executionToken).ConfigureAwait(false);
            }
            finally
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), CancellationToken.None)
                    .ConfigureAwait(false);

                CleanupCompleted = true;
            }
        }
    }

    [Fact]
    public async Task ExecuteBufferedAsync_PreservesReferenceIdenticalInvocationContext()
    {
        ScriptedTurnEventSource source = new(
            new RunCompleted(
                Correlation(1),
                FinalText: "final",
                Usage: null,
                ToolCalls: [],
                FinishReason: "stop",
                Warnings: [],
                SessionId: null,
                StructuredOutputWarning: false));
        TurnExecutionCoordinator coordinator = new(source);

        // Reference identity, not equality: the runner and the commit path must be reasoning about the
        // one authority decision the boundary made, never a reconstructed copy of it.
        ArcanumInvocationContext expected = InvocationContexts.AttendedSession();

        _ = await coordinator.ExecuteBufferedAsync(
            new PingRequest("prompt"),
            expected,
            hasIdempotencyKey: false,
            CancellationToken.None);

        Assert.Same(expected, source.CapturedRequest!.InvocationContext);
    }

    [Fact]
    public async Task ExecuteStreamAsync_PreservesReferenceIdenticalInvocationContext()
    {
        ScriptedTurnEventSource source = new(
            new RunCompleted(
                Correlation(1),
                FinalText: "final",
                Usage: null,
                ToolCalls: [],
                FinishReason: "stop",
                Warnings: [],
                SessionId: null,
                StructuredOutputWarning: false));
        TurnExecutionCoordinator coordinator = new(source);

        ArcanumInvocationContext expected = InvocationContexts.NoContextSession();

        await foreach (IntelligenceEvent _ in coordinator.ExecuteIntelligenceStreamAsync(
            new PingRequest("prompt"),
            expected,
            hasIdempotencyKey: false,
            CancellationToken.None))
        {
        }

        Assert.Same(expected, source.CapturedRequest!.InvocationContext);
        Assert.False(source.CapturedRequest.InvocationContext.CanReadCovenant);
    }

    [Fact]
    public async Task ExecuteBufferedAsync_NullInvocationContextIsRejected()
    {
        TurnExecutionCoordinator coordinator = new(new ScriptedTurnEventSource());

        _ = await Assert.ThrowsAsync<ArgumentNullException>(
            () => coordinator.ExecuteBufferedAsync(
                new PingRequest("prompt"),
                null!,
                hasIdempotencyKey: false,
                CancellationToken.None));
    }

    private sealed class ScriptedTurnEventSource(params TurnEvent[] events) : ITurnEventSource
    {
        public TurnExecutionRequest? CapturedRequest { get; private set; }

        public Exception? ExceptionAfterEvents { get; init; }

        public async IAsyncEnumerable<TurnEvent> RunTurnAsync(
            TurnExecutionRequest request,
            [EnumeratorCancellation] CancellationToken executionToken)
        {
            CapturedRequest = request;
            executionToken.ThrowIfCancellationRequested();

            foreach (TurnEvent evt in events)
            {
                executionToken.ThrowIfCancellationRequested();
                yield return evt;
                await Task.Yield();
            }

            if (ExceptionAfterEvents is not null)
            {
                throw ExceptionAfterEvents;
            }
        }
    }
}
