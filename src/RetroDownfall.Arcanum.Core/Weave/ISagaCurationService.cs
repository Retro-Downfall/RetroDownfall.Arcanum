using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// RAG Phase 4 — the operator-facing port over <see cref="ISagaMemoryStore"/>'s curation primitives
/// (<c>CorrectAsync</c>, <c>RetireAsync</c>, <c>ReinstateAsync</c>, <c>SetPinAsync</c>).
/// </summary>
/// <remarks>
/// Owns three things the store deliberately does not: computing the embedding before any transaction
/// opens (an embedding-provider call made inside one would hold a write lock across the network while
/// it waits on a round trip the store has no way to bound), mapping the store's
/// <see cref="SagaCurationOutcomeKind"/> to typed <see cref="ErrorCodes.Saga"/> codes a caller can act
/// on, and composing the <see cref="SagaMemoryDetail"/> projection every call reads back.
/// </remarks>
public interface ISagaCurationService
{

    /// <summary>
    /// Reads one memory's full detail view: its row, its curation lifecycle, its retrieval eligibility,
    /// and — when the Annals is enabled — the claim that governs it and that claim's version history.
    /// </summary>
    Task<Result<SagaMemoryDetail>> ShowAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces one memory's text. Computes a fresh embedding for <paramref name="content"/> before
    /// anything is written, and refuses with <see cref="ErrorCodes.Saga.EmbeddingUnavailable"/> when the
    /// embedding substrate cannot produce one — a correction that could not re-embed would leave the
    /// stored text saying one thing and the vector saying another.
    /// </summary>
    /// <param name="expectedContentHash">
    /// Uppercase hex of <c>AnnalContentDigest.ForSagaMemory</c> over the content the caller last read —
    /// proof it saw what it is correcting, exactly as the Covenant's own correction takes a rendered
    /// hash on the wire. A malformed hex string fails with <see cref="ErrorCodes.Validation"/>, not a
    /// Saga code.
    /// </param>
    Task<Result<SagaMemoryDetail>> CorrectAsync(
        string id, string expectedContentHash, string content, CancellationToken cancellationToken);

    /// <summary>
    /// Retires one memory. Needs no embedding — retiring only removes a memory's vector, it never
    /// writes one — so it is never refused for the embedding substrate being unavailable.
    /// </summary>
    Task<Result<SagaMemoryDetail>> RetireAsync(
        string id, string expectedContentHash, CancellationToken cancellationToken);

    /// <summary>
    /// Reinstates a retired memory, computing a fresh embedding of its stored content first, exactly as
    /// <see cref="CorrectAsync"/> does.
    /// </summary>
    Task<Result<SagaMemoryDetail>> ReinstateAsync(
        string id, string expectedContentHash, CancellationToken cancellationToken);

    /// <summary>
    /// Marks (or unmarks) one memory as durable. Neither embeds nor takes a content hash, so it is
    /// never refused for the embedding substrate being unavailable — an operator must be able to retire
    /// or pin a memory precisely when retrieval is degraded.
    /// </summary>
    Task<Result<SagaMemoryDetail>> SetPinAsync(string id, bool pinned, CancellationToken cancellationToken);

}
