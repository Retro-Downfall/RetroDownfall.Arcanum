using System.Buffers.Binary;
using System.Collections.Immutable;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestDiscoveryStore
{
    private static async Task<ImmutableArray<LongRestCandidate>> HierarchyCandidatesAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, LongRestTarget anchor, LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        Guid node;

        await using (SqliteCommand command = Command(connection, transaction,
            "SELECT NodeId FROM long_rest_discovery_nodes WHERE GenerationId = $generation AND Layer = 0 AND MemoryId = $memory;",
            ("$generation", Format(generation.GenerationId)), ("$memory", anchor.MemoryId)))
        {
            node = Guid.Parse((string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
        }

        // The query is the anchor's nearest generated ancestor: the most specific summary of what the anchor is about. Every ancestor is
        // on the anchor's own chain, so none of them is ever ranked, selected or expanded: a chain node always matches the anchor
        // and would hand back the anchor's own tree. Excluding a node from ranking is not excluding it from verification: the nearest
        // ancestor is read here and the topmost one just below, and reading a node verifies the whole subtree under it, which is every
        // other node of the chain. So a record altered anywhere on the chain refuses the anchor.
        LongRestHierarchyInput? query = null;

        List<Guid> chain = [];

        for (int depth = 0; depth < LongRestDiscoveryLimits.HierarchyLevels; depth++)
        {
            await using SqliteCommand parent = Command(connection, transaction, """
                SELECT parent.NodeId FROM long_rest_discovery_edges edge
                JOIN long_rest_discovery_nodes parent ON parent.GenerationId = edge.GenerationId AND parent.NodeId = edge.ParentNodeId
                JOIN long_rest_discovery_nodes child ON child.GenerationId = edge.GenerationId AND child.NodeId = edge.ChildNodeId
                WHERE edge.GenerationId = $generation AND edge.ChildNodeId = $child AND parent.Layer > child.Layer;
                """, ("$generation", Format(generation.GenerationId)), ("$child", Format(node)));

            if (await parent.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string identity)
            {
                break;
            }

            node = Guid.Parse(identity);

            chain.Add(node);

            query ??= await ReadHierarchyNodeAsync(connection, transaction, generation, node, capability, cancellationToken).ConfigureAwait(false);
        }

        if (chain.Count > 1)
        {
            _ = await ReadHierarchyNodeAsync(connection, transaction, generation, chain[^1], capability, cancellationToken).ConfigureAwait(false);
        }

        // A raw observed Saga vector never serves as a query in the configured generated space.
        if (query is null)
        {
            return [];
        }

        float[] direction = SphericalKMeans.NormalizedDirection(query.Vector.ToArray());

        PriorityQueue<SummaryRank, (double Score, ulong ReverseHigh, ulong ReverseLow)> best = new();

        string? cursor = null;

        while (true)
        {
            List<string> page = [];

            await using (SqliteCommand command = Command(connection, transaction, """
                SELECT NodeId FROM long_rest_discovery_nodes WHERE GenerationId = $generation AND Layer > 0
                    AND ($cursor IS NULL OR NodeId > $cursor) ORDER BY NodeId LIMIT 128;
                """, ("$generation", Format(generation.GenerationId)), ("$cursor", cursor)))
            {
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    page.Add(reader.GetString(0));
                }
            }

            if (page.Count == 0)
            {
                break;
            }

            foreach (string identity in page)
            {
                Guid summary = Guid.Parse(identity);

                if (chain.Contains(summary))
                {
                    continue;
                }

                LongRestHierarchyInput candidate = await ReadHierarchyNodeAsync(connection, transaction, generation, summary, capability, cancellationToken).ConfigureAwait(false);

                double cosine = SphericalKMeans.CosineToDirection(direction, candidate.Vector.AsSpan());

                if (double.IsNaN(cosine))
                {
                    throw new InvalidDataException();
                }

                double score = Math.Clamp(cosine, -1, 1);

                if (score < LongRestDiscoveryLimits.SummaryMinimumCosinePermille / 1000.0)
                {
                    continue;
                }

                SummaryRank rank = new(candidate.Child.NodeId, score);

                byte[] canonical = rank.Node.ToByteArray(bigEndian: true);

                best.Enqueue(rank, (rank.Score, ~BinaryPrimitives.ReadUInt64BigEndian(canonical.AsSpan(0, 8)),
                    ~BinaryPrimitives.ReadUInt64BigEndian(canonical.AsSpan(8, 8))));

                if (best.Count > LongRestDiscoveryLimits.SummaryTopK)
                {
                    best.Dequeue();
                }
            }

            cursor = page[^1];
        }

        LongRestHierarchyBuilder.SelectedSummaries.Record(best.Count);

        ImmutableArray<LongRestHierarchyExpansion>.Builder expansions = ImmutableArray.CreateBuilder<LongRestHierarchyExpansion>();

        foreach (SummaryRank selected in best.UnorderedItems.Select(item => item.Element).OrderByDescending(item => item.Score).ThenBy(item => item.Node.ToString("D"), StringComparer.Ordinal))
        {
            List<Guid> children = await ReadChildNodesAsync(connection, transaction, generation.GenerationId, selected.Node, cancellationToken).ConfigureAwait(false);

            List<HierarchyLeafCursor> branches = children.Select(child => new HierarchyLeafCursor(child)).ToList();

            ImmutableArray<LongRestTarget>.Builder leaves = ImmutableArray.CreateBuilder<LongRestTarget>();

            int emitted = 0;

            while (branches.Count > 0 && emitted < LongRestDiscoveryLimits.ExpandedLeaves)
            {
                for (int branch = 0; branch < branches.Count && emitted < LongRestDiscoveryLimits.ExpandedLeaves;)
                {
                    int otherFrames = branches.Sum(item => item.FrontierCount) - branches[branch].FrontierCount;

                    string? memory = await branches[branch].NextAsync(connection, transaction, generation.GenerationId, cancellationToken).ConfigureAwait(false);

                    int frontier = otherFrames + branches[branch].MaximumFrontierCount;

                    LongRestHierarchyBuilder.TraversalFrontier.Record(frontier);

                    if (frontier > LongRestDiscoveryLimits.TraversalFrontier)
                    {
                        throw new InvalidDataException();
                    }

                    if (memory is null)
                    {
                        branches.RemoveAt(branch);

                        continue;
                    }

                    branch++;

                    LongRestSourceLeaf leaf = await ReadFrozenLeafAsync(connection, transaction, generation, memory, capability, cancellationToken).ConfigureAwait(false);

                    if (!CandidateEligible(leaf) || memory == anchor.MemoryId)
                    {
                        continue;
                    }

                    emitted++;

                    leaves.Add(leaf.Target);
                }
            }

            LongRestHierarchyBuilder.ExpandedLeaves.Record(emitted);

            expansions.Add(new(selected.Score, leaves.ToImmutable()));
        }

        // Selected summaries expand in rank order, but the lane takes their leaves in turn, so the best summary cannot fill it alone.
        return LongRestDiscoveryPolicy.InterleaveExpansions(anchor, expansions.ToImmutable());
    }

    private static async Task<List<Guid>> ReadChildNodesAsync(SqliteConnection connection, SqliteTransaction transaction, Guid generation,
        Guid parent, CancellationToken cancellationToken)
    {
        List<Guid> children = [];

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT edge.ChildNodeId, edge.Ordinal, child.Layer, parent.Layer FROM long_rest_discovery_edges edge
            LEFT JOIN long_rest_discovery_nodes child ON child.NodeId = edge.ChildNodeId AND child.GenerationId = edge.GenerationId
            LEFT JOIN long_rest_discovery_nodes parent ON parent.NodeId = edge.ParentNodeId AND parent.GenerationId = edge.GenerationId
            WHERE edge.GenerationId = $generation AND edge.ParentNodeId = $parent ORDER BY edge.Ordinal LIMIT 17;
            """, ("$generation", Format(generation)), ("$parent", Format(parent)));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(2) || reader.IsDBNull(3) || reader.GetInt32(2) < 0 || reader.GetInt32(2) >= reader.GetInt32(3)
                || reader.GetInt32(1) != children.Count + 1 || children.Count == LongRestDiscoveryLimits.HierarchyChildren)
            {
                throw new InvalidDataException();
            }

            children.Add(Guid.Parse(reader.GetString(0)));
        }

        return children;
    }

    private sealed record SummaryRank(Guid Node, double Score);

    /// <summary>A single content-free root-to-leaf path, with one edge ordinal per actual level.</summary>
    private sealed class HierarchyLeafCursor(Guid root)
    {
        private readonly List<(Guid Node, int NextOrdinal)> _frames = [(root, 1)];

        internal int FrontierCount => _frames.Count;

        internal int MaximumFrontierCount { get; private set; }

        internal async Task<string?> NextAsync(SqliteConnection connection, SqliteTransaction transaction, Guid generation, CancellationToken cancellationToken)
        {
            MaximumFrontierCount = _frames.Count;

            while (_frames.Count > 0)
            {
                (Guid node, int next) = _frames[^1];

                await using SqliteCommand command = Command(connection, transaction,
                    "SELECT Layer, MemoryId FROM long_rest_discovery_nodes WHERE GenerationId = $generation AND NodeId = $node;",
                    ("$generation", Format(generation)), ("$node", Format(node)));

                int layer;

                string? memory;

                await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidDataException();
                    }

                    layer = reader.GetInt32(0);

                    memory = reader.IsDBNull(1) ? null : reader.GetString(1);
                }

                if (layer == 0)
                {
                    _frames.RemoveAt(_frames.Count - 1);

                    return memory ?? throw new InvalidDataException();
                }

                command.CommandText = """
                    SELECT child.NodeId FROM long_rest_discovery_edges edge JOIN long_rest_discovery_nodes child
                        ON child.GenerationId = edge.GenerationId AND child.NodeId = edge.ChildNodeId
                    WHERE edge.GenerationId = $generation AND edge.ParentNodeId = $node AND edge.Ordinal = $ordinal AND child.Layer < $layer;
                    """;

                command.Parameters.AddWithValue("$ordinal", next);

                command.Parameters.AddWithValue("$layer", layer);

                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string child)
                {
                    _frames.RemoveAt(_frames.Count - 1);

                    continue;
                }

                _frames[^1] = (node, checked(next + 1));

                _frames.Add((Guid.Parse(child), 1));

                MaximumFrontierCount = Math.Max(MaximumFrontierCount, _frames.Count);

                if (_frames.Count > LongRestDiscoveryLimits.HierarchyLevels + 1)
                {
                    throw new InvalidDataException();
                }
            }

            return null;
        }
    }
}
