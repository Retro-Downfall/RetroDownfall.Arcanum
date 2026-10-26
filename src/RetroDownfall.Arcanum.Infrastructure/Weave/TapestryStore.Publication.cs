using System.Data.Common;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

internal sealed partial class TapestryStore
{
    public Task<Result> PublishGenerationAsync(TapestryPublishRequest request, CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);

            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            TapestryGeneration? generation = await ReadGraphGenerationAsync(connection, transaction, request.GenerationId, cancellationToken).ConfigureAwait(false);

            if (generation is null || generation.Status != TapestryGenerationStatus.Building)
            {
                return GraphFailure("Tapestry.NotBuilding");
            }

            TapestryBuildSnapshot snapshot = request.Snapshot;

            if (await ReadRevisionsAsync(connection, transaction, snapshot.Scope, cancellationToken).ConfigureAwait(false)
                != (snapshot.CorpusRevision, snapshot.InvalidationRevision))
            {
                return GraphFailure("Tapestry.SourceChanged");
            }

            TapestryGenerationRecipe recipe = request.Recipe;

            if (snapshot.Corpus.ExceedsCeiling || !SupportedAlgorithm(generation.AlgorithmVersion)
                || generation.ScopeKind != snapshot.Scope.Kind || generation.ScopeId != snapshot.Scope.Id
                || generation.SourceRevision != snapshot.CorpusRevision || generation.ValidatedInvalidationRevision != snapshot.InvalidationRevision
                || generation.CorpusFingerprint != snapshot.Corpus.Fingerprint || generation.AlgorithmVersion != recipe.AlgorithmVersion
                || generation.SettingsFingerprint != recipe.SettingsFingerprint || generation.SummaryModel != recipe.SummaryModel
                || generation.SummaryRecipeVersion != recipe.SummaryRecipeVersion || generation.EmbeddingDimension != recipe.EmbeddingDimension
                || generation.EmbeddingIdentity != recipe.EmbeddingIdentity)
            {
                return GraphFailure("Tapestry.BuildIdentityChanged");
            }

            GraphCounts? counts = await ValidateGraphAsync(connection, transaction, generation, snapshot, cancellationToken).ConfigureAwait(false);

            if (counts is null)
            {
                return GraphFailure("Tapestry.InvalidGraph");
            }

            await using DbCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                UPDATE tapestry_generations SET Status='Superseded'
                WHERE ScopeKind=@kind AND ScopeId=@scope AND Status='Complete';
                UPDATE tapestry_generations SET Status='Complete',LayerCount=@layers,NodeCount=@nodes,
                    RootNodeCount=@roots,TerminalReason=@reason,CompletedAt=@completed
                WHERE GenerationId=@generation AND Status='Building';
                """;

            AddParameter(command, "@kind", generation.ScopeKind.ToString());

            AddParameter(command, "@scope", generation.ScopeId);

            AddParameter(command, "@generation", generation.GenerationId);

            AddParameter(command, "@layers", counts.Layers);

            AddParameter(command, "@nodes", counts.Nodes);

            AddParameter(command, "@roots", counts.Roots);

            AddParameter(command, "@reason", request.TerminalReason.ToString());

            AddParameter(command, "@completed", Iso(request.CompletedAt));

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success();
        }, cancellationToken);

    private static Result GraphFailure(string code) => Result.Failure(new Error(code, "The Tapestry generation could not be safely published."));

    private sealed record GraphCounts(int Nodes, int Layers, int Roots);

    private static async Task<GraphCounts?> ValidateGraphAsync(DbConnection connection, DbTransaction transaction, TapestryGeneration generation, TapestryBuildSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT n.NodeId,n.NodeKind,n.SourceKind,n.SourceId,n.ContentHash,n.Layer,n.EmbeddingDimension,
                e.Embedding,e.Dim,
                NOT EXISTS(SELECT 1 FROM tapestry_edges p WHERE p.GenerationId=n.GenerationId AND p.ChildNodeId=n.NodeId),
                EXISTS(SELECT 1 FROM tapestry_edges c WHERE c.GenerationId=n.GenerationId AND c.ParentNodeId=n.NodeId)
            FROM tapestry_nodes n LEFT JOIN tapestry_node_embeddings e ON e.NodeId=n.NodeId
            WHERE n.GenerationId=@generation ORDER BY n.NodeId
            """;

        AddParameter(command, "@generation", generation.GenerationId);

        List<(string SourceId, string ContentHash)> leaves = [];

        HashSet<string> sourceIds = new(StringComparer.Ordinal);

        int nodes = 0;

        int maxLayer = -1;

        int roots = 0;

        string expectedSourceKind = LeafCorpus.For(generation.ScopeKind).Kind.ToString();

        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.IsDBNull(7) || reader.IsDBNull(8) || reader.GetInt32(6) != generation.EmbeddingDimension
                    || reader.GetInt32(8) != generation.EmbeddingDimension || ((byte[])reader[7]).Length != generation.EmbeddingDimension * sizeof(float)
                    || !ValidVector(EmbeddingBlobCodec.Decode((byte[])reader[7]), generation.EmbeddingDimension))
                {
                    return null;
                }

                nodes++;

                maxLayer = Math.Max(maxLayer, reader.GetInt32(5));

                roots += reader.GetInt32(9);

                if (reader.GetString(1) == nameof(TapestryNodeKind.Leaf))
                {
                    if (reader.IsDBNull(2) || reader.GetString(2) != expectedSourceKind || reader.IsDBNull(3)
                        || !sourceIds.Add(reader.GetString(3)) || leaves.Count == TapestryLimits.MaxLeavesPerScope)
                    {
                        return null;
                    }

                    leaves.Add((reader.GetString(3), reader.GetString(4)));
                }
                else if (reader.GetInt32(10) == 0)
                {
                    return null;
                }
            }
        }

        if (nodes == 0 || roots == 0 || maxLayer == int.MaxValue || leaves.Count != snapshot.Corpus.LeafCount || TapestryHash.OfCorpus(leaves) != snapshot.Corpus.Fingerprint)
        {
            return null;
        }

        // Descending layers make every component terminate at a root, and every nonempty summary
        // reaches a leaf. Recheck the edge contract at publication as well as at each SQL write.
        command.CommandText = """
            SELECT 1 FROM tapestry_edges e
            LEFT JOIN tapestry_nodes p ON p.GenerationId=e.GenerationId AND p.NodeId=e.ParentNodeId
            LEFT JOIN tapestry_nodes c ON c.GenerationId=e.GenerationId AND c.NodeId=e.ChildNodeId
            WHERE e.GenerationId=@generation AND (p.NodeId IS NULL OR c.NodeId IS NULL OR p.NodeKind<>'Summary'
                OR p.Layer<=c.Layer OR p.ScopeKind IS NOT c.ScopeKind OR p.ScopeId IS NOT c.ScopeId)
            LIMIT 1
            """;

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null ? new(nodes, maxLayer + 1, roots) : null;
    }
}
