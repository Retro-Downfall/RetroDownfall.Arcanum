using System.Collections.Concurrent;
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
/// unexpected exception) falls back to <see cref="SpellRoutingDecision.FullGrimoire"/> at Debug log
/// level — the hub always has a working path via the existing LLM router, so a Phase 5 failure can
/// never block spell routing entirely.
/// </summary>
public sealed class SemanticSpellRouter(
    SpellWeaveCache spellWeaveCache,
    IWeaveService weaveService,
    IOptionsSnapshot<ArcanumSettings> settings,
    ILogger<SemanticSpellRouter> logger)
{

    public async Task<SpellRoutingDecision> ResolveAsync(
        IReadOnlyList<SpellMetadata> spells,
        string userPrompt,
        CancellationToken cancellationToken)
    {

        EmbeddingSettings embeddings = settings.Value.ResolveEmbeddings();

        if (!embeddings.Enabled || !embeddings.SemanticSpellRoutingEnabled)
        {

            return SpellRoutingDecision.FullGrimoire();

        }

        if (spells.Count == 0)
        {

            return SpellRoutingDecision.FullGrimoire();

        }

        try
        {

            ConcurrentDictionary<string, Embedding<float>>? spellEmbeddings = await spellWeaveCache
                .GetOrCreateAsync(spells, cancellationToken)
                .ConfigureAwait(false);

            if (spellEmbeddings is null)
            {

                logger.LogDebug("SemanticSpellRouter: spell catalog embeddings unavailable; falling back to LLM-based routing.");

                return SpellRoutingDecision.FullGrimoire();

            }

            if (!weaveService.IsAvailable)
            {

                logger.LogDebug("SemanticSpellRouter: embedding provider unavailable; falling back to LLM-based routing.");

                return SpellRoutingDecision.FullGrimoire();

            }

            Result<Embedding<float>> promptEmbedResult = await weaveService
                .EmbedAsync(userPrompt, cancellationToken)
                .ConfigureAwait(false);

            if (promptEmbedResult.IsFailure)
            {

                logger.LogDebug(
                    "SemanticSpellRouter: failed to embed the user prompt ({Code}); falling back to LLM-based routing.",
                    promptEmbedResult.Error.Code);

                return SpellRoutingDecision.FullGrimoire();

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

                    logger.LogWarning(
                        "SemanticSpellRouter: query embedding dimension ({QueryDimension}) does not match any cached spell embedding — likely a stale cache after an embedding model/provider change; falling back to LLM-based routing.",
                        queryVector.Length);

                }
                else
                {

                    logger.LogDebug("SemanticSpellRouter: no spell embeddings matched the current catalog; falling back to LLM-based routing.");

                }

                return SpellRoutingDecision.FullGrimoire();

            }

            scored.Sort(static (a, b) => b.Similarity.CompareTo(a.Similarity));

            float similarityThreshold = ArcanumSettingClamps.EmbeddingsSimilarityThreshold(embeddings.SimilarityThreshold);

            if (!embeddings.SpellRoutingHybridMode)
            {

                (SpellMetadata Spell, float Similarity) top = scored[0];

                return SpellRoutingDecision.DirectResonance(top.Similarity >= similarityThreshold ? top.Spell : null);

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

                return SpellRoutingDecision.FullGrimoire();

            }

            return SpellRoutingDecision.FilteredDivination(candidates);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogDebug(ex, "SemanticSpellRouter failed unexpectedly; falling back to LLM-based routing.");

            return SpellRoutingDecision.FullGrimoire();

        }

    }

}
