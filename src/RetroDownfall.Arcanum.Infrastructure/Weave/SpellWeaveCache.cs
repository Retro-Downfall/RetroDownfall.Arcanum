using System.Collections.Frozen;
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
/// — retaining up to eight least-recently-used catalog snapshots. Re-embedding happens under a
/// semaphore so concurrent first-access callers cannot double-embed the same catalog. A short
/// metadata lock updates recency without holding up cache hits behind provider work.
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

    private const int MaximumCatalogs = 8;

    private readonly object _cacheGate = new();

    private readonly List<CacheSnapshot> _snapshots = [];

    private sealed record CacheSnapshot(
        CatalogKey Key,
        IReadOnlyDictionary<string, Embedding<float>> Cache);

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

    public async Task<IReadOnlyDictionary<string, Embedding<float>>?> GetOrCreateAsync(
        IReadOnlyList<SpellMetadata> metadata,
        CancellationToken cancellationToken,
        Action<string, int>? observeEmbedding = null)
    {
        CatalogKey currentKey = BuildCatalogKey(metadata);

        if (TryGetCached(currentKey) is { } cached)
        {
            return cached;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // The caller's catalog was copied before waiting. Resolve the current provider binding
            // only after acquiring the generation semaphore, then check for a competing refresh.
            currentKey = WithCurrentBinding(currentKey);

            if (TryGetCached(currentKey) is { } refreshed)
            {
                return refreshed;
            }

            if (!weaveService.IsAvailable)
            {
                return null;
            }

            string[] descriptions = new string[currentKey.Spells.Length];

            for (int i = 0; i < currentKey.Spells.Length; i++)
            {
                descriptions[i] = currentKey.Spells[i].Description;
            }

            observeEmbedding?.Invoke("spell-catalog-embedding", descriptions.Length);

            Result<Embedding<float>[]> embedResult = await weaveService
                .EmbedBatchAsync(descriptions, cancellationToken)
                .ConfigureAwait(false);

            if (embedResult.IsFailure)
            {
                logger.LogDebug(
                    "SpellWeaveCache failed to embed the spell catalog ({Reason}); falling back to LLM-based spell routing.",
                    "catalog_embedding_failed");

                return null;
            }

            if (!currentKey.Matches(WithCurrentBinding(currentKey)))
            {
                logger.LogDebug(
                    "SpellWeaveCache failed to refresh the spell catalog ({Reason}); falling back to LLM-based spell routing.",
                    "catalog_binding_changed");

                return null;
            }

            // IWeaveService answers exactly one vector per description or fails. Publish a frozen
            // map so a consumer cannot remove another caller's catalog entries.
            Dictionary<string, Embedding<float>> entries = new(StringComparer.Ordinal);

            for (int i = 0; i < currentKey.Spells.Length; i++)
            {
                entries[currentKey.Spells[i].Name] = embedResult.Value[i];
            }

            FrozenDictionary<string, Embedding<float>> next = entries.ToFrozenDictionary(StringComparer.Ordinal);

            lock (_cacheGate)
            {
                if (_snapshots.Count == MaximumCatalogs) _snapshots.RemoveAt(0);

                _snapshots.Add(new CacheSnapshot(currentKey, next));
            }

            return next;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                "SpellWeaveCache failed to refresh the spell catalog ({Reason}, exception type {ExceptionType}); falling back to LLM-based spell routing.",
                "catalog_refresh_failed",
                ex.GetType().FullName);

            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private IReadOnlyDictionary<string, Embedding<float>>? TryGetCached(CatalogKey key)
    {
        lock (_cacheGate)
        {
            for (int index = 0; index < _snapshots.Count; index++)
            {
                CacheSnapshot snapshot = _snapshots[index];

                if (!key.Matches(snapshot.Key)) continue;

                _snapshots.RemoveAt(index);

                _snapshots.Add(snapshot);

                return snapshot.Cache;
            }
        }

        return null;
    }

    private CatalogKey WithCurrentBinding(CatalogKey key)
    {
        EmbeddingSettings embeddings = optionsMonitor.CurrentValue.ResolveEmbeddings();

        return key with { Provider = embeddings.Provider, Model = embeddings.Model, Dimensions = embeddings.Dimensions };
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
