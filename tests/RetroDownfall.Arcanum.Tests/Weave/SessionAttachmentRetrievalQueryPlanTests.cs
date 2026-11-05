using Microsoft.Data.Sqlite;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed class SessionAttachmentRetrievalQueryPlanTests
{
    private const string IndexName = "idx_session_attachment_chunks_retrieval_scope";

    private static readonly Guid SessionId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    static SessionAttachmentRetrievalQueryPlanTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public void Retrieval_index_is_a_separate_fingerprinted_core_object_and_Core35_is_frozen()
    {
        GrimoireSchemaObject index = Assert.Single(GrimoireSchemaCatalog.CoreObjects, item => item.Name == IndexName);

        Assert.Equal("Indexes", index.Category.ToString());

        Assert.Equal("Indexes." + IndexName, index.ResourcePath);

        Assert.Equal(CoreSchemaVersionThirtyFiveFixture.PublishedFingerprint, CoreSchemaVersionThirtyFiveFixture.Fingerprint);

        GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        Assert.True(core.TryGetStep(35, out GrimoireSchemaVersionStep step));

        Assert.Equal(36, step.ToVersion);

        Assert.Null(step.Backfill);

        Assert.Equal(CoreSchemaVersionThirtyFiveFixture.PublishedFingerprint, core.SourceDefinitionFingerprintFor(35));

        Assert.Equal(GrimoireSqlNormalizer.Normalize(index.Sql), GrimoireSqlNormalizer.Normalize(Assert.Single(step.Statements).Sql));
    }

    [Theory]
    [InlineData("CREATE TABLE IF NOT EXISTS standalone (Value TEXT);")]
    [InlineData("CREATE INDEX IF NOT EXISTS standalone ON owner(Value); CREATE INDEX IF NOT EXISTS extra ON owner(Value);")]
    [InlineData("CREATE INDEX IF NOT EXISTS standalone ON missing_owner(Value);")]
    public void Standalone_index_resources_reject_non_index_multiple_or_unowned_definitions(string sql)
    {
        GrimoireSchemaObject owner = new(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core,
            GrimoireSchemaCategory.Tables, "owner", "Tables.owner", "CREATE TABLE IF NOT EXISTS owner (Value TEXT);");

        // Cast keeps this test compilable before the new catalog category is implemented.
        GrimoireSchemaObject index = new(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core,
            (GrimoireSchemaCategory)4, "standalone", "Indexes.standalone", sql);

        Assert.Throws<InvalidOperationException>(() => GrimoireSchemaManifestBuilder.Build(
            GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 36, new string('A', 64), [owner, index]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Actual_latest_attachment_query_seeks_covering_index_after_fresh_or_Core35_upgrade_and_reopen(bool upgrade, bool comprehension)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection connection = await file.OpenAsync(CancellationToken.None))
        {
            if (upgrade)
            {
                await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionThirtyFiveFixture.ChainSet(), 64, CancellationToken.None);

                await SeedAsync(connection);

                Assert.Equal(0L, await ScalarAsync(connection, $"SELECT COUNT(*) FROM sqlite_schema WHERE name='{IndexName}'"));

                Assert.Equal(Expected(comprehension), await SearchAsync(connection, comprehension, assertPlan: false));
            }

            await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 64, CancellationToken.None);

            if (!upgrade)
            {
                await SeedAsync(connection);
            }

            Assert.Equal(Expected(comprehension), await SearchAsync(connection, comprehension, assertPlan: true));

            await AssertIndexAsync(connection);
        }

        await using SqliteConnection reopened = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(reopened, 64, CancellationToken.None);

        Assert.Equal(Expected(comprehension), await SearchAsync(reopened, comprehension, assertPlan: true));

        await AssertIndexAsync(reopened);

        Assert.Equal(6L, await ScalarAsync(reopened, "SELECT COUNT(*) FROM session_attachment_chunks"));

        Assert.Equal(6L, await ScalarAsync(reopened, "SELECT COUNT(*) FROM session_attachment_embeddings"));

        Assert.Equal("0000803F0000000000000000", await ScalarAsync(reopened, "SELECT hex(Embedding) FROM session_attachment_embeddings WHERE ChunkId='a'"));
    }

    [Fact]
    public async Task Failed_Core36_step_rolls_back_index_and_preserves_Core35_metadata_and_vectors()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionThirtyFiveFixture.ChainSet(), 64, CancellationToken.None);

        await SeedAsync(connection);

        GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        Assert.True(core.TryGetStep(35, out GrimoireSchemaVersionStep step));

        GrimoireSchemaVersionStep failing = step with
        {
            Statements = [.. step.Statements, new GrimoireSchemaTransitionStatement("InjectedFailure", 20, "InjectedFailure", "INSERT INTO MissingRetrievalIndexTable VALUES(1);")],
        };

        GrimoireSchemaVersionChainSet chains = new(
        [
            new(core.HeadManifest, core.HeadObjects, [.. core.Steps.Select(item => item.ToVersion == 36 ? failing : item)]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.GuildCanonical),
        ]);

        await Assert.ThrowsAsync<SqliteException>(() => GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 64, CancellationToken.None));

        Assert.Equal(35, await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));

        Assert.Equal(CoreSchemaVersionThirtyFiveFixture.PublishedFingerprint,
            await ScalarAsync(connection, "SELECT SourceDefinitionFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=0 AND TransactionTierCode=0"));

        Assert.Equal(0L, await ScalarAsync(connection, $"SELECT COUNT(*) FROM sqlite_schema WHERE name='{IndexName}'"));

        Assert.Equal(Expected(true), await SearchAsync(connection, true, assertPlan: false));
    }

    private static string Expected(bool comprehension) => comprehension
        ? "a:3F800000;b:3F7E6F2A;c:3F7E6F2A;document:3F7E6F2A"
        : "a:3F800000;b:3F7E6F2A;c:3F7E6F2A";

    private static async Task<string> SearchAsync(SqliteConnection connection, bool comprehension, bool assertPlan)
    {
        ArcanumSettings settings = new();

        settings.Features.AttachmentRetrieval = true;

        settings.Features.Comprehension = comprehension;

        TestOptionsMonitor<ArcanumSettings> options = new(settings);

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        SessionAttachmentRetrievalService service = new(options,
            new DivinationService(db, NullLogger<DivinationService>.Instance, options),
            new SessionAttachmentIndexRepository(db, options), NullLogger<SessionAttachmentRetrievalService>.Instance);

        List<string> statements = [];

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) => statements.Add(sql), null);

        SessionAttachmentRetrievedChunk[] results;

        try
        {
            results = await service.SearchAsync(SessionId, new Embedding<float>(new float[] { 1f, 0f, 0f }), includeHistorical: false, CancellationToken.None);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        string executed = Assert.Single(statements, sql => sql.Contains("FROM \"session_attachment_embeddings\" e", StringComparison.Ordinal));

        Assert.Contains("s.\"RetrievalScope\" =", executed, StringComparison.Ordinal);

        if (assertPlan)
        {
            await using SqliteCommand explain = connection.CreateCommand();

            // Native tracing captures the exact executed statement with bound values expanded by SQLite.
            explain.CommandText = "EXPLAIN QUERY PLAN " + executed;

            List<string> plan = [];

            await using SqliteDataReader reader = await explain.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(3));
            }

            Assert.Equal(new[]
            {
                $"SEARCH s USING COVERING INDEX {IndexName} (RetrievalScope=?)",
                "SEARCH e USING INDEX sqlite_autoindex_session_attachment_embeddings_1 (ChunkId=?)",
            }, plan);
        }

        return string.Join(';', results.Select(hit => hit.ChunkId + ":" + BitConverter.SingleToInt32Bits(hit.Similarity).ToString("X8")));
    }

    private static async Task AssertIndexAsync(SqliteConnection connection)
    {
        Assert.Equal(GrimoireSchemaVersionChains.CoreSchemaVersion, await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_index_info('{IndexName}') ORDER BY seqno)";

        Assert.Equal("RetrievalScope,ChunkId,MimeType", await command.ExecuteScalarAsync());
    }

    private static async Task SeedAsync(SqliteConnection connection)
    {
        // Parent identity and source metadata are real; only extraction/provider I/O is bypassed.
        foreach ((string id, string? scope, string mime, float[] vector) in new (string, string?, string, float[])[]
        {
            ("a", SessionId.ToString(), "text/plain", [1f, 0f, 0f]),
            ("b", SessionId.ToString(), "text/plain", [0.9f, 0.1f, 0f]),
            ("c", SessionId.ToString(), "text/plain", [0.9f, 0.1f, 0f]),
            ("document", SessionId.ToString(), "application/pdf", [0.9f, 0.1f, 0f]),
            ("historical", null, "text/plain", [1f, 0f, 0f]),
            ("other", "22222222-2222-4222-8222-222222222222", "text/plain", [1f, 0f, 0f]),
        })
        {
            await using SqliteCommand command = connection.CreateCommand();

            string attachment = Guid.NewGuid().ToString("D").ToUpperInvariant();

            command.CommandText = """
                INSERT INTO SessionAttachments(Id,SessionId,State,LogicalKey,OriginalFileName,Version,RelativePath,ContentSha256,MimeType,ByteLength,Kind,CreatedAt)
                VALUES(@attachment,@session,'Bound',@id,'fixture.txt',1,'fixture','hash',@mime,1,'Text','2026-01-01T00:00:00.0000000Z');
                INSERT INTO session_attachment_chunks(ChunkId,SessionId,AttachmentId,LogicalKey,Version,OriginalFileName,MimeType,ContentSha256,ChunkIndex,CharacterStart,CharacterEnd,StartLine,EndLine,Content,EmbeddingDimension,ExtractedAt,IndexedAt,RetrievalScope)
                VALUES(@id,@session,@attachment,@id,1,'fixture.txt',@mime,'hash',0,0,4,1,1,'body',3,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z',@scope);
                INSERT INTO session_attachment_embeddings(ChunkId,Embedding,Dim) VALUES(@id,@embedding,3);
                """;

            command.Parameters.AddWithValue("@attachment", attachment);

            command.Parameters.AddWithValue("@session", SessionId.ToString());

            command.Parameters.AddWithValue("@id", id);

            command.Parameters.AddWithValue("@mime", mime);

            command.Parameters.AddWithValue("@scope", scope is null ? DBNull.Value : scope);

            command.Parameters.AddWithValue("@embedding", EmbeddingBlobCodec.Encode(vector));

            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }
}
