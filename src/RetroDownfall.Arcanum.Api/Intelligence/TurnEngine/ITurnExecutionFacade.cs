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
public interface ITurnExecutionFacade
{

    Task<Result<PromptTurnResult>> ExecuteBufferedAsync(
        PingRequest request,
        bool hasIdempotencyKey,
        CancellationToken executionToken,
        InferenceAuditContext? auditContext = null);

    IAsyncEnumerable<IntelligenceEvent> ExecuteIntelligenceStreamAsync(
        PingRequest request,
        bool hasIdempotencyKey,
        CancellationToken executionToken,
        InferenceAuditContext? auditContext = null);

    /// <summary>
    /// Projects the same semantic turn stream into OpenAI SSE chunks (no HTTP serialization).
    /// </summary>
    IAsyncEnumerable<OpenAiChatChunk> ExecuteOpenAiSseAsync(
        PingRequest request,
        bool hasIdempotencyKey,
        string completionId,
        string model,
        CancellationToken executionToken,
        InferenceAuditContext? auditContext = null);

}
