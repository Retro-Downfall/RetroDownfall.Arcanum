using System.Data.Common;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

internal sealed partial class TapestryStore
{
    public Task AppendGraphCheckpointAsync(TapestryGraphCheckpoint checkpoint, CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(checkpoint);

            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            TapestryGeneration? generation = await ReadGraphGenerationAsync(connection, transaction, checkpoint.GenerationId, cancellationToken).ConfigureAwait(false);

            if (generation is null || generation.Status != TapestryGenerationStatus.Building
                || (checkpoint.LegacySingleParent && generation.AlgorithmVersion != SphericalKMeans.AlgorithmVersion))
            {
                throw new InvalidOperationException("Graph checkpoints require a Building generation and compatible topology.");
            }

            (long corpus, long invalidation) = await ReadRevisionsAsync(connection, transaction, new(generation.ScopeKind, generation.ScopeId), cancellationToken).ConfigureAwait(false);

            if (generation.SourceRevision != corpus || generation.ValidatedInvalidationRevision != invalidation)
            {
                throw new InvalidOperationException("Tapestry source snapshot changed before checkpoint.");
            }

            foreach (TapestryNodeWrite write in checkpoint.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TapestryNode node = write.Node;

                if (node.GenerationId != generation.GenerationId || node.ScopeKind != generation.ScopeKind
                    || node.ScopeId != generation.ScopeId || node.ParentNodeId is not null
                    || node.EmbeddingDimension != generation.EmbeddingDimension || !ValidVector(write.Embedding, generation.EmbeddingDimension)
                    || (node.NodeKind == TapestryNodeKind.Summary
                        && (string.IsNullOrWhiteSpace(node.Content) || TapestryHash.OfContent(node.Content) != node.ContentHash)))
                {
                    throw new ArgumentException("Graph checkpoint contains an invalid node or original-dimensional vector.");
                }

                await InsertNodeAsync(connection, transaction, write, cancellationToken).ConfigureAwait(false);
            }

            foreach (TapestryGraphEdge edge in checkpoint.Edges)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!ValidPosterior(edge.GlobalPosterior) || !ValidPosterior(edge.LocalPosterior) || string.IsNullOrWhiteSpace(edge.MembershipReason))
                {
                    throw new ArgumentException("Graph membership metadata is invalid.");
                }

                await using DbCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = """
                    INSERT INTO tapestry_edges(GenerationId,ParentNodeId,ChildNodeId,GlobalPosterior,LocalPosterior,MembershipReason)
                    VALUES(@generation,@parent,@child,@global,@local,@reason)
                    """;

                AddParameter(command, "@generation", checkpoint.GenerationId);

                AddParameter(command, "@parent", edge.ParentNodeId);

                AddParameter(command, "@child", edge.ChildNodeId);

                AddParameter(command, "@global", (object?)edge.GlobalPosterior ?? DBNull.Value);

                AddParameter(command, "@local", (object?)edge.LocalPosterior ?? DBNull.Value);

                AddParameter(command, "@reason", edge.MembershipReason);

                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (checkpoint.LegacySingleParent)
                {
                    await using DbCommand legacy = connection.CreateCommand();

                    legacy.Transaction = transaction;

                    legacy.CommandText = "UPDATE tapestry_nodes SET ParentNodeId=@parent,ParentScopeKey=@key WHERE GenerationId=@generation AND NodeId=@child";

                    AddParameter(legacy, "@parent", edge.ParentNodeId);

                    AddParameter(legacy, "@key", ParentScopeKey(checkpoint.GenerationId, edge.ParentNodeId));

                    AddParameter(legacy, "@generation", checkpoint.GenerationId);

                    AddParameter(legacy, "@child", edge.ChildNodeId);

                    _ = await legacy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            string[] parents = [.. checkpoint.Edges.Select(static edge => edge.ParentNodeId).Distinct(StringComparer.Ordinal)];

            IReadOnlyDictionary<string, IReadOnlyList<string>> ancestors = await ReadClosureAsync(connection, transaction, checkpoint.GenerationId, parents, false, cancellationToken).ConfigureAwait(false);

            HashSet<string> affected = new(parents, StringComparer.Ordinal);

            foreach (IReadOnlyList<string> chain in ancestors.Values)
            {
                affected.UnionWith(chain);
            }

            foreach (string id in affected)
            {
                await using DbCommand count = connection.CreateCommand();

                count.Transaction = transaction;

                count.CommandText = """
                    WITH RECURSIVE reachable(NodeId) AS (
                        SELECT @id UNION SELECT e.ChildNodeId FROM tapestry_edges e
                        JOIN reachable r ON e.ParentNodeId=r.NodeId WHERE e.GenerationId=@generation)
                    UPDATE tapestry_nodes SET DescendantLeafCount=(
                        SELECT COUNT(DISTINCT n.SourceId) FROM reachable r JOIN tapestry_nodes n ON n.NodeId=r.NodeId
                        WHERE n.GenerationId=@generation AND n.NodeKind='Leaf')
                    WHERE GenerationId=@generation AND NodeId=@id
                    """;

                AddParameter(count, "@id", id);

                AddParameter(count, "@generation", checkpoint.GenerationId);

                _ = await count.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static bool ValidPosterior(double? value) => value is null || (double.IsFinite(value.Value) && value.Value is >= 0 and <= 1);

    private static bool ValidVector(float[] vector, int dimensions)
    {
        if (vector.Length != dimensions)
        {
            return false;
        }

        double norm = 0;

        foreach (float value in vector)
        {
            if (!float.IsFinite(value))
            {
                return false;
            }

            norm += (double)value * value;
        }

        return norm > 0 && double.IsFinite(norm);
    }

    public async Task<IReadOnlyList<string>> GetRootIdsAsync(string generationId, CancellationToken cancellationToken)
    {
        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT n.NodeId FROM tapestry_nodes n WHERE n.GenerationId=@generation
            AND NOT EXISTS (SELECT 1 FROM tapestry_edges e WHERE e.GenerationId=n.GenerationId AND e.ChildNodeId=n.NodeId)
            ORDER BY n.NodeId
            """;

        AddParameter(command, "@generation", generationId);

        List<string> roots = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            roots.Add(reader.GetString(0));
        }

        return roots;
    }

    public async Task<IReadOnlyList<string>> GetChildIdsAsync(string generationId, IReadOnlyList<string> parentIds, CancellationToken cancellationToken)
    {
        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        HashSet<string> children = new(StringComparer.Ordinal);

        foreach (string[] page in parentIds.Distinct(StringComparer.Ordinal).Chunk(LeafPageSize))
        {
            await using DbCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = $"SELECT DISTINCT ChildNodeId FROM tapestry_edges WHERE GenerationId=@generation AND ParentNodeId IN ({BindIdList(command, page)})";

            AddParameter(command, "@generation", generationId);

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                children.Add(reader.GetString(0));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return [.. children.Order(StringComparer.Ordinal)];
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAncestorIdsAsync(string generationId, IReadOnlyList<string> nodeIds, CancellationToken cancellationToken) =>
        ReadClosureAsync(generationId, nodeIds, false, cancellationToken);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetDescendantLeafIdsAsync(string generationId, IReadOnlyList<string> nodeIds, CancellationToken cancellationToken) =>
        ReadClosureAsync(generationId, nodeIds, true, cancellationToken);

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadClosureAsync(string generationId, IReadOnlyList<string> nodeIds, bool leaves, CancellationToken cancellationToken)
    {
        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, IReadOnlyList<string>> result = await ReadClosureAsync(connection, transaction, generationId, nodeIds, leaves, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadClosureAsync(DbConnection connection, DbTransaction transaction, string generationId, IReadOnlyList<string> nodeIds, bool leaves, CancellationToken cancellationToken)
    {
        Dictionary<string, IReadOnlyList<string>> result = new(StringComparer.Ordinal);

        foreach (string[] page in nodeIds.Distinct(StringComparer.Ordinal).Chunk(LeafPageSize))
        {
            Dictionary<string, List<string>> collected = page.ToDictionary(static id => id, static _ => new List<string>(), StringComparer.Ordinal);

            await using DbCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            string join = leaves ? "e.ParentNodeId=r.NodeId" : "e.ChildNodeId=r.NodeId";

            string next = leaves ? "e.ChildNodeId" : "e.ParentNodeId";

            string output = leaves
                ? "SELECT DISTINCT r.Origin,n.SourceId FROM reach r JOIN tapestry_nodes n ON n.NodeId=r.NodeId WHERE n.GenerationId=@generation AND n.NodeKind='Leaf' ORDER BY r.Origin,n.SourceId"
                : "SELECT Origin,NodeId FROM reach WHERE Origin<>NodeId ORDER BY Origin,NodeId";

            command.CommandText = $"""
                WITH RECURSIVE reach(Origin,NodeId) AS (
                    SELECT NodeId,NodeId FROM tapestry_nodes WHERE GenerationId=@generation AND NodeId IN ({BindIdList(command, page)})
                    UNION SELECT r.Origin,{next} FROM reach r JOIN tapestry_edges e ON {join} WHERE e.GenerationId=@generation)
                {output}
                """;

            AddParameter(command, "@generation", generationId);

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(1))
                {
                    collected[reader.GetString(0)].Add(reader.GetString(1));
                }
            }

            foreach ((string key, List<string> value) in collected)
            {
                result.Add(key, value);
            }
        }

        return result;
    }
}
