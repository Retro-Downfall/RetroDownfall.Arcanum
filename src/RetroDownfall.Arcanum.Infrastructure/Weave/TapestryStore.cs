using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

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
    /// <summary>
    /// The live-scope-id query for each corpus. Both <see cref="DiscoverScopesAsync"/> and
    /// <see cref="PruneRemovedScopesAsync"/> read from here, so "which scopes exist" has exactly one
    /// definition: a tree can never be pruned on a rule the sweep would not also have rebuilt it on.
    /// </summary>
    private static string LiveScopeIdQuery(TapestryScopeKind kind) => kind switch
    {
        TapestryScopeKind.Workspace =>
            """SELECT DISTINCT "WorkspacePath" FROM "workspace_file_chunks" """,
        TapestryScopeKind.SessionAttachment =>
            """SELECT DISTINCT "SessionId" FROM "session_attachment_chunks" """,
        TapestryScopeKind.Session =>
            """
            SELECT DISTINCT "SessionId" FROM "Entries"
            WHERE "Content" IS NOT NULL AND trim("Content") <> ''
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Tapestry scope kind."),
    };

    private static string OrderedLiveScopeIdQuery(TapestryScopeKind kind) => kind switch
    {
        TapestryScopeKind.Workspace => LiveScopeIdQuery(kind) + """ORDER BY "WorkspacePath" """,
        _ => LiveScopeIdQuery(kind) + """ORDER BY "SessionId" """,
    };

    public async Task<IReadOnlyList<TapestryScope>> DiscoverScopesAsync(
        bool includeWorkspace,
        bool includeSessionAttachments,
        bool includeSessions,
        CancellationToken cancellationToken)
    {
        List<TapestryScope> scopes = [];

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        foreach (TapestryScopeKind kind in EnabledKinds(
            includeWorkspace,
            includeSessionAttachments,
            includeSessions))
        {
            await CollectScopesAsync(
                connection,
                OrderedLiveScopeIdQuery(kind),
                kind,
                scopes,
                cancellationToken).ConfigureAwait(false);
        }

        return scopes;
    }

    /// <summary>
    /// The corpora this sweep participates in. A disabled corpus is absent, which is what keeps
    /// <see cref="PruneRemovedScopesAsync"/> from treating "the operator turned this feature off" as
    /// "these scopes are gone".
    /// </summary>
    private static IEnumerable<TapestryScopeKind> EnabledKinds(
        bool includeWorkspace,
        bool includeSessionAttachments,
        bool includeSessions)
    {
        if (includeWorkspace)
        {
            yield return TapestryScopeKind.Workspace;
        }

        if (includeSessionAttachments)
        {
            yield return TapestryScopeKind.SessionAttachment;
        }

        if (includeSessions)
        {
            yield return TapestryScopeKind.Session;
        }
    }

    public Task<int> PruneRemovedScopesAsync(
        bool includeWorkspace,
        bool includeSessionAttachments,
        bool includeSessions,
        CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                int removed = 0;

                foreach (TapestryScopeKind kind in EnabledKinds(
                    includeWorkspace,
                    includeSessionAttachments,
                    includeSessions))
                {
                    // The diff runs set-wise in SQLite against the same source the sweep discovers from,
                    // rather than round-tripping the live scope list into a parameter per session. Only
                    // this kind is touched, so a disabled corpus keeps its trees untouched and needs no
                    // rebuild when the operator turns it back on.
                    string kindName = kind.ToString();

                    removed += await DeleteGenerationsAsync(
                        connection,
                        transaction,
                        $"""
                        "ScopeKind" = @scopeKind
                        AND "ScopeId" NOT IN ({LiveScopeIdQuery(kind)})
                        """,
                        command => AddParameter(command, "@scopeKind", kindName),
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return removed;
            },
            cancellationToken);

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

    /// <summary>
    /// Rows read or stored per page. It bounds one query's result and one hash-storing transaction, not a
    /// scan's total work.
    /// </summary>
    internal const int LeafPageSize = 512;

    /// <summary>
    /// Where each scope kind's leaves live: the corpus table, its id and label columns, the predicate that
    /// puts a row in one scope (written against the alias <c>s</c>), and the table its already-imprinted
    /// vectors sit in. Every identifier is a constant of this type, never input.
    /// </summary>
    private sealed record LeafCorpus(
        TapestryLeafSourceKind Kind,
        string Table,
        string IdColumn,
        string LabelColumn,
        string ScopePredicate,
        string EmbeddingTable,
        string EmbeddingKeyColumn)
    {
        internal static LeafCorpus For(TapestryScopeKind kind) => kind switch
        {
            TapestryScopeKind.Workspace => new(
                TapestryLeafSourceKind.WorkspaceFileChunk,
                "\"workspace_file_chunks\"",
                "\"ChunkId\"",
                "\"RelativePath\"",
                "s.\"WorkspacePath\" = @scopeId",
                "\"workspace_file_embeddings\"",
                "\"ChunkId\""),

            TapestryScopeKind.SessionAttachment => new(
                TapestryLeafSourceKind.SessionAttachmentChunk,
                "\"session_attachment_chunks\"",
                "\"ChunkId\"",
                "\"OriginalFileName\"",
                "s.\"SessionId\" = @scopeId AND s.\"RetrievalScope\" IS NOT NULL",
                "\"session_attachment_embeddings\"",
                "\"ChunkId\""),

            TapestryScopeKind.Session => new(
                TapestryLeafSourceKind.Entry,
                "\"Entries\"",
                "\"Id\"",
                "\"Role\"",
                "s.\"SessionId\" = @scopeId",
                "\"entry_embeddings\"",
                "\"EntryId\""),

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Tapestry scope kind."),
        };
    }

    /// <summary>One corpus row and the hash stored beside it, if any.</summary>
    private sealed record StoredLeafHash(string SourceId, string? ContentSha256, bool IsBlank);

    public async Task<TapestryCorpusIdentity> GetCorpusIdentityAsync(
        TapestryScope scope,
        int maxLeaves,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLeaves, 1);

        LeafCorpus corpus = LeafCorpus.For(scope.Kind);

        // One read of ids and stored hashes, never of text, and never more than one row past the ceiling:
        // a scope that exceeds it is answered by counting, before any row is read or hashed.
        List<StoredLeafHash> rows = await ReadStoredLeafHashesAsync(
            corpus,
            scope.Id,
            checked(maxLeaves + 1),
            cancellationToken).ConfigureAwait(false);

        if (rows.Count > maxLeaves)
        {
            return new TapestryCorpusIdentity(rows.Count, string.Empty, ExceedsCeiling: true);
        }

        // The rows with no stored hash yet are the only ones whose text is read: the first sweep after an
        // upgrade, and anything inserted or rewritten since. Each page is its own short transaction.
        List<string> missing = [.. rows.Where(static row => row.ContentSha256 is null).Select(static row => row.SourceId)];

        Dictionary<string, StoredLeafHash> stored = new(StringComparer.Ordinal);

        for (int offset = 0; offset < missing.Count; offset += LeafPageSize)
        {
            IReadOnlyList<StoredLeafHash> page = await StoreLeafHashesAsync(
                corpus,
                missing.GetRange(offset, Math.Min(LeafPageSize, missing.Count - offset)),
                cancellationToken).ConfigureAwait(false);

            foreach (StoredLeafHash row in page)
            {
                stored[row.SourceId] = row;
            }
        }

        List<(string SourceId, string ContentHash)> leaves = new(rows.Count);

        foreach (StoredLeafHash row in rows)
        {
            // A row that vanished before it could be read has nothing left to fingerprint.
            StoredLeafHash? known = row.ContentSha256 is null
                ? stored.GetValueOrDefault(row.SourceId)
                : row;

            if (known?.ContentSha256 is { } hash && !known.IsBlank)
            {
                leaves.Add((known.SourceId, hash));
            }
        }

        return new TapestryCorpusIdentity(leaves.Count, TapestryHash.OfCorpus(leaves), ExceedsCeiling: false);
    }

    /// <summary>The scope's row ids with whatever hash is stored for each, at most <paramref name="limit"/> of them.</summary>
    private async Task<List<StoredLeafHash>> ReadStoredLeafHashesAsync(
        LeafCorpus corpus,
        string scopeId,
        int limit,
        CancellationToken cancellationToken)
    {
        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT s.{corpus.IdColumn}, h."ContentSha256", h."IsBlank"
            FROM {corpus.Table} s
            LEFT JOIN "tapestry_leaf_hashes" h
                ON h."SourceKind" = @sourceKind AND h."SourceId" = s.{corpus.IdColumn}
            WHERE {corpus.ScopePredicate}
            LIMIT @limit
            """;

        AddParameter(command, "@sourceKind", corpus.Kind.ToString());

        AddParameter(command, "@scopeId", scopeId);

        AddParameter(command, "@limit", limit);

        List<StoredLeafHash> rows = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new StoredLeafHash(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) != 0));
        }

        return rows;
    }

    /// <summary>
    /// Reads, hashes and stores the hash of each listed row's text, in one transaction, and returns what it
    /// stored. A row that no longer exists is absent from the result.
    /// </summary>
    /// <remarks>
    /// The read and the write share a transaction, so a writer cannot change a row between the two: SQLite
    /// refuses to upgrade a read snapshot another writer has since moved past, and the busy retry runs the
    /// page again from its read. That is what keeps a stored hash from ever describing text the row no longer
    /// holds, which the drop triggers on the corpus tables can only guarantee for what happens after the
    /// store.
    /// </remarks>
    private Task<IReadOnlyList<StoredLeafHash>> StoreLeafHashesAsync(
        LeafCorpus corpus,
        IReadOnlyList<string> sourceIds,
        CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync<IReadOnlyList<StoredLeafHash>>(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                await using DbCommand read = connection.CreateCommand();

                read.Transaction = transaction;

                read.CommandText = $"SELECT s.\"Content\" FROM {corpus.Table} s WHERE s.{corpus.IdColumn} = @sourceId";

                DbParameter readId = read.CreateParameter();

                readId.ParameterName = "@sourceId";

                read.Parameters.Add(readId);

                await using DbCommand write = connection.CreateCommand();

                write.Transaction = transaction;

                write.CommandText =
                    """
                    INSERT OR REPLACE INTO "tapestry_leaf_hashes" ("SourceKind", "SourceId", "ContentSha256", "IsBlank")
                    VALUES (@sourceKind, @sourceId, @contentSha256, @isBlank)
                    """;

                AddParameter(write, "@sourceKind", corpus.Kind.ToString());

                DbParameter writeId = write.CreateParameter();

                writeId.ParameterName = "@sourceId";

                write.Parameters.Add(writeId);

                DbParameter writeHash = write.CreateParameter();

                writeHash.ParameterName = "@contentSha256";

                write.Parameters.Add(writeHash);

                DbParameter writeBlank = write.CreateParameter();

                writeBlank.ParameterName = "@isBlank";

                write.Parameters.Add(writeBlank);

                List<StoredLeafHash> stored = new(sourceIds.Count);

                foreach (string sourceId in sourceIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    readId.Value = sourceId;

                    object? text = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                    if (text is null)
                    {
                        continue;
                    }

                    string content = text as string ?? string.Empty;

                    string hash = TapestryHash.OfContent(content);

                    bool blank = content.Trim().Length == 0;

                    writeId.Value = sourceId;

                    writeHash.Value = hash;

                    writeBlank.Value = blank ? 1 : 0;

                    _ = await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    stored.Add(new StoredLeafHash(sourceId, hash, blank));
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return stored;
            },
            cancellationToken);

    public async IAsyncEnumerable<IReadOnlyList<TapestryLeafSource>> EnumerateLeafPagesAsync(
        TapestryScope scope,
        int expectedDimensions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LeafCorpus corpus = LeafCorpus.For(scope.Kind);

        IReadOnlyList<string> ids = await ListLeafIdsAsync(corpus, scope.Id, cancellationToken).ConfigureAwait(false);

        for (int offset = 0; offset < ids.Count; offset += LeafPageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<TapestryLeafSource> page = await ReadLeafPageAsync(
                corpus,
                scope.Id,
                [.. ids.Skip(offset).Take(LeafPageSize)],
                expectedDimensions,
                cancellationToken).ConfigureAwait(false);

            if (page.Count > 0)
            {
                yield return page;
            }
        }
    }

    /// <summary>The scope's row ids in stable id order, and nothing else of them.</summary>
    private async Task<IReadOnlyList<string>> ListLeafIdsAsync(
        LeafCorpus corpus,
        string scopeId,
        CancellationToken cancellationToken)
    {
        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT s.{corpus.IdColumn}
            FROM {corpus.Table} s
            WHERE {corpus.ScopePredicate}
            ORDER BY s.{corpus.IdColumn}
            """;

        AddParameter(command, "@scopeId", scopeId);

        List<string> ids = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>
    /// One page of leaves: the listed rows that are still in the scope, each joined to its own
    /// already-imprinted embedding so an unchanged leaf costs no embedding call on rebuild. A missing or
    /// wrong-dimension companion simply yields no vector and the builder embeds that leaf itself.
    /// </summary>
    private async Task<IReadOnlyList<TapestryLeafSource>> ReadLeafPageAsync(
        LeafCorpus corpus,
        string scopeId,
        IReadOnlyList<string> pageIds,
        int expectedDimensions,
        CancellationToken cancellationToken)
    {
        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        string[] placeholders =
        [
            .. Enumerable.Range(0, pageIds.Count)
                .Select(static index => "@id" + index.ToString(CultureInfo.InvariantCulture)),
        ];

        command.CommandText =
            $"""
            SELECT s.{corpus.IdColumn}, s.{corpus.LabelColumn}, s."Content", e."Embedding", e."Dim"
            FROM {corpus.Table} s
            LEFT JOIN {corpus.EmbeddingTable} e ON e.{corpus.EmbeddingKeyColumn} = s.{corpus.IdColumn}
            WHERE s.{corpus.IdColumn} IN ({string.Join(", ", placeholders)})
              AND {corpus.ScopePredicate}
            ORDER BY s.{corpus.IdColumn}
            """;

        for (int index = 0; index < pageIds.Count; index++)
        {
            AddParameter(command, placeholders[index], pageIds[index]);
        }

        AddParameter(command, "@scopeId", scopeId);

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

            if (!reader.IsDBNull(3)
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

                    // The switch is only atomic if it can tell that it did not happen. A generation that
                    // is not Building — already published, abandoned, or never begun — matches no row
                    // here, and the supersede above has already run in this transaction: committing would
                    // retire the scope's current generation and promote nothing. Throwing before the commit
                    // rolls the supersede back with it, so the current generation stays current.
                    int published = await publish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    if (published != 1)
                    {
                        throw new InvalidOperationException(
                            $"Tapestry generation {generationId} could not be published: it is not a Building generation, so nothing was switched.");
                    }
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
                    "\"GenerationId\" = @generationId AND \"Status\" = 'Building'",
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
    /// Deletes every <c>Session</c>-kind tree of one Session, in the caller's transaction, whatever each
    /// generation's status.
    /// </summary>
    /// <remarks>
    /// A Session tree is a model-written summary of that Session's entries, so it is derived from every
    /// entry an erasure removes. Both erasure paths call this inside the transaction that deletes the
    /// entry (the live erasure kernel, and the staged restore purge): a summary of the erased words must
    /// not stay retrievable behind a purge that reported success. The next sweep rebuilds the tree from
    /// what remains. The attachment tree of the same Session is not derived from its entries and is left
    /// alone.
    /// </remarks>
    /// <returns>The generations removed.</returns>
    internal static Task<int> DeleteSessionTreesAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken) =>
        DeleteSessionTreesByKeyAsync(
            connection,
            transaction,
            CovenantIdentitySql.Key(sessionId),
            cancellationToken);

    /// <summary>
    /// The same deletion for a Session named by its normalised identity (<see cref="CovenantIdentitySql.Key(string)"/>),
    /// compared against each tree's scope id normalised the same way.
    /// </summary>
    /// <remarks>
    /// The normalised comparison is what a staged archive needs: it is another installation's database, and
    /// the tree is keyed by whatever spelling that installation's <c>Entries.SessionId</c> held, which the
    /// label ledger naming the Session does not have to share. It is also safe on a live database, where it
    /// finds the one spelling the sweep writes. An empty key is refused outright, because a predicate keyed
    /// by an empty string matches every blank-keyed row rather than none.
    /// </remarks>
    internal static Task<int> DeleteSessionTreesByKeyAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sessionKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);

        return DeleteGenerationsAsync(
            connection,
            transaction,
            $"\"ScopeKind\" = 'Session' AND {CovenantIdentitySql.Keyed("\"ScopeId\"", "@sessionKey")}",
            command => AddParameter(command, "@sessionKey", sessionKey),
            cancellationToken);
    }

    /// <summary>
    /// Deletes matching generations. The optional vector mirror has no foreign key, so its rows are
    /// removed explicitly before the cascade takes the BLOB rows with the nodes.
    /// </summary>
    /// <remarks>
    /// The mirror holds the embedding itself, so whether it is deleted from is a property of the
    /// database, not of whether this process loaded an accelerator: a plain mirror an earlier build
    /// filled is deleted from whatever the flag says, and a legacy vec0 mirror this runtime cannot open
    /// is skipped rather than failing the delete it rides on.
    /// </remarks>
    private static async Task<int> DeleteGenerationsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string predicate,
        Action<DbCommand> bind,
        CancellationToken cancellationToken)
    {
        if (await SagaVectorMirror.ClassifyAsync(
                connection,
                transaction,
                TapestryStorageKeys.VectorTable,
                cancellationToken).ConfigureAwait(false) is SagaVectorMirrorKind.PlainTable)
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
            //
            // The attachment join repeats LeafCorpus's scope predicate: a superseded
            // attachment version keeps its rows and its bytes and only loses RetrievalScope, so the
            // stale-leaf hash guard below cannot see it. Without the predicate a version the operator
            // has already replaced would be injected as turn context. The predicate belongs in the ON
            // clause, not the WHERE: a summary node has no SourceId and must still hydrate.
            string leafJoin = generation.ScopeKind switch
            {
                TapestryScopeKind.Workspace =>
                    """LEFT JOIN "workspace_file_chunks" s ON s."ChunkId" = n."SourceId" """,

                TapestryScopeKind.SessionAttachment =>
                    """
                    LEFT JOIN "session_attachment_chunks" s
                        ON s."ChunkId" = n."SourceId" AND s."RetrievalScope" IS NOT NULL
                    """,

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
              AND (@sessionId IS NULL OR (
                    (g."ScopeKind" = 'Session' AND g."ScopeId" = @canonicalSessionId)
                    OR (g."ScopeKind" = 'SessionAttachment' AND g."ScopeId" = @sessionId)))
            ORDER BY g."ScopeKind", g."ScopeId"
            """;

        AddSessionScopeParameters(command, sessionId);

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
              AND (@sessionId IS NULL OR (
                    (g."ScopeKind" = 'Session' AND g."ScopeId" = @canonicalSessionId)
                    OR (g."ScopeKind" = 'SessionAttachment' AND g."ScopeId" = @sessionId)))
            """;

        AddSessionScopeParameters(command, sessionId);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Binds one Session under the two spellings its trees are keyed by. A Session tree is keyed by
    /// <c>Entries.SessionId</c> (uppercase) and an attachment tree by the chunk column (lowercase), so
    /// one parameter can never match both kinds; the spellings come from the same helpers retrieval
    /// builds its scopes with.
    /// </summary>
    private static void AddSessionScopeParameters(DbCommand command, Guid? sessionId)
    {
        AddParameter(
            command,
            "@sessionId",
            sessionId is { } attachment ? TapestryScope.ForSessionAttachment(attachment).Id : DBNull.Value);

        AddParameter(
            command,
            "@canonicalSessionId",
            sessionId is { } session ? TapestryScope.ForSession(session).Id : DBNull.Value);
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
        UtcInstantText.TryParse(value, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static string Iso(DateTimeOffset value) => UtcInstantText.Format(value);

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
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
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
