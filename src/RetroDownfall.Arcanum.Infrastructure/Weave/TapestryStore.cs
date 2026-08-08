using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Raw-SQL persistence for The Tapestry, reusing the scoped <see cref="ArcanumDbContext"/>'s
/// connection through <see cref="DbCommand"/> exactly like <c>SagaMemoryStore</c> — none of the
/// <c>tapestry_*</c> tables is in the compiled EF model (they are declared in <c>Data/Schema/Tables/</c>).
///
/// <para>The generation lifecycle is the correctness core: builds stage into a <c>Building</c> row,
/// <see cref="PublishGenerationAsync"/> flips exactly one scope's current generation inside a single
/// transaction, and <see cref="ReconcileGenerationsAsync"/> removes anything that is not the current
/// complete generation after a restart or cancellation.</para>
/// </summary>
internal sealed class TapestryStore(
    ArcanumDbContext db,
    WeaveIndexAvailability availability) : ITapestryStore
{

    public async Task<IReadOnlyList<TapestryScope>> DiscoverScopesAsync(
        bool includeWorkspace,
        bool includeSessionAttachments,
        bool includeSessions,
        CancellationToken cancellationToken)
    {

        List<TapestryScope> scopes = [];

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (includeWorkspace)
        {

            await CollectScopesAsync(
                connection,
                """SELECT DISTINCT "WorkspacePath" FROM "workspace_file_chunks" ORDER BY "WorkspacePath" """,
                TapestryScopeKind.Workspace,
                scopes,
                cancellationToken).ConfigureAwait(false);

        }

        if (includeSessionAttachments)
        {

            await CollectScopesAsync(
                connection,
                """SELECT DISTINCT "SessionId" FROM "session_attachment_chunks" ORDER BY "SessionId" """,
                TapestryScopeKind.SessionAttachment,
                scopes,
                cancellationToken).ConfigureAwait(false);

        }

        if (includeSessions)
        {

            await CollectScopesAsync(
                connection,
                """
                SELECT DISTINCT "SessionId" FROM "Entries"
                WHERE "Content" IS NOT NULL AND trim("Content") <> ''
                ORDER BY "SessionId"
                """,
                TapestryScopeKind.Session,
                scopes,
                cancellationToken).ConfigureAwait(false);

        }

        return scopes;

    }

    private static async Task CollectScopesAsync(
        DbConnection connection,
        string sql,
        TapestryScopeKind kind,
        List<TapestryScope> scopes,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            if (reader.IsDBNull(0))
            {

                continue;

            }

            string id = reader.GetString(0);

            if (id.Length > 0)
            {

                scopes.Add(new TapestryScope(kind, id));

            }

        }

    }

    public async Task<IReadOnlyList<TapestryLeafSource>> EnumerateLeafSourcesAsync(
        TapestryScope scope,
        int expectedDimensions,
        bool includeEmbeddings,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        // Each corpus joins its own already-imprinted embedding table so an unchanged leaf costs no
        // embedding call on rebuild. A missing or wrong-dimension companion simply yields NULL and the
        // builder embeds that leaf itself. The fingerprint-only variant drops that join: it reads no
        // BLOBs and decodes no vectors, which is the whole cost of answering "is this scope current?".
        command.CommandText = includeEmbeddings
            ? scope.Kind switch
            {
                TapestryScopeKind.Workspace =>
                    """
                    SELECT c."ChunkId", c."RelativePath", c."Content", e."Embedding", e."Dim"
                    FROM "workspace_file_chunks" c
                    LEFT JOIN "workspace_file_embeddings" e ON e."ChunkId" = c."ChunkId"
                    WHERE c."WorkspacePath" = @scopeId
                    ORDER BY c."ChunkId"
                    """,

                TapestryScopeKind.SessionAttachment =>
                    """
                    SELECT c."ChunkId", c."OriginalFileName", c."Content", e."Embedding", e."Dim"
                    FROM "session_attachment_chunks" c
                    LEFT JOIN "session_attachment_embeddings" e ON e."ChunkId" = c."ChunkId"
                    WHERE c."SessionId" = @scopeId AND c."RetrievalScope" IS NOT NULL
                    ORDER BY c."ChunkId"
                    """,

                _ =>
                    """
                    SELECT n."Id", n."Role", n."Content", e."Embedding", e."Dim"
                    FROM "Entries" n
                    LEFT JOIN "entry_embeddings" e ON e."EntryId" = n."Id"
                    WHERE n."SessionId" = @scopeId
                      AND n."Content" IS NOT NULL AND trim(n."Content") <> ''
                    ORDER BY n."Id"
                    """,
            }
            : scope.Kind switch
            {
                TapestryScopeKind.Workspace =>
                    """
                    SELECT c."ChunkId", c."RelativePath", c."Content"
                    FROM "workspace_file_chunks" c
                    WHERE c."WorkspacePath" = @scopeId
                    ORDER BY c."ChunkId"
                    """,

                TapestryScopeKind.SessionAttachment =>
                    """
                    SELECT c."ChunkId", c."OriginalFileName", c."Content"
                    FROM "session_attachment_chunks" c
                    WHERE c."SessionId" = @scopeId AND c."RetrievalScope" IS NOT NULL
                    ORDER BY c."ChunkId"
                    """,

                _ =>
                    """
                    SELECT n."Id", n."Role", n."Content"
                    FROM "Entries" n
                    WHERE n."SessionId" = @scopeId
                      AND n."Content" IS NOT NULL AND trim(n."Content") <> ''
                    ORDER BY n."Id"
                    """,
            };

        AddParameter(command, "@scopeId", scope.Id);

        List<TapestryLeafSource> leaves = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string content = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);

            if (content.Trim().Length == 0)
            {

                continue;

            }

            float[]? embedding = null;

            if (includeEmbeddings
                && !reader.IsDBNull(3)
                && !reader.IsDBNull(4)
                && Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture) == expectedDimensions)
            {

                float[] decoded = EmbeddingBlobCodec.Decode((byte[])reader[3]);

                if (decoded.Length == expectedDimensions)
                {

                    embedding = decoded;

                }

            }

            leaves.Add(new TapestryLeafSource(
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                content,
                TapestryHash.OfContent(content),
                embedding));

        }

        return leaves;

    }

    public async Task<TapestryGeneration?> GetCurrentGenerationAsync(
        TapestryScope scope,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT {GenerationColumns}
            FROM "tapestry_generations"
            WHERE "ScopeKind" = @scopeKind AND "ScopeId" = @scopeId AND "Status" = 'Complete'
            ORDER BY "CompletedAt" DESC
            LIMIT 1
            """;

        AddParameter(command, "@scopeKind", scope.Kind.ToString());

        AddParameter(command, "@scopeId", scope.Id);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadGeneration(reader)
            : null;

    }

    public Task<string> BeginGenerationAsync(
        TapestryScope scope,
        string algorithmVersion,
        string settingsFingerprint,
        string? summaryModel,
        string summaryRecipeVersion,
        int embeddingDimension,
        string corpusFingerprint,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {

        string generationId = Guid.NewGuid().ToString("N");

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand command = connection.CreateCommand();

                command.CommandText =
                    """
                    INSERT INTO "tapestry_generations"
                        ("GenerationId", "ScopeKind", "ScopeId", "Status", "AlgorithmVersion",
                         "SettingsFingerprint", "SummaryModel", "SummaryRecipeVersion",
                         "EmbeddingDimension", "CorpusFingerprint", "LayerCount", "NodeCount",
                         "RootNodeCount", "TerminalReason", "StartedAt", "CompletedAt")
                    VALUES (@generationId, @scopeKind, @scopeId, 'Building', @algorithmVersion,
                            @settingsFingerprint, @summaryModel, @summaryRecipeVersion,
                            @embeddingDimension, @corpusFingerprint, 0, 0, 0, NULL, @startedAt, NULL)
                    """;

                AddParameter(command, "@generationId", generationId);

                AddParameter(command, "@scopeKind", scope.Kind.ToString());

                AddParameter(command, "@scopeId", scope.Id);

                AddParameter(command, "@algorithmVersion", algorithmVersion);

                AddParameter(command, "@settingsFingerprint", settingsFingerprint);

                AddParameter(command, "@summaryModel", (object?)summaryModel ?? DBNull.Value);

                AddParameter(command, "@summaryRecipeVersion", summaryRecipeVersion);

                AddParameter(command, "@embeddingDimension", embeddingDimension);

                AddParameter(command, "@corpusFingerprint", corpusFingerprint);

                AddParameter(command, "@startedAt", Iso(startedAt));

                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                return generationId;

            },
            cancellationToken);

    }

    public Task AppendNodesAsync(
        IReadOnlyList<TapestryNodeWrite> nodes,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(nodes);

        if (nodes.Count == 0)
        {

            return Task.CompletedTask;

        }

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                // One transaction per checkpoint: a torn write can never leave a node without its
                // embedding, and a retry after SQLITE_BUSY starts from a clean transaction.
                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (TapestryNodeWrite write in nodes)
                {

                    await InsertNodeAsync(connection, transaction, write, cancellationToken).ConfigureAwait(false);

                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    private async Task InsertNodeAsync(
        DbConnection connection,
        DbTransaction transaction,
        TapestryNodeWrite write,
        CancellationToken cancellationToken)
    {

        TapestryNode node = write.Node;

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            INSERT INTO "tapestry_nodes"
                ("NodeId", "GenerationId", "ScopeKind", "ScopeId", "Layer", "ParentScopeKey",
                 "NodeKind", "ParentNodeId", "SourceKind", "SourceId", "SourceLabel", "Content",
                 "ContentHash", "ChildMembershipHash", "DescendantLeafCount", "ClusterOrdinal",
                 "PartitionReason", "EmbeddingDimension", "CreatedAt")
            VALUES (@nodeId, @generationId, @scopeKind, @scopeId, @layer, @parentScopeKey,
                    @nodeKind, @parentNodeId, @sourceKind, @sourceId, @sourceLabel, @content,
                    @contentHash, @childMembershipHash, @descendantLeafCount, @clusterOrdinal,
                    @partitionReason, @embeddingDimension, @createdAt)
            """;

        AddParameter(command, "@nodeId", node.NodeId);

        AddParameter(command, "@generationId", node.GenerationId);

        AddParameter(command, "@scopeKind", node.ScopeKind.ToString());

        AddParameter(command, "@scopeId", node.ScopeId);

        AddParameter(command, "@layer", node.Layer);

        AddParameter(command, "@parentScopeKey", ParentScopeKey(node.GenerationId, node.ParentNodeId));

        AddParameter(command, "@nodeKind", node.NodeKind.ToString());

        AddParameter(command, "@parentNodeId", (object?)node.ParentNodeId ?? DBNull.Value);

        AddParameter(command, "@sourceKind", (object?)node.SourceKind?.ToString() ?? DBNull.Value);

        AddParameter(command, "@sourceId", (object?)node.SourceId ?? DBNull.Value);

        AddParameter(command, "@sourceLabel", node.SourceLabel);

        AddParameter(command, "@content", (object?)node.Content ?? DBNull.Value);

        AddParameter(command, "@contentHash", node.ContentHash);

        AddParameter(command, "@childMembershipHash", (object?)node.ChildMembershipHash ?? DBNull.Value);

        AddParameter(command, "@descendantLeafCount", node.DescendantLeafCount);

        AddParameter(command, "@clusterOrdinal", node.ClusterOrdinal);

        AddParameter(command, "@partitionReason", node.PartitionReason.ToString());

        AddParameter(command, "@embeddingDimension", node.EmbeddingDimension);

        AddParameter(command, "@createdAt", Iso(node.CreatedAt));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        byte[] blob = EmbeddingBlobCodec.Encode(write.Embedding);

        await using DbCommand embeddingCommand = connection.CreateCommand();

        embeddingCommand.Transaction = transaction;

        embeddingCommand.CommandText =
            """
            INSERT OR REPLACE INTO "tapestry_node_embeddings" ("NodeId", "Embedding", "Dim")
            VALUES (@nodeId, @embedding, @dim)
            """;

        AddParameter(embeddingCommand, "@nodeId", node.NodeId);

        AddParameter(embeddingCommand, "@embedding", blob);

        AddParameter(embeddingCommand, "@dim", write.Embedding.Length);

        _ = await embeddingCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (!availability.IsVecAvailable)
        {

            return;

        }

        await using DbCommand vecCommand = connection.CreateCommand();

        vecCommand.Transaction = transaction;

        vecCommand.CommandText =
            """
            INSERT OR REPLACE INTO "tapestry_node_embeddings_vec" ("NodeId", "Embedding")
            VALUES (@nodeId, @embedding)
            """;

        AddParameter(vecCommand, "@nodeId", node.NodeId);

        AddParameter(vecCommand, "@embedding", blob);

        _ = await vecCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    public Task SetParentAsync(
        string generationId,
        string parentNodeId,
        IReadOnlyList<string> childNodeIds,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(childNodeIds);

        if (childNodeIds.Count == 0)
        {

            return Task.CompletedTask;

        }

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (string childNodeId in childNodeIds)
                {

                    await using DbCommand command = connection.CreateCommand();

                    command.Transaction = transaction;

                    command.CommandText =
                        """
                        UPDATE "tapestry_nodes"
                        SET "ParentNodeId" = @parentNodeId,
                            "ParentScopeKey" = @parentScopeKey
                        WHERE "NodeId" = @childNodeId
                        """;

                    AddParameter(command, "@parentNodeId", parentNodeId);

                    AddParameter(command, "@parentScopeKey", ParentScopeKey(generationId, parentNodeId));

                    AddParameter(command, "@childNodeId", childNodeId);

                    _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    public Task PublishGenerationAsync(
        string generationId,
        int layerCount,
        int nodeCount,
        int rootNodeCount,
        TapestryTerminalReason terminalReason,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                // Supersede-then-complete inside one transaction is the atomic current-generation
                // switch: a reader either sees the old complete generation or the new one, never both
                // and never neither.
                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                await using (DbCommand supersede = connection.CreateCommand())
                {

                    supersede.Transaction = transaction;

                    supersede.CommandText =
                        """
                        UPDATE "tapestry_generations" SET "Status" = 'Superseded'
                        WHERE "Status" = 'Complete'
                          AND ("ScopeKind", "ScopeId") = (
                              SELECT "ScopeKind", "ScopeId" FROM "tapestry_generations"
                              WHERE "GenerationId" = @generationId)
                        """;

                    AddParameter(supersede, "@generationId", generationId);

                    _ = await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await using (DbCommand publish = connection.CreateCommand())
                {

                    publish.Transaction = transaction;

                    publish.CommandText =
                        """
                        UPDATE "tapestry_generations"
                        SET "Status" = 'Complete',
                            "LayerCount" = @layerCount,
                            "NodeCount" = @nodeCount,
                            "RootNodeCount" = @rootNodeCount,
                            "TerminalReason" = @terminalReason,
                            "CompletedAt" = @completedAt
                        WHERE "GenerationId" = @generationId AND "Status" = 'Building'
                        """;

                    AddParameter(publish, "@layerCount", layerCount);

                    AddParameter(publish, "@nodeCount", nodeCount);

                    AddParameter(publish, "@rootNodeCount", rootNodeCount);

                    AddParameter(publish, "@terminalReason", terminalReason.ToString());

                    AddParameter(publish, "@completedAt", Iso(completedAt));

                    AddParameter(publish, "@generationId", generationId);

                    _ = await publish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    public Task AbandonGenerationAsync(string generationId, CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                await DeleteGenerationsAsync(
                    connection,
                    transaction,
                    "\"GenerationId\" = @generationId",
                    command => AddParameter(command, "@generationId", generationId),
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    public Task<int> ReconcileGenerationsAsync(CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                int removed = await DeleteGenerationsAsync(
                    connection,
                    transaction,
                    "\"Status\" <> 'Complete'",
                    static _ => { },
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return removed;

            },
            cancellationToken);

    /// <summary>
    /// Deletes matching generations. The optional vec0 mirror has no foreign key, so its rows are
    /// removed explicitly before the cascade takes the BLOB rows with the nodes.
    /// </summary>
    private async Task<int> DeleteGenerationsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string predicate,
        Action<DbCommand> bind,
        CancellationToken cancellationToken)
    {

        if (availability.IsVecAvailable)
        {

            try
            {

                await using DbCommand vecCommand = connection.CreateCommand();

                vecCommand.Transaction = transaction;

                vecCommand.CommandText =
                    $"""
                    DELETE FROM "tapestry_node_embeddings_vec"
                    WHERE "NodeId" IN (
                        SELECT "NodeId" FROM "tapestry_nodes"
                        WHERE "GenerationId" IN (
                            SELECT "GenerationId" FROM "tapestry_generations" WHERE {predicate}))
                    """;

                bind(vecCommand);

                _ = await vecCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {

                // sqlite-vec became unavailable after schema creation: the BLOB tables below remain
                // authoritative, so this is not a failure.

            }

        }

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"""DELETE FROM "tapestry_generations" WHERE {predicate}""";

        bind(command);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    public async Task<IReadOnlyList<TapestryNode>> GetLayerNodesAsync(
        string generationId,
        int layer,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT {NodeColumns}
            FROM "tapestry_nodes"
            WHERE "GenerationId" = @generationId AND "Layer" = @layer
            ORDER BY "NodeId"
            """;

        AddParameter(command, "@generationId", generationId);

        AddParameter(command, "@layer", layer);

        List<TapestryNode> nodes = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            nodes.Add(ReadNode(reader));

        }

        return nodes;

    }

    public async Task<IReadOnlyDictionary<string, float[]>> GetNodeEmbeddingsAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(nodeIds);

        Dictionary<string, float[]> embeddings = new(StringComparer.Ordinal);

        if (nodeIds.Count == 0)
        {

            return embeddings;

        }

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT "NodeId", "Embedding" FROM "tapestry_node_embeddings"
            WHERE "NodeId" IN ({BindIdList(command, nodeIds)})
            """;

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            embeddings[reader.GetString(0)] = EmbeddingBlobCodec.Decode((byte[])reader[1]);

        }

        return embeddings;

    }

    public async Task<TapestrySummaryReuseCandidate?> TryGetReusableSummaryAsync(
        TapestryScope scope,
        string childMembershipHash,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT n."Content", n."ContentHash", e."Embedding"
            FROM "tapestry_nodes" n
            INNER JOIN "tapestry_generations" g ON g."GenerationId" = n."GenerationId"
            INNER JOIN "tapestry_node_embeddings" e ON e."NodeId" = n."NodeId"
            WHERE g."ScopeKind" = @scopeKind
              AND g."ScopeId" = @scopeId
              AND g."Status" = 'Complete'
              AND n."NodeKind" = 'Summary'
              AND n."ChildMembershipHash" = @membershipHash
              AND n."Content" IS NOT NULL
            LIMIT 1
            """;

        AddParameter(command, "@scopeKind", scope.Kind.ToString());

        AddParameter(command, "@scopeId", scope.Id);

        AddParameter(command, "@membershipHash", childMembershipHash);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new TapestrySummaryReuseCandidate(
                childMembershipHash,
                reader.GetString(0),
                reader.GetString(1),
                EmbeddingBlobCodec.Decode((byte[])reader[2]))
            : null;

    }

    public async Task<IReadOnlyList<TapestryRetrievedNode>> HydrateRetrievedNodesAsync(
        TapestryGeneration generation,
        IReadOnlyList<(string NodeId, float Similarity)> hits,
        TapestryRetrievalMode mode,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(generation);

        ArgumentNullException.ThrowIfNull(hits);

        if (hits.Count == 0)
        {

            return [];

        }

        string[] nodeIds = [.. hits.Select(static hit => hit.NodeId)];

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, HydratedNode> hydrated = new(StringComparer.Ordinal);

        await using (DbCommand command = connection.CreateCommand())
        {

            // Leaf content is resolved from its corpus row rather than duplicated onto the node, so
            // the join target depends on the generation's scope kind. Summary content lives on the
            // node itself and needs no join.
            string leafJoin = generation.ScopeKind switch
            {
                TapestryScopeKind.Workspace =>
                    """LEFT JOIN "workspace_file_chunks" s ON s."ChunkId" = n."SourceId" """,

                TapestryScopeKind.SessionAttachment =>
                    """LEFT JOIN "session_attachment_chunks" s ON s."ChunkId" = n."SourceId" """,

                _ => """LEFT JOIN "Entries" s ON s."Id" = n."SourceId" """,
            };

            command.CommandText =
                $"""
                SELECT n."NodeId", n."Layer", n."NodeKind", n."SourceLabel", n."ContentHash",
                       n."DescendantLeafCount", n."ParentNodeId", n."Content", s."Content"
                FROM "tapestry_nodes" n
                {leafJoin}
                WHERE n."GenerationId" = @generationId
                  AND n."NodeId" IN ({BindIdList(command, nodeIds)})
                """;

            AddParameter(command, "@generationId", generation.GenerationId);

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                bool isSummary = string.Equals(reader.GetString(2), nameof(TapestryNodeKind.Summary), StringComparison.Ordinal);

                string? content = isSummary
                    ? reader.IsDBNull(7) ? null : reader.GetString(7)
                    : reader.IsDBNull(8) ? null : reader.GetString(8);

                if (content is null)
                {

                    continue;

                }

                string recordedHash = reader.GetString(4);

                // A leaf references a live corpus row. If that row changed after this generation was
                // published, the recorded hash no longer describes what we would inject — drop the
                // leaf rather than present stale content under a hash that no longer matches. The
                // corpus fingerprint change will rebuild the tree shortly.
                if (!isSummary
                    && !string.Equals(TapestryHash.OfContent(content), recordedHash, StringComparison.Ordinal))
                {

                    continue;

                }

                hydrated[reader.GetString(0)] = new HydratedNode(
                    reader.GetInt32(1),
                    isSummary ? TapestryNodeKind.Summary : TapestryNodeKind.Leaf,
                    reader.GetString(3),
                    content,
                    recordedHash,
                    reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6));

            }

        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> ancestors = await LoadAncestorsAsync(
            connection,
            generation.GenerationId,
            [.. hydrated.Keys],
            cancellationToken).ConfigureAwait(false);

        List<TapestryRetrievedNode> results = [];

        foreach ((string nodeId, float similarity) in hits)
        {

            if (!hydrated.TryGetValue(nodeId, out HydratedNode? node))
            {

                continue;

            }

            results.Add(new TapestryRetrievedNode(
                nodeId,
                generation.GenerationId,
                generation.ScopeKind,
                generation.ScopeId,
                node.Layer,
                node.NodeKind,
                node.SourceLabel,
                node.Content,
                node.ContentHash,
                node.DescendantLeafCount,
                similarity,
                mode,
                ancestors.TryGetValue(nodeId, out IReadOnlyList<string>? chain) ? chain : []));

        }

        return results;

    }

    /// <summary>
    /// Walks each node's parent chain in one recursive CTE. Lineage is what makes ancestor/descendant
    /// overlap detectable: a summary and one of its descendants have different content and different
    /// hashes, so the shared ledger's exact-content dedupe cannot see the redundancy.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> LoadAncestorsAsync(
        DbConnection connection,
        string generationId,
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken)
    {

        Dictionary<string, IReadOnlyList<string>> ancestors = new(StringComparer.Ordinal);

        if (nodeIds.Count == 0)
        {

            return ancestors;

        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            WITH RECURSIVE lineage("Origin", "AncestorId", "Depth") AS (
                SELECT n."NodeId", n."ParentNodeId", 1
                FROM "tapestry_nodes" n
                WHERE n."GenerationId" = @generationId
                  AND n."ParentNodeId" IS NOT NULL
                  AND n."NodeId" IN ({BindIdList(command, nodeIds)})
                UNION ALL
                SELECT l."Origin", p."ParentNodeId", l."Depth" + 1
                FROM lineage l
                INNER JOIN "tapestry_nodes" p ON p."NodeId" = l."AncestorId"
                WHERE p."ParentNodeId" IS NOT NULL AND l."Depth" < 64
            )
            SELECT "Origin", "AncestorId" FROM lineage ORDER BY "Origin", "Depth"
            """;

        AddParameter(command, "@generationId", generationId);

        Dictionary<string, List<string>> chains = new(StringComparer.Ordinal);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            if (reader.IsDBNull(1))
            {

                continue;

            }

            string origin = reader.GetString(0);

            if (!chains.TryGetValue(origin, out List<string>? chain))
            {

                chain = [];

                chains[origin] = chain;

            }

            chain.Add(reader.GetString(1));

        }

        foreach ((string origin, List<string> chain) in chains)
        {

            ancestors[origin] = chain;

        }

        return ancestors;

    }

    public async Task<int> GetTerminalLayerAsync(string generationId, CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """SELECT COALESCE(MAX("Layer"), 0) FROM "tapestry_nodes" WHERE "GenerationId" = @generationId""";

        AddParameter(command, "@generationId", generationId);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);

    }

    public async Task<IReadOnlyList<TapestryScopeStatus>> GetScopeStatusesAsync(
        Guid? sessionId,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT g."ScopeKind", g."ScopeId", g."LayerCount", g."NodeCount", g."RootNodeCount",
                   g."TerminalReason", g."AlgorithmVersion", g."CompletedAt",
                   (SELECT COUNT(*) FROM "tapestry_nodes" n
                    WHERE n."GenerationId" = g."GenerationId" AND n."NodeKind" = 'Leaf'),
                   (SELECT COUNT(*) FROM "tapestry_nodes" n
                    WHERE n."GenerationId" = g."GenerationId" AND n."NodeKind" = 'Summary')
            FROM "tapestry_generations" g
            WHERE g."Status" = 'Complete'
              AND (@sessionId IS NULL OR (g."ScopeKind" <> 'Workspace' AND g."ScopeId" = @sessionId))
            ORDER BY g."ScopeKind", g."ScopeId"
            """;

        AddParameter(command, "@sessionId", sessionId is null ? DBNull.Value : sessionId.Value.ToString());

        List<TapestryScopeStatus> statuses = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            statuses.Add(new TapestryScopeStatus(
                ParseEnum(reader.GetString(0), TapestryScopeKind.Workspace),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : ParseEnum(reader.GetString(5), TapestryTerminalReason.LeafOnly),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7))));

        }

        return statuses;

    }

    public async Task<int> CountPublishedNodesAsync(Guid? sessionId, CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT COUNT(*)
            FROM "tapestry_nodes" n
            INNER JOIN "tapestry_generations" g ON g."GenerationId" = n."GenerationId"
            WHERE g."Status" = 'Complete'
              AND (@sessionId IS NULL OR (g."ScopeKind" <> 'Workspace' AND g."ScopeId" = @sessionId))
            """;

        AddParameter(command, "@sessionId", sessionId is null ? DBNull.Value : sessionId.Value.ToString());

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);

    }

    private static string ParentScopeKey(string generationId, string? parentNodeId) =>
        TapestryStorageKeys.ParentScopeKey(generationId, parentNodeId);

    private const string GenerationColumns =
        """
        "GenerationId", "ScopeKind", "ScopeId", "Status", "AlgorithmVersion", "SettingsFingerprint",
        "SummaryModel", "SummaryRecipeVersion", "EmbeddingDimension", "CorpusFingerprint",
        "LayerCount", "NodeCount", "RootNodeCount", "TerminalReason", "StartedAt", "CompletedAt"
        """;

    private const string NodeColumns =
        """
        "NodeId", "GenerationId", "ScopeKind", "ScopeId", "Layer", "NodeKind", "ParentNodeId",
        "SourceKind", "SourceId", "SourceLabel", "Content", "ContentHash", "ChildMembershipHash",
        "DescendantLeafCount", "ClusterOrdinal", "PartitionReason", "EmbeddingDimension", "CreatedAt"
        """;

    private static TapestryGeneration ReadGeneration(DbDataReader reader) =>
        new(
            reader.GetString(0),
            ParseEnum(reader.GetString(1), TapestryScopeKind.Workspace),
            reader.GetString(2),
            ParseEnum(reader.GetString(3), TapestryGenerationStatus.Building),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetString(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.IsDBNull(13) ? null : ParseEnum(reader.GetString(13), TapestryTerminalReason.LeafOnly),
            ParseTimestamp(reader.GetString(14)),
            reader.IsDBNull(15) ? null : ParseTimestamp(reader.GetString(15)));

    private static TapestryNode ReadNode(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            ParseEnum(reader.GetString(2), TapestryScopeKind.Workspace),
            reader.GetString(3),
            reader.GetInt32(4),
            ParseEnum(reader.GetString(5), TapestryNodeKind.Leaf),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : ParseEnum(reader.GetString(7), TapestryLeafSourceKind.WorkspaceFileChunk),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            ParseEnum(reader.GetString(15), TapestryPartitionReason.None),
            reader.GetInt32(16),
            ParseTimestamp(reader.GetString(17)));

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out TEnum parsed) ? parsed : fallback;

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static string Iso(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Binds an id list as individual parameters rather than interpolating values, so caller-supplied
    /// ids can never reach the SQL text.
    /// </summary>
    private static string BindIdList(DbCommand command, IReadOnlyList<string> ids)
    {

        string[] placeholders = new string[ids.Count];

        for (int index = 0; index < ids.Count; index++)
        {

            string name = $"@id{index.ToString(CultureInfo.InvariantCulture)}";

            placeholders[index] = name;

            AddParameter(command, name, ids[index]);

        }

        return string.Join(", ", placeholders);

    }

    private static void AddParameter(DbCommand command, string name, object value)
    {

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        command.Parameters.Add(parameter);

    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        }

        return connection;

    }

    private sealed record HydratedNode(
        int Layer,
        TapestryNodeKind NodeKind,
        string SourceLabel,
        string Content,
        string ContentHash,
        int DescendantLeafCount,
        string? ParentNodeId);

}
