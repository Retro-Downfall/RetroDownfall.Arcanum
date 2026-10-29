using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// RAG Phase 5 — embedding-based pre-filter for spell routing. Decides, for a given turn, whether the
/// existing LLM-based <see cref="SemanticRouter"/> is needed at all:
///
/// <list type="bullet">
/// <item><b>Disabled</b> (<c>Arcanum:Features:SemanticSpellRouting</c> is <c>false</c>, the default):
/// always <see cref="SpellRoutingDecision.FullGrimoire"/> — the hub calls the unchanged LLM router
/// with the full catalog.</item>
/// <item><b>Pure embedding mode</b> (the current code-owned mode): embeds the user prompt, computes
/// cosine similarity against cached spell description embeddings (see <see cref="SpellWeaveCache"/>),
/// and returns <see cref="SpellRoutingDecision.DirectResonance"/> with the highest-similarity spell
/// above the internal threshold (or <c>null</c> if none) — no LLM call at all.</item>
/// <item><b>Hybrid mode</b> (reserved internal mode): returns a code-owned top-K candidate set via
/// <see cref="SpellRoutingDecision.FilteredDivination"/> for the LLM router to pick from — reduced
/// context, same JSON response protocol and timeout/fallback behavior as today.</item>
/// </list>
///
/// Graceful degradation: any embedding-related failure (cache unavailable, prompt embedding fails,
/// unexpected exception) falls back to <see cref="SpellRoutingDecision.FullGrimoire"/> with one safe
/// Warning per closed reason and monitored configuration epoch across request scopes. The hub
/// retains the existing LLM routing path, so an embedding failure does not block routing.
/// </summary>
public sealed class SemanticSpellRouter(
    SpellWeaveCache spellWeaveCache,
    IWeaveService weaveService,
    IOptionsMonitor<ArcanumSettings> settings,
    ILogger<SemanticSpellRouter> logger)
{
    private static readonly ConditionalWeakTable<ArcanumSettings, WarningEpoch> WarningEpochs = new();

    private enum FallbackReason
    {
        CatalogUnavailable,
        ProviderUnavailable,
        PromptEmbeddingFailed,
        DimensionMismatch,
        CatalogVectorsMissing,
        UnexpectedError,
    }

    private sealed class WarningEpoch
    {
        private int _reasons;

        internal bool TryMark(FallbackReason reason)
        {
            int mask = 1 << (int)reason;

            return (Interlocked.Or(ref _reasons, mask) & mask) == 0;
        }
    }

    private void WarnFallback(ArcanumSettings epoch, FallbackReason reason, Exception? exception = null)
    {
        if (!WarningEpochs.GetValue(epoch, static _ => new WarningEpoch()).TryMark(reason)) return;

        string closedReason = reason switch
        {
            FallbackReason.CatalogUnavailable => "catalog_unavailable",
            FallbackReason.ProviderUnavailable => "provider_unavailable",
            FallbackReason.PromptEmbeddingFailed => "prompt_embedding_failed",
            FallbackReason.DimensionMismatch => "dimension_mismatch",
            FallbackReason.CatalogVectorsMissing => "catalog_vectors_missing",
            FallbackReason.UnexpectedError => "unexpected_error",
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };

        logger.LogWarning(
            "Semantic spell routing fallback ({Reason}, exception type {ExceptionType}); full-catalog LLM routing may add billable provider work. Verify integrations.embeddings.provider, integrations.embeddings.model and integrations.embeddings.dimensions; run 'arcanum doctor'.",
            closedReason,
            exception?.GetType().FullName ?? "none");
    }

    public async Task<SpellRoutingDecision> ResolveAsync(
        IReadOnlyList<SpellMetadata> spells,
        string userPrompt,
        CancellationToken cancellationToken,
        Action<string, int>? observeEmbedding = null) =>
        (await ResolveWithQueryAsync(spells, userPrompt, cancellationToken, observeEmbedding).ConfigureAwait(false)).Decision;

    internal sealed record RoutingResolution(SpellRoutingDecision Decision, QueryEmbeddingHandoff? QueryEmbedding);

    // Transient query data only; no retrieval eligibility or source authority is retained.
    internal sealed record QueryEmbeddingHandoff(
        string Probe,
        Embedding<float> Embedding,
        EmbeddingProvenance Provenance,
        int RequestedDimensions,
        int ObservedDimensions);

    internal bool TryReuseQueryEmbedding(QueryEmbeddingHandoff? handoff, string probe,
        EmbeddingSettings turnSettings, out Embedding<float>? embedding)
    {
        embedding = null;

        if (handoff is null || !string.Equals(handoff.Probe, probe, StringComparison.Ordinal)
            || !MatchesBinding(handoff, turnSettings)
            || !MatchesBinding(handoff, settings.CurrentValue.ResolveEmbeddings())
            || handoff.Embedding.Vector.Length != handoff.ObservedDimensions
            || !IsUsableVector(handoff.Embedding.Vector.Span)
            || !TryReadProvenance(handoff.Embedding, out EmbeddingProvenance? currentProvenance)
            || currentProvenance is null
            || !string.Equals(currentProvenance.Identity, handoff.Provenance.Identity, StringComparison.Ordinal))
        {
            return false;
        }

        embedding = handoff.Embedding;

        return true;
    }

    private static bool MatchesBinding(QueryEmbeddingHandoff handoff, EmbeddingSettings current) =>
        current.Enabled && current.Dimensions == handoff.RequestedDimensions
        && !string.IsNullOrWhiteSpace(current.Provider) && !string.IsNullOrWhiteSpace(current.Model)
        && string.Equals(new EmbeddingProvenance(current.Provider, current.Model).Identity,
            handoff.Provenance.Identity, StringComparison.Ordinal);

    private static bool TryReadProvenance(Embedding<float> embedding, out EmbeddingProvenance? provenance)
    {
        provenance = embedding.AdditionalProperties is { } properties
            && properties.TryGetValue(EmbeddingProvenance.MetadataKey, out object? value)
            ? value as EmbeddingProvenance : null;

        return provenance is not null;
    }

    private static bool IsUsableVector(ReadOnlySpan<float> vector)
    {
        if (vector.IsEmpty) return false;

        foreach (float value in vector)
        {
            if (!float.IsFinite(value)) return false;
        }

        return true;
    }

    internal async Task<RoutingResolution> ResolveWithQueryAsync(
        IReadOnlyList<SpellMetadata> spells,
        string userPrompt,
        CancellationToken cancellationToken,
        Action<string, int>? observeEmbedding = null)
    {
        QueryEmbeddingHandoff? queryHandoff = null;

        RoutingResolution Complete(SpellRoutingDecision decision) => new(decision, queryHandoff);

        ArcanumSettings epoch = settings.CurrentValue;

        EmbeddingSettings embeddings = epoch.ResolveEmbeddings();

        if (!embeddings.Enabled || !embeddings.SemanticSpellRoutingEnabled)
        {
            return Complete(SpellRoutingDecision.FullGrimoire());
        }

        if (spells.Count == 0)
        {
            return Complete(SpellRoutingDecision.FullGrimoire());
        }

        try
        {
            IReadOnlyDictionary<string, Embedding<float>>? spellEmbeddings = await spellWeaveCache
                .GetOrCreateAsync(spells, cancellationToken, observeEmbedding)
                .ConfigureAwait(false);

            if (spellEmbeddings is null)
            {
                WarnFallback(epoch, FallbackReason.CatalogUnavailable);

                return Complete(SpellRoutingDecision.FullGrimoire());
            }

            if (!weaveService.IsAvailable)
            {
                WarnFallback(epoch, FallbackReason.ProviderUnavailable);

                return Complete(SpellRoutingDecision.FullGrimoire());
            }

            observeEmbedding?.Invoke("spell-routing-embedding", 1);

            Result<Embedding<float>> promptEmbedResult = await weaveService
                .EmbedAsync(userPrompt, cancellationToken)
                .ConfigureAwait(false);

            if (promptEmbedResult.IsFailure)
            {
                WarnFallback(epoch, FallbackReason.PromptEmbeddingFailed);

                return Complete(SpellRoutingDecision.FullGrimoire());
            }

            if (TryReadProvenance(promptEmbedResult.Value, out EmbeddingProvenance? provenance)
                && provenance is not null
                && !string.IsNullOrWhiteSpace(embeddings.Provider) && !string.IsNullOrWhiteSpace(embeddings.Model)
                && string.Equals(provenance.Identity,
                    new EmbeddingProvenance(embeddings.Provider, embeddings.Model).Identity, StringComparison.Ordinal)
                && IsUsableVector(promptEmbedResult.Value.Vector.Span))
            {
                queryHandoff = new(userPrompt, promptEmbedResult.Value, provenance,
                    embeddings.Dimensions, promptEmbedResult.Value.Vector.Length);
            }

            ReadOnlySpan<float> queryVector = promptEmbedResult.Value.Vector.Span;

            List<(SpellMetadata Spell, float Similarity)> scored = new(spells.Count);

            bool dimensionMismatchDetected = false;

            foreach (SpellMetadata spell in spells)
            {
                if (spellEmbeddings.TryGetValue(spell.Name, out Embedding<float>? spellEmbedding))
                {
                    // EmbeddingBlobCodec.CosineSimilarity returns 0 for a length mismatch, which is
                    // indistinguishable from a genuine "no signal" match if scored naively — skip
                    // mismatched entries here instead, so a stale cache (e.g. after an embedding
                    // model/provider change) cannot masquerade as a confident "no match" decision.
                    if (spellEmbedding.Vector.Length != queryVector.Length)
                    {
                        dimensionMismatchDetected = true;

                        continue;
                    }

                    float similarity = EmbeddingBlobCodec.CosineSimilarity(queryVector, spellEmbedding.Vector.Span);

                    scored.Add((spell, similarity));
                }
            }

            if (scored.Count == 0)
            {
                if (dimensionMismatchDetected)
                {
                    WarnFallback(epoch, FallbackReason.DimensionMismatch);
                }
                else
                {
                    WarnFallback(epoch, FallbackReason.CatalogVectorsMissing);
                }

                return Complete(SpellRoutingDecision.FullGrimoire());
            }

            scored.Sort(static (a, b) => b.Similarity.CompareTo(a.Similarity));

            float similarityThreshold = ArcanumSettingClamps.EmbeddingsSimilarityThreshold(embeddings.SimilarityThreshold);

            if (!embeddings.SpellRoutingHybridMode)
            {
                (SpellMetadata Spell, float Similarity) top = scored[0];

                return Complete(SpellRoutingDecision.DirectResonance(top.Similarity >= similarityThreshold ? top.Spell : null));
            }

            int topK = ArcanumSettingClamps.EmbeddingsSpellRoutingHybridTopK(embeddings.SpellRoutingHybridTopK);

            List<SpellMetadata> candidates = new(Math.Min(topK, scored.Count));

            for (int i = 0; i < scored.Count && i < topK; i++)
            {
                // scored is sorted descending by similarity, so once one candidate falls below the
                // threshold, every subsequent one does too — a hard stop, not just a skip. Without
                // this, the LLM router could be handed irrelevant candidates (or all-zero-similarity
                // candidates from a broken embedding space) purely by rank, regardless of whether any
                // of them actually resemble the prompt.
                if (scored[i].Similarity < similarityThreshold)
                {
                    break;
                }

                candidates.Add(scored[i].Spell);
            }

            if (candidates.Count == 0)
            {
                logger.LogDebug("SemanticSpellRouter: no candidate cleared the similarity threshold in hybrid mode; falling back to full-catalog LLM routing.");

                return Complete(SpellRoutingDecision.FullGrimoire());
            }

            return Complete(SpellRoutingDecision.FilteredDivination(candidates));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WarnFallback(epoch, FallbackReason.UnexpectedError, ex);

            return Complete(SpellRoutingDecision.FullGrimoire());
        }
    }
}
