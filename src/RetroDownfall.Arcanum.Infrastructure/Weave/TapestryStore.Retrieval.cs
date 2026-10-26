using System.Data.Common;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

internal sealed partial class TapestryStore
{
    public Task<Result<TapestryGenerationAccess>> GetRetrievableGenerationAsync(TapestryScope scope, TapestryRetrievalIdentity identity, CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(identity);

            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            TapestryGeneration? generation = await ReadCurrentGenerationAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);

            Result<TapestryGenerationAccess> result = generation is not null && MatchesEmbeddingIdentity(generation, identity)
                ? Result<TapestryGenerationAccess>.Success(new(generation, identity))
                : Result<TapestryGenerationAccess>.Failure(new("Tapestry.RebuildRequired", "No current generation proves this query embedding identity."));

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }, cancellationToken);

    private static bool MatchesEmbeddingIdentity(TapestryGeneration generation, TapestryRetrievalIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.EmbeddingIdentity) || identity.EmbeddingDimension < 1
            || generation.EmbeddingDimension != identity.EmbeddingDimension)
        {
            return false;
        }

        if (generation.EmbeddingIdentity is { } recorded)
        {
            return recorded == identity.EmbeddingIdentity;
        }

        // Historical spherical settings included dimensions and the provider/model hash. Recompute
        // that complete proof with the actual query identity; never guess or backfill legacy metadata.
        return generation.AlgorithmVersion == SphericalKMeans.AlgorithmVersion
            && identity.LegacySettings is { } legacy
            && generation.SettingsFingerprint == TapestryHash.OfSettings(legacy.MaxTreeDepth,
                legacy.TargetChildrenPerSummary, legacy.MaxChildrenPerSummary, legacy.MaxClustersPerLayer,
                legacy.MaxSummaryTokens, identity.EmbeddingDimension, identity.EmbeddingIdentity);
    }

    public Task<IReadOnlyList<TapestryRetrievedNode>> HydrateRetrievedNodesAsync(TapestryGenerationAccess access, IReadOnlyList<(string NodeId, float Similarity)> hits, TapestryRetrievalMode mode, CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync<IReadOnlyList<TapestryRetrievedNode>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(access);

            ArgumentNullException.ThrowIfNull(hits);

            if (hits.Count == 0)
            {
                return [];
            }

            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            TapestryGeneration captured = access.Generation;

            TapestryGeneration? generation = await ReadCurrentGenerationAsync(connection, transaction,
                new(captured.ScopeKind, captured.ScopeId), cancellationToken).ConfigureAwait(false);

            if (generation is null || generation.GenerationId != captured.GenerationId || !MatchesEmbeddingIdentity(generation, access.Identity))
            {
                return [];
            }

            Dictionary<string, HydratedNode> hydrated = new(StringComparer.Ordinal);

            LeafCorpus corpus = LeafCorpus.For(generation.ScopeKind);

            foreach (string[] page in hits.Select(static hit => hit.NodeId).Distinct(StringComparer.Ordinal).Chunk(LeafPageSize))
            {
                await using DbCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = $"""
                    SELECT n.NodeId,n.Layer,n.NodeKind,n.SourceLabel,n.ContentHash,n.DescendantLeafCount,n.Content,s.Content
                    FROM tapestry_nodes n
                    LEFT JOIN {corpus.Table} s ON s.{corpus.IdColumn}=n.SourceId AND {corpus.ScopePredicate} AND {NonblankSource}
                    WHERE n.GenerationId=@generation AND n.NodeId IN ({BindIdList(command, page)})
                    """;

                AddParameter(command, "@scopeId", generation.ScopeId);

                AddParameter(command, "@generation", generation.GenerationId);

                await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    bool summary = reader.GetString(2) == nameof(TapestryNodeKind.Summary);

                    int contentColumn = summary ? 6 : 7;

                    if (reader.IsDBNull(contentColumn))
                    {
                        continue;
                    }

                    string content = reader.GetString(contentColumn);

                    string hash = reader.GetString(4);

                    if (TapestryHash.OfContent(content) != hash)
                    {
                        continue;
                    }

                    hydrated.Add(reader.GetString(0), new(reader.GetInt32(1), summary ? TapestryNodeKind.Summary : TapestryNodeKind.Leaf,
                        reader.GetString(3), content, hash, reader.GetInt32(5), null));
                }
            }

            IReadOnlyDictionary<string, IReadOnlyList<string>> ancestors = await ReadClosureAsync(connection, transaction,
                generation.GenerationId, [.. hydrated.Keys], false, cancellationToken).ConfigureAwait(false);

            List<TapestryRetrievedNode> result = [];

            foreach ((string id, float similarity) in hits)
            {
                if (hydrated.TryGetValue(id, out HydratedNode? node))
                {
                    result.Add(new(id, generation.GenerationId, generation.ScopeKind, generation.ScopeId, node.Layer,
                        node.NodeKind, node.SourceLabel, node.Content, node.ContentHash, node.DescendantLeafCount, similarity,
                        mode, ancestors.TryGetValue(id, out IReadOnlyList<string>? lineage) ? lineage : []));
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }, cancellationToken);
}
