using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

/// <summary>
/// Public facade over <see cref="TurnExecutionCoordinator"/> so
/// <c>WizardIntelligenceProvider</c> (the Master implementation) can take a <see cref="Lazy{T}"/>
/// without exposing the internal coordinator type on a public ctor.
/// </summary>
/// <remarks>
/// Every method takes the caller's <see cref="ArcanumInvocationContext"/> as a required argument and
/// passes the same reference through unchanged. Reference identity matters: the coordinator, the
/// runner, and the commit path must all be reasoning about one authority decision, not three copies
/// that a later edit could let drift apart (§10.12).
/// </remarks>
public interface ITurnExecutionFacade
{

    Task<Result<PromptTurnResult>> ExecuteBufferedAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        bool hasIdempotencyKey,
        CancellationToken executionToken,
        InferenceAuditContext? auditContext = null);

    IAsyncEnumerable<IntelligenceEvent> ExecuteIntelligenceStreamAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        bool hasIdempotencyKey,
        CancellationToken executionToken,
        InferenceAuditContext? auditContext = null);

    /// <summary>
    /// Projects the same semantic turn stream into OpenAI SSE chunks (no HTTP serialization).
    /// </summary>
    /// <remarks>
    /// No audit context, unlike the two members above. Both of those have production callers that
    /// supply one; this member has no production caller at all, because the chat-completions endpoint
    /// streams through <c>IArcanumIntelligenceProvider</c> and serializes SSE itself. A parameter no
    /// caller can pass on a member no caller reaches is two layers of unreachable, and carrying it
    /// implied an audit path that never runs. A caller that appears will bring the argument with it.
    /// </remarks>
    IAsyncEnumerable<OpenAiChatChunk> ExecuteOpenAiSseAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        bool hasIdempotencyKey,
        string completionId,
        string model,
        CancellationToken executionToken);

}
