using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// RAG Phase 5 — caches spell description imprints ("The Weave") for embedding-based spell routing
/// (see <c>SemanticSpellRouter</c>). Named <c>SpellWeaveCache</c> rather than <c>SpellEmbeddingCache</c>
/// to keep the naming metaphor consistent with the rest of The Weave.
///
/// Re-embeds the full spell catalog when the catalog changes (a spell added, removed, or its
/// description edited) or when the embedding provider/model/dimensions change (an operator hot-reload)
/// — comparing against the last-cached snapshot. Re-embedding happens under a lock so concurrent
/// first-access callers cannot double-embed the same catalog.
///
/// Falls back to <c>null</c> (never throws) when The Weave is unavailable or the embedding call fails —
/// <c>SemanticSpellRouter</c> treats that as "fall back to LLM-based routing", never a functional
/// regression.
/// </summary>
public sealed class SpellWeaveCache(
    IWeaveService weaveService,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ILogger<SpellWeaveCache> logger) : IDisposable
{

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Published as a single immutable snapshot (rather than separate <c>_cache</c>/<c>_cachedMetadata</c>
    /// fields) so the lock-free fast-path read below can never observe a torn combination — vectors
    /// from one refresh paired with metadata from another.
    /// </summary>
    private volatile CacheSnapshot? _snapshot;

    private sealed record CacheSnapshot(
        CatalogKey Key,
        ConcurrentDictionary<string, Embedding<float>> Cache);

    private sealed record CatalogKey(
        (string Name, string Description)[] Spells,
        string? Provider,
        string? Model,
        int Dimensions)
    {

        public bool Matches(CatalogKey other)
        {

            if (!string.Equals(Provider, other.Provider, StringComparison.Ordinal)
                || !string.Equals(Model, other.Model, StringComparison.Ordinal)
                || Dimensions != other.Dimensions
                || Spells.Length != other.Spells.Length)
            {

                return false;

            }

            for (int i = 0; i < Spells.Length; i++)
            {

                if (!string.Equals(Spells[i].Name, other.Spells[i].Name, StringComparison.Ordinal)
                    || !string.Equals(Spells[i].Description, other.Spells[i].Description, StringComparison.Ordinal))
                {

                    return false;

                }

            }

            return true;

        }

    }

    public async Task<ConcurrentDictionary<string, Embedding<float>>?> GetOrCreateAsync(
        IReadOnlyList<SpellMetadata> metadata,
        CancellationToken cancellationToken)
    {

        CatalogKey currentKey = BuildCatalogKey(metadata);

        CacheSnapshot? snapshot = _snapshot;

        if (snapshot is not null && currentKey.Matches(snapshot.Key))
        {

            return snapshot.Cache;

        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            // Re-check after acquiring the lock: another caller may already have refreshed the cache
            // while this call was waiting, in which case there is nothing left to do.
            snapshot = _snapshot;

            if (snapshot is not null && currentKey.Matches(snapshot.Key))
            {

                return snapshot.Cache;

            }

            if (!weaveService.IsAvailable)
            {

                return null;

            }

            string[] descriptions = new string[metadata.Count];

            for (int i = 0; i < metadata.Count; i++)
            {

                descriptions[i] = metadata[i].Description;

            }

            Result<Embedding<float>[]> embedResult = await weaveService
                .EmbedBatchAsync(descriptions, cancellationToken)
                .ConfigureAwait(false);

            if (embedResult.IsFailure)
            {

                logger.LogDebug(
                    "SpellWeaveCache failed to embed the spell catalog ({Code}); falling back to LLM-based spell routing.",
                    embedResult.Error.Code);

                return null;

            }

            if (embedResult.Value.Length != metadata.Count)
            {

                // A provider returning fewer/more vectors than requested is a shape mismatch, not a
                // benign edge case — indexing embedResult.Value[i] against metadata[i] positionally
                // would either throw (caught below, opaque to operators) or silently pair the wrong
                // spell with the wrong vector for every index past the shorter length.
                logger.LogWarning(
                    "SpellWeaveCache: embedding provider returned {ActualCount} vector(s) for {ExpectedCount} spell description(s); falling back to LLM-based spell routing.",
                    embedResult.Value.Length,
                    metadata.Count);

                return null;

            }

            ConcurrentDictionary<string, Embedding<float>> next = new(StringComparer.Ordinal);

            for (int i = 0; i < metadata.Count; i++)
            {

                next[metadata[i].Name] = embedResult.Value[i];

            }

            _snapshot = new CacheSnapshot(currentKey, next);

            return next;

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogDebug(ex, "SpellWeaveCache failed to refresh the spell catalog; falling back to LLM-based spell routing.");

            return null;

        }
        finally
        {

            _lock.Release();

        }

    }

    private CatalogKey BuildCatalogKey(IReadOnlyList<SpellMetadata> metadata)
    {

        var snapshot = new (string Name, string Description)[metadata.Count];

        for (int i = 0; i < metadata.Count; i++)
        {

            snapshot[i] = (metadata[i].Name, metadata[i].Description);

        }

        EmbeddingSettings embeddings = optionsMonitor.CurrentValue.ResolveEmbeddings();

        return new CatalogKey(snapshot, embeddings.Provider, embeddings.Model, embeddings.Dimensions);

    }

    public void Dispose()
    {

        _lock.Dispose();

    }

}
