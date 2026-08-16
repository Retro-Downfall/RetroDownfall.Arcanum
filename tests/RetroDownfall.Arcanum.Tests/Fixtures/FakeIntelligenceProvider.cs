using System.Threading;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public sealed class FakeIntelligenceProvider : IArcanumIntelligenceProvider, IContextPreviewService
{

    public ContextPreviewResult NextContextPreview { get; set; } = ContextPreviewTestData.Create();

    public ContextPreviewRequest? LastContextPreviewRequest { get; private set; }

    public int PreviewContextCallCount { get; private set; }

    public string NextText { get; set; } = "pong";

    public Error? NextFailure { get; set; }

    public List<PromptToolCall>? NextToolCalls { get; set; }

    public IReadOnlyList<ReasoningContentSegment>? NextReasoning { get; set; }

    public string? NextFinishReason { get; set; }

    public Exception? NextStreamException { get; set; }

    public Exception? ThrowOnSecondYield { get; set; }

    public string? LastPrompt { get; private set; }

    /// <summary>
    /// The full <see cref="PingRequest"/> passed to the most recent <see cref="ExecutePromptAsync"/>
    /// call — lets tests inspect <see cref="PingRequest.StatelessMessages"/> (for example to verify
    /// client tool-call/tool-result replay was mapped correctly by <c>OpenAiChatCompletionMapper</c>)
    /// beyond just the flattened <see cref="LastPrompt"/> string.
    /// </summary>
    public PingRequest? LastRequest { get; private set; }

    /// <summary>The <see cref="InferenceAuditContext"/> passed to the most recent call, if any.</summary>
    public InferenceAuditContext? LastAuditContext { get; private set; }

    public bool StreamCancellationObserved { get; private set; }

    private int _executePromptCallCount;

    private int _streamPromptCallCount;

    /// <summary>
    /// Number of times <see cref="ExecutePromptAsync"/> has been invoked — lets idempotency-replay
    /// tests assert the handler was not re-executed on a cache hit.
    /// </summary>
    /// <remarks>
    /// Incremented with <see cref="Interlocked"/> because batch processing fans out over
    /// <c>Parallel.ForEachAsync</c> with <c>MaxConcurrentRequestsPerBatch</c> workers. A plain
    /// <c>++</c> loses updates under that contention, which surfaced as a count one short of the
    /// request total only when the full suite was loading the machine — the classic
    /// "unrelated test is flaky" shape.
    /// </remarks>
    public int ExecutePromptCallCount => Volatile.Read(ref _executePromptCallCount);

    /// <summary>Number of times <see cref="StreamPromptAsync"/> has been invoked — see <see cref="ExecutePromptCallCount"/>.</summary>
    public int StreamPromptCallCount => Volatile.Read(ref _streamPromptCallCount);

    /// <summary>
    /// When set, <see cref="StreamPromptAsync"/> yields one <see cref="IntelligenceEventType.ToolCall"/>
    /// event per entry (in order) before the final <see cref="IntelligenceEventType.Token"/>/
    /// <see cref="IntelligenceEventType.Result"/> events — simulates Arcanum's server-side tool loop
    /// for testing the <c>/v1</c> streaming <c>delta.tool_calls</c> bridge.
    /// </summary>
    public List<IntelligenceToolCallEvent>? NextStreamToolCalls { get; set; }

    public IReadOnlyList<IntelligenceEvent>? NextStreamEvents { get; set; }

    public List<string>? NextWarnings { get; set; }

    /// <summary>
    /// When set, <see cref="ExecutePromptAsync"/> waits on this gate (or until cancelled) before
    /// returning — used by batch in-flight expiry tests.
    /// </summary>
    public TaskCompletionSource? ExecuteGate { get; set; }

    public TaskCompletionSource? ExecuteEntered { get; set; }

    public Task<Result<PromptTurnResult>> ExecutePromptAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        CancellationToken cancellationToken,
        InferenceAuditContext? auditContext = null)
    {

        _ = Interlocked.Increment(ref _executePromptCallCount);

        ExecuteEntered?.TrySetResult();

        LastPrompt = request.Prompt;

        LastRequest = request;

        LastAuditContext = auditContext;

        if (ExecuteGate is { } gate)
        {

            return WaitForGateThenCompleteAsync(gate, request, cancellationToken);

        }

        if (NextFailure is Error failure)
        {

            return Task.FromResult(Result<PromptTurnResult>.Failure(failure));

        }

        return Task.FromResult(
            Result<PromptTurnResult>.Success(new PromptTurnResult(NextText, null, NextToolCalls, NextFinishReason)
            {
                Warnings = NextWarnings ?? [],
                Reasoning = NextReasoning ?? [],
                PreserveProviderToolCallIds = request.ForwardClientTools,
            }));

    }

    public Task<Result<ContextPreviewResult>> PreviewContextAsync(

        ContextPreviewRequest request,

        ArcanumInvocationContext invocationContext,

        CancellationToken cancellationToken)

    {

        cancellationToken.ThrowIfCancellationRequested();

        PreviewContextCallCount++;

        LastContextPreviewRequest = request;

        ContextPreviewResult result = request.ShowContent

            ? ContextPreviewTestData.Create(showContent: true)

            : NextContextPreview with { Content = null };

        return Task.FromResult(Result<ContextPreviewResult>.Success(result));

    }

    private async Task<Result<PromptTurnResult>> WaitForGateThenCompleteAsync(
        TaskCompletionSource gate,
        PingRequest request,
        CancellationToken cancellationToken)
    {

        await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (NextFailure is Error failure)
        {

            return Result<PromptTurnResult>.Failure(failure);

        }

        return Result<PromptTurnResult>.Success(new PromptTurnResult(NextText, null, NextToolCalls, NextFinishReason)
        {
            Warnings = NextWarnings ?? [],
            Reasoning = NextReasoning ?? [],
            PreserveProviderToolCallIds = request.ForwardClientTools,
        });

    }

    public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        InferenceAuditContext? auditContext = null)
    {

        _ = Interlocked.Increment(ref _streamPromptCallCount);

        LastRequest = request;

        LastAuditContext = auditContext;

        await Task.CompletedTask.ConfigureAwait(false);

        if (cancellationToken.CanBeCanceled)
        {

            cancellationToken.Register(() => StreamCancellationObserved = true);

        }

        if (NextFailure is Error failure)
        {

            yield return new IntelligenceEvent(
                IntelligenceEventType.Error,
                failure.Message,
                failure.Code);

            yield break;

        }

        if (NextStreamException is Exception ex)
        {

            throw ex;

        }

        if (NextStreamEvents is { } streamEvents)
        {
            foreach (IntelligenceEvent streamEvent in streamEvents)
            {
                yield return streamEvent;
            }

            yield break;
        }

        if (NextStreamToolCalls is { Count: > 0 } toolCalls)
        {

            foreach (IntelligenceToolCallEvent toolCall in toolCalls)
            {

                yield return new IntelligenceEvent(
                    IntelligenceEventType.ToolCall,
                    toolCall.Name,
                    toolCall.ArgumentsJson,
                    null,
                    toolCall with { PreserveProviderCallId = request.ForwardClientTools });

            }

        }

        yield return new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, NextText);

        if (ThrowOnSecondYield is Exception secondEx)
        {

            throw secondEx;

        }

        yield return new IntelligenceEvent(
            IntelligenceEventType.Result,
            "Complete",
            "0",
            null,
            FinishReason: NextFinishReason ?? "stop")
        {
            Warnings = NextWarnings ?? []
        };

    }

}
