using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

/// <summary>
/// Generation lifecycle is The Tapestry's correctness core (DESIGN §21.11): a staging generation is
/// invisible, the current-generation switch is atomic, a failed build never replaces the last good
/// tree, and reconciliation removes everything that is not the current complete generation.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class TapestryStoreTests : IAsyncLifetime
{
    private const int TestDimensions = 8;

    private static readonly TapestryScope WorkspaceScope = new(TapestryScopeKind.Workspace, "/repo");

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private TapestryStore? _store;

    public TapestryStoreTests(GrimoireFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _store = new TapestryStore(_db, new WeaveIndexAvailability());

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static float[] Vec(params float[] leading)
    {
        float[] result = new float[TestDimensions];

        leading.AsSpan().CopyTo(result);

        return result;
    }

    private Task<string> BeginAsync(string corpusFingerprint = "corpus-1") =>
        _store!.BeginGenerationAsync(
            WorkspaceScope,
            SphericalKMeans.AlgorithmVersion,
            "settings-1",
            "fast",
            TapestryHash.SummaryRecipeVersion,
            TestDimensions,
            corpusFingerprint,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

    private static TapestryNodeWrite Leaf(
        string generationId,
        string nodeId,
        string sourceId,
        string content) =>
        new(
            new TapestryNode(
                nodeId,
                generationId,
                WorkspaceScope.Kind,
                WorkspaceScope.Id,
                0,
                TapestryNodeKind.Leaf,
                null,
                TapestryLeafSourceKind.WorkspaceFileChunk,
                sourceId,
                "a.cs",
                null,
                TapestryHash.OfContent(content),
                null,
                1,
                0,
                TapestryPartitionReason.None,
                TestDimensions,
                DateTimeOffset.UtcNow),
            Vec(1f));

    private static TapestryNodeWrite Summary(
        string generationId,
        string nodeId,
        string content,
        string membershipHash,
        int descendants) =>
        new(
            new TapestryNode(
                nodeId,
                generationId,
                WorkspaceScope.Kind,
                WorkspaceScope.Id,
                1,
                TapestryNodeKind.Summary,
                null,
                null,
                null,
                "summary",
                content,
                TapestryHash.OfContent(content),
                membershipHash,
                descendants,
                0,
                TapestryPartitionReason.None,
                TestDimensions,
                DateTimeOffset.UtcNow),
            Vec(0f, 1f));

    /// <summary>Every leaf of a scope, collected from the pages the store streams them in.</summary>
    private async Task<IReadOnlyList<TapestryLeafSource>> EnumerateLeavesAsync(TapestryScope scope)
    {
        List<TapestryLeafSource> leaves = [];

        await foreach (IReadOnlyList<TapestryLeafSource> page in _store!.EnumerateLeafPagesAsync(
            scope,
            TestDimensions,
            CancellationToken.None))
        {
            leaves.AddRange(page);
        }

        return leaves;
    }

    private async Task SeedWorkspaceChunkAsync(string chunkId, string content)
    {
        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT OR REPLACE INTO workspace_file_chunks
                (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset, CharLength,
                 StartLine, EndLine, FileLastWriteTime, IndexedAt)
            VALUES (@chunkId, '/repo', 'a.cs', 0, @content, 0, 10, 1, 3,
                    '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
            """;

        DbParameter chunk = command.CreateParameter();

        chunk.ParameterName = "@chunkId";

        chunk.Value = chunkId;

        command.Parameters.Add(chunk);

        DbParameter body = command.CreateParameter();

        body.ParameterName = "@content";

        body.Value = content;

        command.Parameters.Add(body);

        _ = await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// One bound attachment and the chunk the tapestry reads from it, each column in the spelling its own
    /// writer renders.
    /// </summary>
    /// <remarks>
    /// The two <c>SessionId</c> columns here deliberately disagree, and that is what production holds.
    /// <c>SessionAttachments."SessionId"</c> is compared against <c>Sessions."Id"</c> by Covenant, backup
    /// and retention components, so it was moved onto the canonical spelling and is guarded there.
    /// <c>session_attachment_chunks.SessionId</c> was deliberately left in the minority form: the tapestry
    /// reads it as its live scope-id set and those values become <c>tapestry_nodes.ScopeId</c>, so moving
    /// it would orphan every attachment-scoped generation and rebuild the tree at provider cost. The
    /// scope this case builds is therefore keyed by the chunk's spelling, exactly as the store keys it.
    /// </remarks>
    private async Task SeedAttachmentChunkAsync(string sessionId, string chunkId, string content)
    {
        string attachmentId = Guid.NewGuid().ToString("D").ToUpperInvariant();

        await ExecuteAsync(
            """
            INSERT INTO "SessionAttachments"
                ("Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey",
                 "OriginalFileName", "Version", "RelativePath", "ContentSha256", "MimeType",
                 "ByteLength", "Kind", "CreatedAt")
            VALUES (@attachmentId, @sessionId, NULL, NULL, 'Bound', 'notes', 'notes.md', 1,
                    'notes.md', 'ATTACHMENT-HASH', 'text/plain', 16, 'Text',
                    '2026-01-01T00:00:00Z')
            """,
            ("@attachmentId", attachmentId),
            ("@sessionId", sessionId.ToUpperInvariant()));

        await ExecuteAsync(
            """
            INSERT OR REPLACE INTO session_attachment_chunks
                (ChunkId, GenerationId, SessionId, AttachmentId, LogicalKey, Version,
                 OriginalFileName, MimeType, ContentSha256, ChunkIndex, CharacterStart, CharacterEnd,
                 StartLine, EndLine, Content, EmbeddingDimension, ExtractedAt, IndexedAt,
                 RetrievalScope)
            VALUES (@chunkId, 'generation-1', @sessionId, @attachmentId, 'notes', 1, 'notes.md',
                    'text/plain', 'ATTACHMENT-HASH', 0, 0, 16, 1, 1, @content, @dimensions,
                    '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'Latest')
            """,
            ("@chunkId", chunkId),
            ("@sessionId", sessionId),
            ("@attachmentId", attachmentId),
            ("@content", content),
            ("@dimensions", TestDimensions));
    }

    private Task RetireAttachmentChunkAsync(string chunkId) =>
        ExecuteAsync(
            """UPDATE session_attachment_chunks SET RetrievalScope = NULL WHERE ChunkId = @chunkId""",
            ("@chunkId", chunkId));

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = name;

            parameter.Value = value;

            command.Parameters.Add(parameter);
        }

        _ = await command.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task BuildingGenerationIsInvisibleUntilPublished()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string generationId = await BeginAsync();

        await _store!.AppendNodesAsync([Leaf(generationId, "n1", "c1", "alpha")], CancellationToken.None);

        Assert.Null(await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None));

        await _store.PublishGenerationAsync(
            generationId,
            1,
            1,
            1,
            TapestryTerminalReason.LeafOnly,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        TapestryGeneration? current = await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None);

        Assert.NotNull(current);

        Assert.Equal(generationId, current.GenerationId);

        Assert.Equal(TapestryGenerationStatus.Complete, current.Status);

        Assert.Equal(TapestryTerminalReason.LeafOnly, current.TerminalReason);

        Assert.Equal(SphericalKMeans.AlgorithmVersion, current.AlgorithmVersion);
    }

    [SkippableFact]
    public async Task PublishingSupersedesExactlyOnePriorGeneration()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string first = await BeginAsync("corpus-1");

        await _store!.AppendNodesAsync([Leaf(first, "n1", "c1", "alpha")], CancellationToken.None);

        await _store.PublishGenerationAsync(
            first,
            1,
            1,
            1,
            TapestryTerminalReason.LeafOnly,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        string second = await BeginAsync("corpus-2");

        await _store.AppendNodesAsync([Leaf(second, "n2", "c1", "beta")], CancellationToken.None);

        await _store.PublishGenerationAsync(
            second,
            1,
            1,
            1,
            TapestryTerminalReason.LeafOnly,
            DateTimeOffset.UtcNow.AddSeconds(1),
            CancellationToken.None);

        TapestryGeneration? current = await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None);

        Assert.Equal(second, current!.GenerationId);

        Assert.Equal("corpus-2", current.CorpusFingerprint);
    }

    /// <summary>
    /// The atomic switch is only atomic if it can tell that it did not switch. Publishing a generation
    /// that is not <c>Building</c> — one already published, or one an abandonment removed — changes no
    /// row in its own statement, so the supersede that ran first in the same transaction would otherwise
    /// commit alone and leave the scope with no current generation at all.
    /// </summary>
    [SkippableFact]
    public async Task PublishingANonBuildingGenerationThrowsAndLeavesTheCurrentOneComplete()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string published = await BeginAsync("corpus-1");

        await _store!.AppendNodesAsync([Leaf(published, "n1", "c1", "alpha")], CancellationToken.None);

        await _store.PublishGenerationAsync(
            published,
            1,
            1,
            1,
            TapestryTerminalReason.LeafOnly,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        // Publishing the same generation a second time: it is Complete, not Building.
        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.PublishGenerationAsync(
                published,
                1,
                1,
                1,
                TapestryTerminalReason.LeafOnly,
                DateTimeOffset.UtcNow,
                CancellationToken.None));

        Assert.Equal(published, (await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None))?.GenerationId);

        // A generation that was abandoned and no longer exists.
        string abandoned = await BeginAsync("corpus-2");

        await _store.AbandonGenerationAsync(abandoned, CancellationToken.None);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.PublishGenerationAsync(
                abandoned,
                1,
                1,
                1,
                TapestryTerminalReason.LeafOnly,
                DateTimeOffset.UtcNow,
                CancellationToken.None));

        Assert.Equal(published, (await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None))?.GenerationId);
    }

    [SkippableFact]
    public async Task AbandoningAStagingGenerationLeavesTheCompleteOneCurrent()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string published = await BeginAsync("corpus-1");

        await _store!.AppendNodesAsync([Leaf(published, "n1", "c1", "alpha")], CancellationToken.None);

        await _store.PublishGenerationAsync(
            published,
            1,
            1,
            1,
            TapestryTerminalReason.LeafOnly,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        string failed = await BeginAsync("corpus-2");

        await _store.AppendNodesAsync([Leaf(failed, "n2", "c1", "beta")], CancellationToken.None);

        await _store.AbandonGenerationAsync(failed, CancellationToken.None);

        TapestryGeneration? current = await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None);

        Assert.Equal(published, current!.GenerationId);

        Assert.Empty(await _store.GetNodeEmbeddingsAsync(["n2"], CancellationToken.None));
    }

    [SkippableFact]
    public async Task AbandonAfterPublicationPreservesTheCompleteGenerationAndItsNodes()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string published = await BeginAsync();

        await _store!.AppendNodesAsync([Leaf(published, "published-leaf", "c1", "alpha")], CancellationToken.None);

        await _store.PublishGenerationAsync(published, 1, 1, 1, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow, CancellationToken.None);

        // Publication can commit before its caller observes cancellation or a completion failure.
        // Best-effort abandonment must not erase the generation readers already see as current.
        await _store.AbandonGenerationAsync(published, CancellationToken.None);

        await _store.AbandonGenerationAsync(published, CancellationToken.None);

        Assert.Equal(published, (await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None))?.GenerationId);

        Assert.Equal("published-leaf", Assert.Single(await _store.GetLayerNodesAsync(published, 0, CancellationToken.None)).NodeId);

        Assert.Contains("published-leaf", await _store.GetNodeEmbeddingsAsync(["published-leaf"], CancellationToken.None));
    }

    [SkippableFact]
    public async Task ReconcileRemovesEverythingThatIsNotTheCurrentCompleteGeneration()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string published = await BeginAsync("corpus-1");

        await _store!.AppendNodesAsync([Leaf(published, "n1", "c1", "alpha")], CancellationToken.None);

        await _store.PublishGenerationAsync(
            published,
            1,
            1,
            1,
            TapestryTerminalReason.LeafOnly,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        string interrupted = await BeginAsync("corpus-2");

        await _store.AppendNodesAsync([Leaf(interrupted, "n2", "c1", "beta")], CancellationToken.None);

        int removed = await _store.ReconcileGenerationsAsync(CancellationToken.None);

        Assert.Equal(1, removed);

        Assert.Equal(published, (await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None))!.GenerationId);

        Assert.Single(await _store.GetNodeEmbeddingsAsync(["n1", "n2"], CancellationToken.None));
    }

    /// <summary>
    /// Removing a generation empties its nodes' rows from a plain vector mirror whatever the accelerator
    /// flag says, and leaves the rows of a generation that stays.
    /// </summary>
    /// <remarks>
    /// The mirror holds the embedding itself, so a row left behind is a node's content left behind. The
    /// store's flag is off here, which is the shipping runtime, and the rows are seeded because no
    /// production path of this build writes them.
    /// </remarks>
    [SkippableFact]
    public async Task ReconcileGenerationsAsync_EmptiesThePlainVectorMirrorRowsOfARemovedGenerationWhileTheFlagIsOff()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string published = await BeginAsync("corpus-1");

        await _store!.AppendNodesAsync([Leaf(published, "n1", "c1", "alpha")], CancellationToken.None);

        await _store.PublishGenerationAsync(
            published,
            1,
            1,
            1,
            TapestryTerminalReason.LeafOnly,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        string interrupted = await BeginAsync("corpus-2");

        await _store.AppendNodesAsync([Leaf(interrupted, "n2", "c1", "beta")], CancellationToken.None);

        await ExecuteAsync(
            """CREATE TABLE "tapestry_node_embeddings_vec" ("NodeId" TEXT PRIMARY KEY, "Embedding" BLOB NOT NULL)""");

        await ExecuteAsync(
            """INSERT INTO "tapestry_node_embeddings_vec" ("NodeId", "Embedding") VALUES ('n1', X'0000803F'), ('n2', X'0000803F')""");

        Assert.Equal(1, await _store.ReconcileGenerationsAsync(CancellationToken.None));

        Assert.Equal(
            ["n1"],
            await ScalarsAsync("""SELECT "NodeId" FROM "tapestry_node_embeddings_vec" ORDER BY "NodeId" """));
    }

    /// <summary>
    /// A legacy <c>vec0</c> mirror this runtime cannot open is skipped, and removing the generation
    /// still succeeds.
    /// </summary>
    /// <remarks>
    /// An FTS5 virtual table stands in for it, because it records the same <c>CREATE VIRTUAL TABLE</c>
    /// text, which is all that classifying a mirror reads. The stand-in could be deleted from, so it
    /// still holding its row is what shows no statement reached it.
    /// </remarks>
    [SkippableFact]
    public async Task ReconcileGenerationsAsync_SkipsALegacyVirtualVectorMirrorWithoutFailing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string interrupted = await BeginAsync("corpus-2");

        await _store!.AppendNodesAsync([Leaf(interrupted, "n2", "c1", "beta")], CancellationToken.None);

        await ExecuteAsync(
            """CREATE VIRTUAL TABLE "tapestry_node_embeddings_vec" USING fts5("NodeId", "Embedding")""");

        await ExecuteAsync(
            """INSERT INTO "tapestry_node_embeddings_vec" ("NodeId", "Embedding") VALUES ('n2', 'x')""");

        Assert.Equal(1, await _store.ReconcileGenerationsAsync(CancellationToken.None));

        Assert.Empty(await _store.GetNodeEmbeddingsAsync(["n2"], CancellationToken.None));

        Assert.Equal(
            ["n2"],
            await ScalarsAsync("""SELECT "NodeId" FROM "tapestry_node_embeddings_vec" """));
    }

    private async Task<List<string>> ScalarsAsync(string sql)
    {
        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        List<string> values = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    [SkippableFact]
    public async Task SummariesAreOfferedForReuseByExactChildMembership()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string generationId = await BeginAsync();

        string membership = TapestryHash.OfChildMembership(
            ["h1", "h2"],
            TapestryHash.SummaryRecipeVersion,
            "fast",
            "embedding-model");

        await _store!.AppendNodesAsync(
            [Summary(generationId, "s1", "the summary", membership, 2)],
            CancellationToken.None);

        await _store.PublishGenerationAsync(
            generationId,
            2,
            1,
            1,
            TapestryTerminalReason.SingleRoot,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        TapestrySummaryReuseCandidate? candidate = await _store.TryGetReusableSummaryAsync(
            WorkspaceScope,
            membership,
            CancellationToken.None);

        Assert.NotNull(candidate);

        Assert.Equal("the summary", candidate.Content);

        Assert.Equal(TestDimensions, candidate.Embedding.Length);

        Assert.Null(await _store.TryGetReusableSummaryAsync(
            WorkspaceScope,
            TapestryHash.OfChildMembership(["h1", "h3"], TapestryHash.SummaryRecipeVersion, "fast", "embedding-model"),
            CancellationToken.None));
    }

    [SkippableFact]
    public async Task HydrationResolvesLeafContentFromTheCorpusRow()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedWorkspaceChunkAsync("c1", "public sealed class Alpha { }");

        string generationId = await BeginAsync();

        await _store!.AppendNodesAsync(
            [Leaf(generationId, "n1", "c1", "public sealed class Alpha { }")],
            CancellationToken.None);

        await _store.PublishGenerationAsync(
            generationId,
            1,
            1,
            1,
            TapestryTerminalReason.LeafOnly,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        TapestryGeneration current = (await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None))!;

        IReadOnlyList<TapestryRetrievedNode> hydrated = await _store.HydrateRetrievedNodesAsync(
            current,
            [("n1", 0.9f)],
            TapestryRetrievalMode.CollapsedTree,
            CancellationToken.None);

        TapestryRetrievedNode node = Assert.Single(hydrated);

        Assert.Equal("public sealed class Alpha { }", node.Content);

        Assert.Equal("a.cs", node.SourceLabel);

        Assert.Equal(TapestryNodeKind.Leaf, node.NodeKind);

        Assert.Equal(0.9f, node.Similarity);
    }

    [SkippableFact]
    public async Task HydrationDropsALeafWhoseSourceChangedSinceTheGenerationWasBuilt()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedWorkspaceChunkAsync("c1", "original");

        string generationId = await BeginAsync();

        await _store!.AppendNodesAsync([Leaf(generationId, "n1", "c1", "original")], CancellationToken.None);

        await _store.PublishGenerationAsync(
            generationId,
            1,
            1,
            1,
            TapestryTerminalReason.LeafOnly,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await SeedWorkspaceChunkAsync("c1", "edited out from under the tree");

        TapestryGeneration current = (await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None))!;

        IReadOnlyList<TapestryRetrievedNode> hydrated = await _store.HydrateRetrievedNodesAsync(
            current,
            [("n1", 0.9f)],
            TapestryRetrievalMode.CollapsedTree,
            CancellationToken.None);

        Assert.Empty(hydrated);
    }

    [SkippableFact]
    public async Task HydrationDropsAnAttachmentLeafWhoseVersionHasBeenSuperseded()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string sessionId = Guid.NewGuid().ToString();

        TapestryScope attachmentScope = new(TapestryScopeKind.SessionAttachment, sessionId);

        const string ChunkId = "attachment-1:generation-1:0";

        const string Body = "the note body as it stood in version one";

        await SeedAttachmentChunkAsync(sessionId, ChunkId, Body);

        string generationId = await _store!.BeginGenerationAsync(
            attachmentScope,
            SphericalKMeans.AlgorithmVersion,
            "settings-1",
            "fast",
            TapestryHash.SummaryRecipeVersion,
            TestDimensions,
            "corpus-1",
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await _store.AppendNodesAsync(
            [
                new TapestryNodeWrite(
                    new TapestryNode(
                        "n1",
                        generationId,
                        attachmentScope.Kind,
                        attachmentScope.Id,
                        0,
                        TapestryNodeKind.Leaf,
                        null,
                        TapestryLeafSourceKind.SessionAttachmentChunk,
                        ChunkId,
                        "notes.md",
                        null,
                        TapestryHash.OfContent(Body),
                        null,
                        1,
                        0,
                        TapestryPartitionReason.None,
                        TestDimensions,
                        DateTimeOffset.UtcNow),
                    Vec(1f)),
            ],
            CancellationToken.None);

        await _store.PublishGenerationAsync(
            generationId,
            1,
            1,
            1,
            TapestryTerminalReason.LeafOnly,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        TapestryGeneration current = (await _store.GetCurrentGenerationAsync(
            attachmentScope,
            CancellationToken.None))!;

        // A newer attachment version soft-retires the prior one: the old rows keep their bytes and
        // only lose RetrievalScope, which is exactly what removes them from flat attachment RAG. The
        // tree is woven from the same scoped corpus, so hydration must honour the same predicate —
        // otherwise a superseded version is injected as turn context under an unchanged hash.
        await RetireAttachmentChunkAsync(ChunkId);

        IReadOnlyList<TapestryRetrievedNode> hydrated = await _store.HydrateRetrievedNodesAsync(
            current,
            [("n1", 0.9f)],
            TapestryRetrievalMode.CollapsedTree,
            CancellationToken.None);

        Assert.Empty(hydrated);
    }

    [SkippableFact]
    public async Task HydrationCarriesTheCompleteAncestorChain()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedWorkspaceChunkAsync("c1", "leaf body");

        string generationId = await BeginAsync();

        await _store!.AppendNodesAsync(
            [
                Leaf(generationId, "n1", "c1", "leaf body"),
                Summary(generationId, "s1", "layer one", "m1", 1),
            ],
            CancellationToken.None);

        await _store.AppendNodesAsync(
            [
                Summary(generationId, "s2", "layer two", "m2", 1) with
                {
                    Node = Summary(generationId, "s2", "layer two", "m2", 1).Node with { Layer = 2 },
                },
            ],
            CancellationToken.None);

        await _store.SetParentAsync(generationId, "s1", ["n1"], CancellationToken.None);

        await _store.SetParentAsync(generationId, "s2", ["s1"], CancellationToken.None);

        await _store.PublishGenerationAsync(
            generationId,
            3,
            3,
            1,
            TapestryTerminalReason.SingleRoot,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        TapestryGeneration current = (await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None))!;

        IReadOnlyList<TapestryRetrievedNode> hydrated = await _store.HydrateRetrievedNodesAsync(
            current,
            [("n1", 0.9f)],
            TapestryRetrievalMode.CollapsedTree,
            CancellationToken.None);

        Assert.Equal(["s1", "s2"], Assert.Single(hydrated).AncestorNodeIds);
    }

    [SkippableFact]
    public async Task LeafEnumerationReusesTheAlreadyImprintedWorkspaceEmbedding()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedWorkspaceChunkAsync("c1", "chunk body");

        DbConnection connection = _db!.Database.GetDbConnection();

        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT OR REPLACE INTO workspace_file_embeddings (ChunkId, Embedding, Dim)
                VALUES ('c1', @embedding, @dim)
                """;

            DbParameter embedding = command.CreateParameter();

            embedding.ParameterName = "@embedding";

            embedding.Value = RetroDownfall.Arcanum.Core.Primitives.EmbeddingBlobCodec.Encode(Vec(0.5f));

            command.Parameters.Add(embedding);

            DbParameter dimension = command.CreateParameter();

            dimension.ParameterName = "@dim";

            dimension.Value = TestDimensions;

            command.Parameters.Add(dimension);

            _ = await command.ExecuteNonQueryAsync();
        }

        IReadOnlyList<TapestryLeafSource> leaves = await EnumerateLeavesAsync(WorkspaceScope);

        TapestryLeafSource leaf = Assert.Single(leaves);

        Assert.Equal("c1", leaf.SourceId);

        Assert.Equal("a.cs", leaf.Label);

        Assert.Equal(TapestryHash.OfContent("chunk body"), leaf.ContentHash);

        Assert.NotNull(leaf.ExistingEmbedding);

        Assert.Equal(0.5f, leaf.ExistingEmbedding![0]);
    }

    [SkippableFact]
    public async Task LeafEnumerationIgnoresAWrongDimensionEmbedding()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedWorkspaceChunkAsync("c1", "chunk body");

        DbConnection connection = _db!.Database.GetDbConnection();

        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT OR REPLACE INTO workspace_file_embeddings (ChunkId, Embedding, Dim)
                VALUES ('c1', @embedding, 4)
                """;

            DbParameter embedding = command.CreateParameter();

            embedding.ParameterName = "@embedding";

            embedding.Value = RetroDownfall.Arcanum.Core.Primitives.EmbeddingBlobCodec.Encode(new float[4]);

            command.Parameters.Add(embedding);

            _ = await command.ExecuteNonQueryAsync();
        }

        IReadOnlyList<TapestryLeafSource> leaves = await EnumerateLeavesAsync(WorkspaceScope);

        Assert.Null(Assert.Single(leaves).ExistingEmbedding);
    }

    [SkippableFact]
    public async Task DiscoverScopesHonoursPerCorpusParticipation()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedWorkspaceChunkAsync("c1", "chunk body");

        IReadOnlyList<TapestryScope> all = await _store!.DiscoverScopesAsync(
            includeWorkspace: true,
            includeSessionAttachments: true,
            includeSessions: true,
            CancellationToken.None);

        Assert.Contains(all, scope => scope.Kind == TapestryScopeKind.Workspace && scope.Id == "/repo");

        IReadOnlyList<TapestryScope> withoutWorkspace = await _store.DiscoverScopesAsync(
            includeWorkspace: false,
            includeSessionAttachments: true,
            includeSessions: true,
            CancellationToken.None);

        Assert.DoesNotContain(withoutWorkspace, scope => scope.Kind == TapestryScopeKind.Workspace);
    }

    /// <summary>
    /// A Session's own history tree is found by the scope retrieval builds for that Session.
    /// </summary>
    /// <remarks>
    /// <c>Entries.SessionId</c> is guaranteed uppercase-dashed, so the sweep keys a Session-kind tree
    /// under that spelling; <c>session_attachment_chunks.SessionId</c> is deliberately lowercase, so the
    /// attachment kind is keyed under that one. The two kinds therefore disagree about how one Session
    /// is spelled, and the scopes the turn builds have to agree with each kind separately. The Entry is
    /// written through the object-relational writer production uses, and the scopes the turn reads are
    /// built by the provider's own <c>BuildTapestryScopes</c>, so neither side is spelled by the test.
    /// </remarks>
    [SkippableFact]
    public async Task Session_scope_generation_is_found_by_the_scope_the_retrieval_path_builds()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await SeedSessionEntryAsync(sessionId, "the session history the tree summarizes");

        await SeedAttachmentChunkAsync(sessionId.ToString("D"), "attachment-chunk", "attachment body");

        IReadOnlyList<TapestryScope> discovered = await _store!.DiscoverScopesAsync(
            includeWorkspace: false,
            includeSessionAttachments: true,
            includeSessions: true,
            CancellationToken.None);

        TapestryScope sessionScope = Assert.Single(discovered, static scope => scope.Kind == TapestryScopeKind.Session);

        TapestryScope attachmentScope = Assert.Single(discovered, static scope => scope.Kind == TapestryScopeKind.SessionAttachment);

        foreach (TapestryScope scope in new[] { sessionScope, attachmentScope })
        {
            string generationId = await _store.BeginGenerationAsync(
                scope,
                SphericalKMeans.AlgorithmVersion,
                "settings-1",
                "fast",
                TapestryHash.SummaryRecipeVersion,
                TestDimensions,
                "corpus-1",
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            await _store.AppendNodesAsync(
                [LeafIn(scope, generationId, $"node-{scope.Kind}", $"source-{scope.Kind}", "body")],
                CancellationToken.None);

            await _store.PublishGenerationAsync(
                generationId,
                1,
                1,
                1,
                TapestryTerminalReason.LeafOnly,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        TapestryEmbeddingSettings settings = new()
        {
            SessionTreesEnabled = true,
            SessionAttachmentTreesEnabled = true,
            WorkspaceTreesEnabled = false,
        };

        List<TapestryScope> retrievalScopes = WizardIntelligenceProvider.BuildTapestryScopes(
            new PingRequest("hi", SessionId: sessionId),
            settings,
            new UnusedIndexingService());

        Assert.Equal(2, retrievalScopes.Count);

        foreach (TapestryScope retrieval in retrievalScopes)
        {
            TapestryGeneration? current = await _store.GetCurrentGenerationAsync(retrieval, CancellationToken.None);

            Assert.True(
                current is not null,
                $"retrieval built {retrieval.Kind} scope '{retrieval.Id}' but the sweep keyed it under "
                + $"'{(retrieval.Kind == TapestryScopeKind.Session ? sessionScope.Id : attachmentScope.Id)}'");
        }

        // The published nodes are counted for that Session too, per kind spelling.
        Assert.Equal(2, await _store.CountPublishedNodesAsync(sessionId, CancellationToken.None));

        Assert.Equal(2, (await _store.GetScopeStatusesAsync(sessionId, CancellationToken.None)).Count);
    }

    private static TapestryNodeWrite LeafIn(
        TapestryScope scope,
        string generationId,
        string nodeId,
        string sourceId,
        string content) =>
        new(
            new TapestryNode(
                nodeId,
                generationId,
                scope.Kind,
                scope.Id,
                0,
                TapestryNodeKind.Leaf,
                null,
                scope.Kind == TapestryScopeKind.Session
                    ? TapestryLeafSourceKind.Entry
                    : TapestryLeafSourceKind.SessionAttachmentChunk,
                sourceId,
                "label",
                null,
                TapestryHash.OfContent(content),
                null,
                1,
                0,
                TapestryPartitionReason.None,
                TestDimensions,
                DateTimeOffset.UtcNow),
            Vec(1f));

    /// <summary>
    /// A Session is a scope when it has at least one Entry with text, whichever Entry that is: blank
    /// Entries do not make a Session, and do not stop a later one from doing so.
    /// </summary>
    [SkippableFact]
    public async Task Session_scopes_are_the_Sessions_with_at_least_one_non_blank_Entry()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid blankFirst = Guid.NewGuid();

        Guid onlyBlank = Guid.NewGuid();

        Guid plain = Guid.NewGuid();

        await SeedSessionEntriesAsync(blankFirst, "   ", string.Empty, "the one Entry that has text");

        await SeedSessionEntriesAsync(onlyBlank, " ", string.Empty, "  ");

        await SeedSessionEntriesAsync(plain, "ordinary text");

        IReadOnlyList<TapestryScope> discovered = await _store!.DiscoverScopesAsync(
            includeWorkspace: false,
            includeSessionAttachments: false,
            includeSessions: true,
            CancellationToken.None);

        Assert.Equal(
            new[] { blankFirst, plain }
                .Select(static id => id.ToString("D").ToUpperInvariant())
                .Order(StringComparer.Ordinal),
            discovered.Select(static scope => scope.Id));
    }

    /// <summary>
    /// Finding the Sessions must not read every Entry's text. The sweep runs this every tick whether or not
    /// anything changed, so the plan matters: the Sessions come from an index that holds no text, and each
    /// is then checked by a probe that stops at its first non-blank Entry, instead of one pass that
    /// fetches every Entry row to test its content.
    /// </summary>
    [SkippableFact]
    public async Task Session_scope_discovery_probes_each_Session_instead_of_reading_every_Entry()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + TapestryStore.LiveScopeIdQuery(TapestryScopeKind.Session);

        List<string> plan = [];

        await using (DbDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(3));
            }
        }

        string described = string.Join(" | ", plan);

        Assert.Contains("COVERING INDEX", described, StringComparison.Ordinal);

        Assert.Contains("EXISTS", described, StringComparison.Ordinal);
    }

    /// <summary>One Session with several Entries, in order, written through the object-relational writer.</summary>
    private async Task SeedSessionEntriesAsync(Guid sessionId, params string[] contents)
    {
        _db!.Sessions.Add(new Session
        {
            Id = sessionId,
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        for (int index = 0; index < contents.Length; index++)
        {
            _db.Entries.Add(new Entry
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = contents[index],
                ModelUsed = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
                Sequence = index + 1,
            });
        }

        _ = await _db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>One Session with one Entry, written through the object-relational writer.</summary>
    private async Task SeedSessionEntryAsync(Guid sessionId, string content)
    {
        _db!.Sessions.Add(new Session
        {
            Id = sessionId,
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        _db.Entries.Add(new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Role = MessageRole.User,
            Content = content,
            ModelUsed = "test-model",
            CreatedAt = DateTimeOffset.UtcNow,
            Sequence = 1,
        });

        _ = await _db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// The scopes under test carry no working directory, so the indexing boundary must never be asked.
    /// </summary>
    private sealed class UnusedIndexingService : IWorkspaceIndexingService
    {
        public void RegisterWorkspace(string workspacePath) => throw new NotSupportedException();

        public void UnregisterWorkspace(string workspacePath) => throw new NotSupportedException();

        public string ResolveIndexedWorkspacePath(string workspacePath) => throw new NotSupportedException();

        public Result<WorkspaceIndexQueueDisposition> QueueIndexNow(string workspacePath) =>
            throw new NotSupportedException();
    }

    [SkippableFact]
    public async Task StatusesReportOnlyPublishedGenerations()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string generationId = await BeginAsync();

        await _store!.AppendNodesAsync(
            [
                Leaf(generationId, "n1", "c1", "alpha"),
                Summary(generationId, "s1", "roll-up", "m1", 1),
            ],
            CancellationToken.None);

        Assert.Empty(await _store.GetScopeStatusesAsync(null, CancellationToken.None));

        Assert.Equal(0, await _store.CountPublishedNodesAsync(null, CancellationToken.None));

        await _store.PublishGenerationAsync(
            generationId,
            2,
            2,
            1,
            TapestryTerminalReason.SingleRoot,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        TapestryScopeStatus status = Assert.Single(
            await _store.GetScopeStatusesAsync(null, CancellationToken.None));

        Assert.Equal(TapestryScopeKind.Workspace, status.ScopeKind);

        Assert.Equal(2, status.LayerCount);

        Assert.Equal(1, status.LeafCount);

        Assert.Equal(1, status.SummaryCount);

        Assert.Equal(TapestryTerminalReason.SingleRoot, status.TerminalReason);

        Assert.Equal(2, await _store.CountPublishedNodesAsync(null, CancellationToken.None));
    }

    [SkippableFact]
    public async Task TerminalLayerReflectsTheHighestWrittenLayer()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string generationId = await BeginAsync();

        await _store!.AppendNodesAsync(
            [
                Leaf(generationId, "n1", "c1", "alpha"),
                Summary(generationId, "s1", "roll-up", "m1", 1),
            ],
            CancellationToken.None);

        Assert.Equal(1, await _store.GetTerminalLayerAsync(generationId, CancellationToken.None));
    }

    /// <summary>
    /// Reconciliation preserves each scope's current complete generation, so a scope that disappears
    /// outright keeps its whole tree forever: it is never rediscovered, never rebuilt, never
    /// superseded, and never read again. Pruning is what bounds the store by the data that exists.
    /// </summary>
    [SkippableFact]
    public async Task PruneRemovedScopesAsync_removes_the_published_tree_of_a_scope_that_no_longer_exists()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string published = await BeginAsync();

        await _store!.AppendNodesAsync([Leaf(published, "n1", "c1", "alpha")], CancellationToken.None);

        await _store.PublishGenerationAsync(
            published,
            1,
            1,
            1,
            TapestryTerminalReason.LeafOnly,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        // The workspace scope was never backed by workspace_file_chunks rows, so it is exactly the
        // shape of a scope whose source data has been deleted.
        Assert.Empty(await _store.DiscoverScopesAsync(true, true, true, CancellationToken.None));

        // Reconciliation is not enough on its own: the generation is Complete, so it is preserved.
        Assert.Equal(0, await _store.ReconcileGenerationsAsync(CancellationToken.None));

        Assert.NotNull(await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None));

        int pruned = await _store.PruneRemovedScopesAsync(true, true, true, CancellationToken.None);

        Assert.Equal(1, pruned);

        Assert.Null(await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None));

        // The nodes and their embeddings go with the generation rather than being left orphaned.
        Assert.Empty(await _store.GetNodeEmbeddingsAsync(["n1"], CancellationToken.None));

        Assert.Equal(0, await _store.CountPublishedNodesAsync(null, CancellationToken.None));
    }

    /// <summary>
    /// The corpus flags are a safety boundary, not a filter. Treating "the operator switched session
    /// trees off" as "every session scope is gone" would destroy derived data whose every summary is a
    /// billed model call, the moment a feature flag flips.
    /// </summary>
    [SkippableFact]
    public async Task PruneRemovedScopesAsync_leaves_a_disabled_corpus_untouched()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string published = await BeginAsync();

        await _store!.AppendNodesAsync([Leaf(published, "n1", "c1", "alpha")], CancellationToken.None);

        await _store.PublishGenerationAsync(
            published,
            1,
            1,
            1,
            TapestryTerminalReason.LeafOnly,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        int pruned = await _store.PruneRemovedScopesAsync(
            includeWorkspace: false,
            includeSessionAttachments: true,
            includeSessions: true,
            CancellationToken.None);

        Assert.Equal(0, pruned);

        Assert.NotNull(await _store.GetCurrentGenerationAsync(WorkspaceScope, CancellationToken.None));
    }
}
