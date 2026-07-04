using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Api.Intelligence;

// RAG Phase 1 — The Weave (imprinting). Layering note: the plan places this implementation in
// Infrastructure (mirroring EmbeddingService/DivinationService both being Infrastructure concerns), but
// WeaveService depends on IEmbeddingGeneratorFactory, whose concrete provider wiring uses AI SDK types
// (OpenAI.Embeddings.EmbeddingClient, Microsoft.Extensions.AI.OpenAI) that are referenced only by the
// Api csproj — RetroDownfall.Arcanum.Infrastructure.csproj has no ProjectReference to Api, so
// Infrastructure genuinely cannot see those types. This lives in Api instead, exactly mirroring why
// ChatClientFactory (the equivalent composition root for chat completions) also lives in Api rather
// than Infrastructure. This placement is invisible to every consumer: IWeaveService (the Core contract)
// is registered once in AddArcanumApiServices, and Infrastructure-layer code (including later phases'
// background services, e.g. Phase 2's EntryWeavingService) depends only on IWeaveService — DI resolves
// this Api-layer class at runtime regardless of which project registered it.
//
// IDivinationService/DivinationService, by contrast, has no dependency on the embedding generator (only
// on ArcanumDbContext raw SQL and WeaveIndexAvailability) and lives in Infrastructure exactly as
// planned — see DivinationService.cs.
/// <summary>
/// Imprints text into The Weave via <see cref="IEmbeddingGeneratorFactory"/>. Never throws for expected
/// failure modes (feature disabled, provider unreachable, timeout, or genuine per-call provider error):
/// callers receive a <see cref="Result{T}"/> and are expected to degrade gracefully. A caller-initiated
/// cancellation (the supplied <see cref="CancellationToken"/> itself firing) still propagates as
/// <see cref="OperationCanceledException"/>, per standard .NET convention — only internal
/// provider/timeout failures are translated into a failed <see cref="Result{T}"/>.
/// </summary>
public sealed class WeaveService(
    IEmbeddingGeneratorFactory generatorFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ILogger<WeaveService> logger) : IWeaveService
{

    /// <summary>
    /// Computed fresh on every access from <see cref="IOptionsMonitor{ArcanumSettings}.CurrentValue"/> —
    /// no <c>OnChange</c> registration (avoids leak/fragility risk); cheap, since the monitor holds a
    /// cached snapshot, and hot-reload friendly, since the next access always sees current config. Same
    /// singleton pattern as <c>McpConnectionManager</c> / <c>EyeOfTheWorldService</c>.
    /// </summary>
    public bool IsAvailable
    {
        get
        {

            EmbeddingSettings embeddings = optionsMonitor.CurrentValue.Embeddings ?? new EmbeddingSettings();

            return embeddings.Enabled
                && !string.IsNullOrWhiteSpace(embeddings.Provider)
                && !string.IsNullOrWhiteSpace(embeddings.Model);

        }
    }

    public async Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
    {

        Result<Embedding<float>[]> batchResult = await EmbedBatchAsync([text], cancellationToken).ConfigureAwait(false);

        if (batchResult.IsFailure)
        {
            return Result<Embedding<float>>.Failure(batchResult.Error);

        }

        return Result<Embedding<float>>.Success(batchResult.Value[0]);

    }

    public async Task<Result<Embedding<float>[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {

        // Disabled-path (no generator resolution, no HTTP call, no exception): checked first, before
        // anything else in this method runs.
        if (!IsAvailable)
        {

            return Result<Embedding<float>[]>.Failure(new Error(
                ErrorCodes.Embeddings.FeatureDisabled,
                "Embeddings are disabled (Arcanum:Embeddings:Enabled is false, or Provider/Model is not configured)."));

        }

        if (texts.Count == 0)
        {
            return Result<Embedding<float>[]>.Success([]);

        }

        EmbeddingSettings embeddings = optionsMonitor.CurrentValue.Embeddings ?? new EmbeddingSettings();

        int batchSize = ArcanumSettingClamps.EmbeddingsBatchSize(embeddings.BatchSize);

        int requestTimeoutSeconds = ArcanumSettingClamps.EmbeddingsRequestTimeoutSeconds(embeddings.RequestTimeoutSeconds);

        List<Embedding<float>> results = new(texts.Count);

        try
        {

            // Sequential, not parallel — avoids overwhelming local providers (Ollama/LlamaCppServer).
            for (int offset = 0; offset < texts.Count; offset += batchSize)
            {

                int count = Math.Min(batchSize, texts.Count - offset);

                List<string> batch = new(count);

                for (int i = 0; i < count; i++)
                {
                    batch.Add(texts[offset + i]);

                }

                Embedding<float>[] batchEmbeddings = await EmbedOneBatchAsync(
                    batch,
                    requestTimeoutSeconds,
                    cancellationToken).ConfigureAwait(false);

                results.AddRange(batchEmbeddings);

            }

            return Result<Embedding<float>[]>.Success([.. results]);

        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {

            // The linked timeout CTS fired, not the caller's own token — this is an internal provider
            // timeout, translated into a sanitized failure rather than propagated as a cancellation.
            logger.LogWarning(
                "Embedding request timed out after {TimeoutSeconds}s (Arcanum:Embeddings:RequestTimeoutSeconds).",
                requestTimeoutSeconds);

            return Result<Embedding<float>[]>.Failure(new Error(
                ErrorCodes.Embeddings.ProviderUnavailable,
                "The embedding provider timed out."));

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(ex, "Embedding provider call failed; The Weave will be treated as unavailable for this request.");

            return Result<Embedding<float>[]>.Failure(new Error(
                ErrorCodes.Embeddings.ProviderUnavailable,
                "The embedding provider is unavailable. See server logs for detail."));

        }

    }

    private async Task<Embedding<float>[]> EmbedOneBatchAsync(
        List<string> batch,
        int requestTimeoutSeconds,
        CancellationToken cancellationToken)
    {

        // The linked CTS is the guaranteed timeout enforcement mechanism regardless of provider —
        // provider-native timeout configuration (where the SDK exposes one) is applied as
        // defense-in-depth inside EmbeddingGeneratorFactory's HttpClient wiring, not relied upon alone.
        using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(requestTimeoutSeconds));

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        using EmbeddingGeneratorLease lease = await generatorFactory
            .ResolveGeneratorAsync(linkedCts.Token)
            .ConfigureAwait(false);

        GeneratedEmbeddings<Embedding<float>> generated = await lease.Generator
            .GenerateAsync(batch, options: null, linkedCts.Token)
            .ConfigureAwait(false);

        Embedding<float>[] embeddings = new Embedding<float>[generated.Count];

        for (int i = 0; i < generated.Count; i++)
        {
            embeddings[i] = generated[i];

        }

        return embeddings;

    }

    public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken)
    {

        // Pure CPU — always runs regardless of IsAvailable (no generator involved).
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(Result<(string Chunk, int Offset)[]>.Success([]));

        }

        EmbeddingSettings embeddings = optionsMonitor.CurrentValue.Embeddings ?? new EmbeddingSettings();

        int chunkSizeChars = ArcanumSettingClamps.EmbeddingsChunkSizeChars(embeddings.ChunkSizeChars);

        int chunkOverlapChars = ArcanumSettingClamps.EmbeddingsChunkOverlapChars(embeddings.ChunkOverlapChars);

        // Phase 1 documented limitation: a naive sliding window with no sentence/word-boundary
        // detection. A chunk boundary can fall mid-word; acceptable for Phase 1's retrieval quality
        // bar and revisited only if a later phase needs it (see DESIGN.md §21).
        int step = Math.Max(1, chunkSizeChars - chunkOverlapChars);

        List<(string Chunk, int Offset)> chunks = [];

        for (int offset = 0; offset < text.Length; offset += step)
        {

            int length = Math.Min(chunkSizeChars, text.Length - offset);

            chunks.Add((text.Substring(offset, length), offset));

            if (offset + length >= text.Length)
            {
                break;

            }

        }

        return Task.FromResult(Result<(string Chunk, int Offset)[]>.Success([.. chunks]));

    }

}
