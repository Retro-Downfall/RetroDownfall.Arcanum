using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public sealed class FakeIntelligenceProvider : IArcanumIntelligenceProvider
{

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

    /// <summary>Number of times <see cref="ExecutePromptAsync"/> has been invoked — lets idempotency-replay tests assert the handler was not re-executed on a cache hit.</summary>
    public int ExecutePromptCallCount { get; private set; }

    /// <summary>Number of times <see cref="StreamPromptAsync"/> has been invoked — see <see cref="ExecutePromptCallCount"/>.</summary>
    public int StreamPromptCallCount { get; private set; }

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
        CancellationToken cancellationToken = default,
        InferenceAuditContext? auditContext = null)
    {

        ExecutePromptCallCount++;

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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
        InferenceAuditContext? auditContext = null)
    {

        StreamPromptCallCount++;

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
