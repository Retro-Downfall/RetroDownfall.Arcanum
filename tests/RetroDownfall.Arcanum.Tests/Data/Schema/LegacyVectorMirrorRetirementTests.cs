using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class LegacyVectorMirrorRetirementTests
{
    private static readonly (string Table, string Key)[] Embeddings =
    [
        ("entry_embeddings", "EntryId"),
        ("workspace_file_embeddings", "ChunkId"),
        ("session_attachment_embeddings", "ChunkId"),
        ("saga_memory_embeddings", "MemoryId"),
        ("tapestry_node_embeddings", "NodeId"),
    ];

    static LegacyVectorMirrorRetirementTests() => SqliteNativeRuntime.Instance.Initialize();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Core34_upgrade_retires_every_plain_mirror_and_preserves_every_durable_embedding(bool upperCase)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await InstallCore34Async(connection);

        await SeedDurableEmbeddingsAsync(connection);

        await SeedPlainMirrorsAsync(connection, upperCase);

        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 1536, CancellationToken.None);

        Assert.True(result.Core.IsHealthy);

        Assert.Equal(35, await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));

        foreach ((string table, string key) in Embeddings)
        {
            Assert.Equal(0L, await ScalarAsync(connection, $"SELECT COUNT(*) FROM sqlite_schema WHERE name='{table}_vec' COLLATE NOCASE"));

            Assert.Equal("0000803F0000000000000000", await ScalarAsync(connection, $"SELECT hex(Embedding) FROM {table} WHERE {key}='11111111-1111-4111-8111-111111111111'"));

            Assert.Equal(3L, await ScalarAsync(connection, $"SELECT Dim FROM {table}"));
        }

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None)).Core.IsHealthy);
    }

    [Theory]
    [InlineData("virtual")]
    [InlineData("view")]
    [InlineData("wrong-columns")]
    [InlineData("trigger")]
    [InlineData("foreign-key")]
    [InlineData("dependent-owner")]
    public async Task Unsupported_mirror_shape_refuses_upgrade_without_dropping_other_mirrors_or_advancing_core(string shape)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await InstallCore34Async(connection);

        await SeedDurableEmbeddingsAsync(connection);

        await SeedPlainMirrorsAsync(connection);

        await ExecuteAsync(connection, "DROP TABLE tapestry_node_embeddings_vec;");

        await ExecuteAsync(connection, shape switch
        {
            "virtual" => "PRAGMA writable_schema=ON; INSERT INTO sqlite_schema(type,name,tbl_name,rootpage,sql) VALUES('table','tapestry_node_embeddings_vec','tapestry_node_embeddings_vec',0,'CREATE VIRTUAL TABLE tapestry_node_embeddings_vec USING vec0(NodeId TEXT PRIMARY KEY, Embedding FLOAT[3])'); PRAGMA writable_schema=RESET;",
            "view" => "CREATE VIEW tapestry_node_embeddings_vec AS SELECT NodeId,Embedding FROM tapestry_node_embeddings;",
            "trigger" => "CREATE TABLE tapestry_node_embeddings_vec(NodeId TEXT PRIMARY KEY,Embedding BLOB NOT NULL); CREATE TRIGGER extra_mirror_trigger AFTER INSERT ON tapestry_node_embeddings_vec BEGIN SELECT 1; END;",
            "foreign-key" => "CREATE TABLE tapestry_node_embeddings_vec(NodeId TEXT PRIMARY KEY REFERENCES tapestry_nodes(NodeId),Embedding BLOB NOT NULL);",
            "dependent-owner" => "CREATE TABLE tapestry_node_embeddings_vec(NodeId TEXT PRIMARY KEY,Embedding BLOB NOT NULL); INSERT INTO tapestry_node_embeddings_vec VALUES('11111111-1111-4111-8111-111111111111',X'0000803F'); CREATE TABLE mirror_dependents(Id TEXT PRIMARY KEY,MirrorId TEXT REFERENCES tapestry_node_embeddings_vec(NodeId) ON DELETE CASCADE); INSERT INTO mirror_dependents VALUES('preserved-dependent','11111111-1111-4111-8111-111111111111');",
            _ => "CREATE TABLE tapestry_node_embeddings_vec(Unrelated TEXT);",
        });

        await Assert.ThrowsAsync<LegacyVectorMirrorRetirementRefusedException>(() => GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 1536, CancellationToken.None));

        Assert.Equal(34, await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM entry_embeddings_vec"));

        Assert.Equal("0000803F0000000000000000", await ScalarAsync(connection, "SELECT hex(Embedding) FROM saga_memory_embeddings"));

        if (shape == "dependent-owner")
        {
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM mirror_dependents WHERE Id='preserved-dependent'"));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Current_or_fresh_admission_refuses_retired_mirrors_instead_of_serving_an_unchecked_catalog(bool fresh, bool upperCase)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        if (!fresh)
        {
            await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);
        }

        string table = upperCase ? "ENTRY_EMBEDDINGS_VEC" : "entry_embeddings_vec";

        await ExecuteAsync(connection, $"CREATE TABLE {table}(EntryId TEXT PRIMARY KEY, Embedding BLOB NOT NULL);");

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None));

        Assert.Equal(1L, await ScalarAsync(connection, $"SELECT COUNT(*) FROM sqlite_schema WHERE name='{table}'"));
    }

    [Fact]
    public async Task Cancellation_after_a_drop_has_started_restores_all_mirrors_and_preserves_the_pending_journal()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await InstallCore34Async(connection);

        await SeedPlainMirrorsAsync(connection);

        GrimoireSchemaInstaller installer = GrimoireSchemaTestInstaller.Create();

        await installer.InstallAsync(connection, 1536, GrimoireSchemaTestInstaller.CreateContext(), CancellationToken.None);

        GrimoireSchemaTransitionJournalRow journal = (await GrimoireSchemaTransitionJournal.ReadAsync(connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None))!;

        string root = Path.GetDirectoryName(new SqliteConnectionStringBuilder(scratch.ConnectionString).DataSource)!;

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(root).BorrowAcquiredLock();

        using GrimoireSchemaQuiescentExecution execution = GrimoireSchemaQuiescentExecution.ForStartup(connection, held, root, new GrimoireDbReadiness());

        using CancellationTokenSource cancellation = new();

        int dropsStarted = 0;

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) =>
        {
            if (sql.StartsWith("DROP TABLE", StringComparison.OrdinalIgnoreCase) && ++dropsStarted == 2)
            {
                cancellation.Cancel();
            }
        }, null);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GrimoireSchemaBackfillRunner(installer, TimeProvider.System).AdvanceAsync(connection, installer.Chains.ForTier(GrimoireSchemaTransactionTier.Core), journal, GrimoireSchemaTestInstaller.CreateContext(), 1, cancellation.Token, execution));
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(dropsStarted >= 2);

        Assert.Equal(34, await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));

        Assert.Equal(journal, await GrimoireSchemaTransitionJournal.ReadAsync(connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None));

        foreach ((string table, _) in Embeddings)
        {
            Assert.Equal(1L, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {table}_vec"));
        }
    }

    [Fact]
    public async Task Failed_version_publication_rolls_back_all_mirror_retirements_and_the_exact_journal()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await InstallCore34Async(connection);

        await SeedPlainMirrorsAsync(connection);

        GrimoireSchemaInstaller installer = GrimoireSchemaTestInstaller.Create();

        await installer.InstallAsync(connection, 1536, GrimoireSchemaTestInstaller.CreateContext(), CancellationToken.None);

        GrimoireSchemaTransitionJournalRow? journal = await GrimoireSchemaTransitionJournal.ReadAsync(connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None);

        Assert.NotNull(journal);

        await ExecuteAsync(connection, "CREATE TEMP TRIGGER refuse_retirement BEFORE DELETE ON grimoire_schema_transitions BEGIN SELECT RAISE(ABORT,'refused'); END;");

        string root = Path.GetDirectoryName(new SqliteConnectionStringBuilder(scratch.ConnectionString).DataSource)!;

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(root).BorrowAcquiredLock();

        using GrimoireSchemaQuiescentExecution execution = GrimoireSchemaQuiescentExecution.ForStartup(connection, held, root, new GrimoireDbReadiness());

        await Assert.ThrowsAnyAsync<Exception>(() => new GrimoireSchemaBackfillRunner(installer, TimeProvider.System).AdvanceAsync(connection, installer.Chains.ForTier(GrimoireSchemaTransactionTier.Core), journal, GrimoireSchemaTestInstaller.CreateContext(), 1, CancellationToken.None, execution));

        Assert.Equal(journal, await GrimoireSchemaTransitionJournal.ReadAsync(connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None));

        Assert.Equal(34, await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));

        foreach ((string table, _) in Embeddings)
        {
            Assert.Equal(1L, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {table}_vec"));
        }

        await ExecuteAsync(connection, "DROP TRIGGER refuse_retirement;");

        Assert.True((await new GrimoireSchemaBackfillRunner(installer, TimeProvider.System).AdvanceAsync(connection, installer.Chains.ForTier(GrimoireSchemaTransactionTier.Core), journal, GrimoireSchemaTestInstaller.CreateContext(), 1, CancellationToken.None, execution)).StepComplete);
    }

    private static async Task InstallCore34Async(SqliteConnection connection)
    {
        Assert.Equal(CoreSchemaVersionThirtyFourFixture.PublishedFingerprint, GrimoireSchemaCatalog.CoreSchemaFingerprint);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionThirtyFourFixture.ChainSet(), 1536, CancellationToken.None);
    }

    private static async Task SeedDurableEmbeddingsAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, "PRAGMA foreign_keys=OFF;");

        await ExecuteAsync(connection, """
            INSERT INTO tapestry_generations(GenerationId,ScopeKind,ScopeId,Status,AlgorithmVersion,SettingsFingerprint,SummaryRecipeVersion,EmbeddingDimension,CorpusFingerprint,StartedAt)
            VALUES('generation','Session','scope','Building','test','test','test',3,'test','2026-01-01T00:00:00Z');
            INSERT INTO tapestry_nodes(NodeId,GenerationId,ScopeKind,ScopeId,Layer,ParentScopeKey,NodeKind,ParentNodeId,SourceKind,SourceId,SourceLabel,ContentHash,EmbeddingDimension,CreatedAt)
            VALUES('11111111-1111-4111-8111-111111111111','generation','Session','scope',0,'generation:root','Leaf',NULL,'Entry','11111111-1111-4111-8111-111111111111','test','test',3,'2026-01-01T00:00:00Z');
            """);

        foreach ((string table, string key) in Embeddings)
        {
            await ExecuteAsync(connection, $"INSERT INTO {table}({key},Embedding,Dim) VALUES('11111111-1111-4111-8111-111111111111',X'0000803F0000000000000000',3);");
        }

        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;");
    }

    private static async Task SeedPlainMirrorsAsync(SqliteConnection connection, bool upperCase = false)
    {
        foreach ((string table, string key) in Embeddings)
        {
            string mirror = upperCase ? (table + "_vec").ToUpperInvariant() : table + "_vec";

            await ExecuteAsync(connection, $"CREATE TABLE {mirror}({key} TEXT PRIMARY KEY,Embedding BLOB NOT NULL); INSERT INTO {mirror} VALUES('11111111-1111-4111-8111-111111111111',X'0000803F0000000000000000');");
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }
}
