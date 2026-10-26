using System.Data.Common;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

internal sealed partial class TapestryStore
{
    private static Error RetrievalChanged => new("Tapestry.SourceChanged", "The captured Tapestry generation is no longer current.");

    private static async Task<bool> IsRetrievalAccessCurrentAsync(DbConnection connection, DbTransaction transaction, TapestryGenerationAccess access, CancellationToken cancellationToken)
    {
        TapestryGeneration captured = access.Generation;

        TapestryGeneration? current = await ReadCurrentGenerationAsync(connection, transaction, new(captured.ScopeKind, captured.ScopeId), cancellationToken).ConfigureAwait(false);

        return current?.GenerationId == captured.GenerationId && MatchesEmbeddingIdentity(current, access.Identity);
    }

    public Task<Result<TapestryRetrievalGraph>> ReadRetrievalGraphAsync(TapestryGenerationAccess access, CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync(async () =>
        {
            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            if (!await IsRetrievalAccessCurrentAsync(connection, transaction, access, cancellationToken).ConfigureAwait(false))
            {
                return Result<TapestryRetrievalGraph>.Failure(RetrievalChanged);
            }

            TapestryGeneration generation = access.Generation;

            LeafCorpus corpus = LeafCorpus.For(generation.ScopeKind);

            string timestamp = generation.ScopeKind switch
            {
                TapestryScopeKind.Workspace => "s.FileLastWriteTime",
                TapestryScopeKind.Session => "s.CreatedAt",
                TapestryScopeKind.SessionAttachment => "CASE WHEN a.SourceKind='WorkspaceFile' AND a.SourceStatus='Refreshable' AND a.SourceContentSha256=a.ContentSha256 AND a.SourceWorkspaceIdentity IS NOT NULL AND a.SourceRelativePath IS NOT NULL THEN a.SourceLastWriteAt END",
                _ => throw new ArgumentOutOfRangeException(nameof(access)),
            };

            string attachmentJoin = generation.ScopeKind == TapestryScopeKind.SessionAttachment
                ? "LEFT JOIN SessionAttachments a ON a.Id=s.AttachmentId AND a.State='Bound'"
                : string.Empty;

            Dictionary<string, (int Layer, DateTimeOffset? Time, List<string> Children, long Bytes)> vertices = new(StringComparer.Ordinal);

            string after = string.Empty;

            while (true)
            {
                int count = 0;

                await using DbCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = $"""
                    SELECT n.NodeId,n.Layer,{timestamp},length(CAST(CASE WHEN n.NodeKind='Summary' THEN n.Content ELSE s.Content END AS BLOB))+length(CAST(n.SourceLabel AS BLOB))
                    FROM tapestry_nodes n
                    LEFT JOIN {corpus.Table} s ON n.NodeKind='Leaf' AND s.{corpus.IdColumn}=n.SourceId AND {corpus.ScopePredicate} AND {NonblankSource}
                    {attachmentJoin}
                    WHERE n.GenerationId=@generation AND n.NodeId COLLATE BINARY>@after
                    ORDER BY n.NodeId COLLATE BINARY LIMIT @page
                    """;

                AddParameter(command, "@generation", generation.GenerationId);

                AddParameter(command, "@scopeId", generation.ScopeId);

                AddParameter(command, "@after", after);

                AddParameter(command, "@page", LeafPageSize);

                await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    after = reader.GetString(0);

                    DateTimeOffset? time = !reader.IsDBNull(2) && UtcInstantText.TryParse(reader.GetString(2), out DateTimeOffset parsed) ? parsed : null;

                    vertices.Add(after, (reader.GetInt32(1), time, [], reader.IsDBNull(3) ? 0 : reader.GetInt64(3)));

                    count++;
                }

                if (count < LeafPageSize)
                {
                    break;
                }
            }

            await using (DbCommand edges = connection.CreateCommand())
            {
                edges.Transaction = transaction;

                edges.CommandText = "SELECT ParentNodeId,ChildNodeId FROM tapestry_edges WHERE GenerationId=@generation ORDER BY ParentNodeId,ChildNodeId";

                AddParameter(edges, "@generation", generation.GenerationId);

                await using DbDataReader reader = await edges.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!vertices.TryGetValue(reader.GetString(0), out var parent)
                        || !vertices.TryGetValue(reader.GetString(1), out var child) || child.Layer >= parent.Layer)
                    {
                        return Result<TapestryRetrievalGraph>.Failure(RetrievalChanged);
                    }

                    parent.Children.Add(reader.GetString(1));
                }
            }

            Dictionary<string, TapestryRetrievalVertex> graph = new(StringComparer.Ordinal);

            foreach (var (id, vertex) in vertices.OrderBy(static item => item.Value.Layer).ThenBy(static item => item.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();

                DateTimeOffset? time = vertex.Time;

                if (vertex.Children.Count != 0)
                {
                    // All-known plus max is idempotent across shared paths: no all-pairs closure or path expansion.
                    time = vertex.Children.All(child => graph[child].SourceEventAt.HasValue)
                        ? vertex.Children.Max(child => graph[child].SourceEventAt)
                        : null;
                }

                graph.Add(id, new(vertex.Layer, time, vertex.Children, vertex.Bytes));
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result<TapestryRetrievalGraph>.Success(new(graph));
        }, cancellationToken);

    public Task<Result<TapestryRankedPage>> RankRetrievalNodesAsync(TapestryGenerationAccess access, float[] queryVector,
        TapestryRetrievalGraph graph, IReadOnlyList<string>? nodeIds, int limit, CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync(async () =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

            if (!ValidVector(queryVector, access.Identity.EmbeddingDimension))
            {
                return Result<TapestryRankedPage>.Failure(new("Tapestry.InvalidQuery", "The query vector does not match its embedding identity."));
            }

            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            if (!await IsRetrievalAccessCurrentAsync(connection, transaction, access, cancellationToken).ConfigureAwait(false))
            {
                return Result<TapestryRankedPage>.Failure(RetrievalChanged);
            }

            HashSet<string>? eligible = nodeIds is null ? null : new(nodeIds, StringComparer.Ordinal);

            PriorityQueue<TapestryRankedNode, TapestryRankedNode> best = new(Comparer<TapestryRankedNode>.Create(static (a, b) => TapestrySelectionPolicy.Compare(b, a)));

            double norm = EmbeddingBlobCodec.NormSquared(queryVector);

            int scored = 0;

            string after = string.Empty;

            while (true)
            {
                int count = 0;

                await using DbCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = """
                    SELECT n.NodeId,CASE WHEN length(e.Embedding)=@bytes THEN e.Embedding END
                    FROM tapestry_nodes n JOIN tapestry_node_embeddings e ON e.NodeId=n.NodeId
                    WHERE n.GenerationId=@generation AND n.NodeId COLLATE BINARY>@after
                    ORDER BY n.NodeId COLLATE BINARY LIMIT @page
                    """;

                AddParameter(command, "@generation", access.Generation.GenerationId);

                AddParameter(command, "@bytes", checked(queryVector.Length * sizeof(float)));

                AddParameter(command, "@after", after);

                AddParameter(command, "@page", LeafPageSize);

                await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    after = reader.GetString(0);

                    count++;

                    if ((eligible is not null && !eligible.Contains(after)) || reader.IsDBNull(1)
                        || !graph.Vertices.TryGetValue(after, out TapestryRetrievalVertex? vertex))
                    {
                        continue;
                    }

                    byte[] blob = (byte[])reader.GetValue(1);

                    if (!EmbeddingBlobCodec.TryCosineSimilarity(queryVector, norm, EmbeddingBlobCodec.AsVector(blob), out float similarity))
                    {
                        continue;
                    }

                    scored++;

                    TapestryRankedNode candidate = new(after, similarity, vertex.SourceEventAt);

                    if (best.Count < limit)
                    {
                        best.Enqueue(candidate, candidate);
                    }
                    else
                    {
                        _ = best.EnqueueDequeue(candidate, candidate);
                    }
                }

                if (count < LeafPageSize)
                {
                    break;
                }
            }

            TapestryRankedNode[] nodes = [.. best.UnorderedItems.Select(static item => item.Element).Order(Comparer<TapestryRankedNode>.Create(TapestrySelectionPolicy.Compare))];

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result<TapestryRankedPage>.Success(new(nodes, scored, scored > limit));
        }, cancellationToken);

    public Task<Result<IReadOnlyDictionary<string, TapestrySelectionMetadata>>> ReadSelectionMetadataAsync(TapestryGenerationAccess access,
        IReadOnlyList<string> nodeIds, CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync(async () =>
        {
            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            if (!await IsRetrievalAccessCurrentAsync(connection, transaction, access, cancellationToken).ConfigureAwait(false))
            {
                return Result<IReadOnlyDictionary<string, TapestrySelectionMetadata>>.Failure(RetrievalChanged);
            }

            var ancestors = await ReadClosureAsync(connection, transaction, access.Generation.GenerationId, nodeIds, false, cancellationToken).ConfigureAwait(false);

            var leaves = await ReadClosureAsync(connection, transaction, access.Generation.GenerationId, nodeIds, true, cancellationToken).ConfigureAwait(false);

            IReadOnlyDictionary<string, TapestrySelectionMetadata> result = nodeIds.Distinct(StringComparer.Ordinal).ToDictionary(static id => id,
                id => new TapestrySelectionMetadata(ancestors[id], leaves[id]), StringComparer.Ordinal);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result<IReadOnlyDictionary<string, TapestrySelectionMetadata>>.Success(result);
        }, cancellationToken);
}
