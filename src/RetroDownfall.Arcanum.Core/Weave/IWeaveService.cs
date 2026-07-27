using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// RAG Phase 1 — The Weave (shared embedding foundation). "The Weave" is Arcanum's embedding and
/// vector substrate: text is imprinted into it as vectors, and Divination (see
/// <see cref="IDivinationService"/>) queries it for semantically relevant knowledge.
///
/// Mirrors the existing <c>IChatClientFactory</c> / <c>IChatClient</c> split in spirit: this is the
/// stable domain contract; the concrete provider wiring (OpenAICompatible, including Ollama via its
/// /v1 endpoint) lives behind <c>IEmbeddingGeneratorFactory</c> in the Api layer and
/// is resolved lazily per call.
///
/// Every method is designed to <b>never throw</b> for expected failure modes (feature disabled,
/// provider unreachable, timeout) — callers get a <see cref="Result{T}"/> and are expected to degrade
/// gracefully (skip Divination, fall back to existing non-RAG behavior) rather than fail the request.
/// </summary>
public interface IWeaveService
{

    /// <summary>
    /// <c>true</c> only when an embedding-backed <c>Arcanum:Features</c> opt-in is enabled and both
    /// <c>Arcanum:Integrations:Embeddings:Provider</c> and
    /// <c>Arcanum:Integrations:Embeddings:Model</c> are configured. Computed fresh on every access
    /// from the current settings snapshot (hot-reload friendly) — callers should check this before
    /// attempting embedding work rather than relying on a previous check's result.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Imprints a single piece of text into a vector. Returns
    /// <see cref="ErrorCodes.Embeddings.FeatureDisabled"/> immediately when <see cref="IsAvailable"/>
    /// is <c>false</c>, or <see cref="ErrorCodes.Embeddings.ProviderUnavailable"/> on provider failure
    /// or timeout. Never throws.
    /// </summary>
    Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken);

    /// <summary>
    /// Imprints many texts in code-owned sequential batches (not in parallel, to avoid overwhelming
    /// local providers). Same error semantics as <see cref="EmbedAsync"/>: never throws, and a
    /// failure on any batch fails the whole call rather than returning partial results.
    /// </summary>
    Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);

    /// <summary>
    /// Splits long text into overlapping chunks using code-owned sizes ahead of imprinting. Pure CPU
    /// work — unlike <see cref="EmbedAsync"/> / <see cref="EmbedBatchAsync"/>, this does not need the
    /// embedding provider and always runs regardless of <see cref="IsAvailable"/>. Phase 1 uses a
    /// naive sliding-window split with no sentence-boundary detection (documented limitation).
    /// </summary>
    Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken);

}
