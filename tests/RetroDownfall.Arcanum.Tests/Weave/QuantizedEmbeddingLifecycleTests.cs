using Microsoft.Data.Sqlite;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed class QuantizedEmbeddingLifecycleTests(ITestOutputHelper output)
{
    internal static readonly (string Table, string Key)[] Families =
    [
        ("entry_embeddings", "EntryId"),
        ("workspace_file_embeddings", "ChunkId"),
        ("session_attachment_embeddings", "ChunkId"),
        ("saga_memory_embeddings", "MemoryId"),
        ("tapestry_node_embeddings", "NodeId"),
    ];

    static QuantizedEmbeddingLifecycleTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public void Five_companions_are_fingerprinted_and_upgrade_without_an_admission_backfill()
    {
        Assert.Equal(CoreSchemaVersionThirtySixFixture.PublishedFingerprint, CoreSchemaVersionThirtySixFixture.Fingerprint);

        foreach ((string table, _) in Families)
        {
            Assert.Contains(GrimoireSchemaCatalog.CoreObjects, item => item.Name == table + "_quantized");
        }

        GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        Assert.True(core.TryGetStep(36, out GrimoireSchemaVersionStep step));

        Assert.Equal(37, step.ToVersion);

        Assert.Equal(CoreSchemaVersionThirtySixFixture.PublishedFingerprint, core.SourceDefinitionFingerprintFor(36));

        Assert.Null(step.Backfill);
    }

    [Fact]
    public async Task Upsert_and_backfill_share_authoritative_transactions_and_skip_unsafe_rows_without_starving()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "CREATE TABLE entry_embeddings (EntryId TEXT PRIMARY KEY, Embedding BLOB, Dim INTEGER);";

        await command.ExecuteNonQueryAsync();

        foreach (GrimoireSchemaObject item in GrimoireSchemaCatalog.CoreObjects.Where(item =>
            item.Name == "entry_embeddings_quantized" || item.Name.StartsWith("entry_embeddings_quantized_", StringComparison.Ordinal)))
        {
            command.CommandText = item.Sql;

            await command.ExecuteNonQueryAsync();
        }

        command.CommandText = "INSERT INTO entry_embeddings VALUES ('a', X'010203', 1), ('b', @vector, 3), ('c', @vector, 3);";

        command.Parameters.AddWithValue("@vector", EmbeddingBlobCodec.Encode([1f, 0f, 0f]));

        await command.ExecuteNonQueryAsync();

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            await QuantizedEmbeddingStore.UpsertAsync(connection, transaction, "entry_embeddings", "c", new float[] { 1, 0, 0 }, CancellationToken.None);

            await transaction.RollbackAsync();
        }

        command.CommandText = "SELECT COUNT(*) FROM entry_embeddings_quantized";

        Assert.Equal(0L, await command.ExecuteScalarAsync());

        string? cursor = null;

        for (int page = 0; page < 3; page++)
        {
            await using SqliteTransaction transaction = connection.BeginTransaction();

            (cursor, int examined) = await QuantizedEmbeddingStore.BackfillBatchAsync(connection, transaction,
                "entry_embeddings", cursor, 1, CancellationToken.None);

            Assert.Equal(1, examined);

            await transaction.CommitAsync();
        }

        Assert.Equal(2L, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT Representation FROM entry_embeddings_quantized WHERE EntryId='b'";

        Assert.Equal(QuantizedEmbeddingCodec.Encode([1f, 0f, 0f], "b"u8), (byte[])(await command.ExecuteScalarAsync())!);

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Assert.Equal((null, 0), await QuantizedEmbeddingStore.BackfillBatchAsync(connection, transaction,
                "entry_embeddings", cursor, 1, CancellationToken.None));

            await transaction.CommitAsync();
        }

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            // The page bound counts source rows even when every usable row already has a companion.
            // Filtering missing projections before LIMIT would scan the covered corpus on every tick.
            Assert.Equal(("c", 3), await QuantizedEmbeddingStore.BackfillBatchAsync(connection, transaction,
                "entry_embeddings", null, 128, CancellationToken.None));

            await transaction.CommitAsync();
        }
    }

    [Theory]
    [InlineData("entry_embeddings", "EntryId")]
    [InlineData("workspace_file_embeddings", "ChunkId")]
    [InlineData("session_attachment_embeddings", "ChunkId")]
    [InlineData("saga_memory_embeddings", "MemoryId")]
    [InlineData("tapestry_node_embeddings", "NodeId")]
    public async Task Companion_storage_rejects_unbounded_payloads_atomically_and_preserves_clustered_layout(string table, string key)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"CREATE TABLE {table} ({key} TEXT PRIMARY KEY, Embedding BLOB, Dim INTEGER);";

        await command.ExecuteNonQueryAsync();

        command.CommandText = GrimoireSchemaCatalog.CoreObjects.Single(item => item.Name == table + "_quantized").Sql;

        await command.ExecuteNonQueryAsync();

        command.CommandText = $"INSERT INTO {table} VALUES ('a', @source, 3);";

        byte[] source = EmbeddingBlobCodec.Encode([1f, 0f, 0f]);

        command.Parameters.AddWithValue("@source", source);

        await command.ExecuteNonQueryAsync();

        command.CommandText = "SELECT wr FROM pragma_table_list WHERE name=@name";

        command.Parameters.AddWithValue("@name", table + "_quantized");

        Assert.Equal(1L, await command.ExecuteScalarAsync());

        int maximum = QuantizedEmbeddingCodec.MaximumDimensions + QuantizedEmbeddingCodec.RepresentationOverheadBytes;

        command.Parameters.AddWithValue("@maximum", maximum);

        foreach (string invalid in new[] { "zeroblob(@maximum + 1)", "CAST(zeroblob(@maximum) AS TEXT)" })
        {
            command.CommandText = $"INSERT INTO {table}_quantized VALUES ('a', {invalid});";

            SqliteException failure = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());

            Assert.Equal(19, failure.SqliteErrorCode);

            command.CommandText = $"SELECT COUNT(*) FROM {table}_quantized";

            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }

        // The maximum supported size is admitted, and short damaged BLOBs retain exact fallback.
        command.CommandText = $"INSERT INTO {table}_quantized VALUES ('a', zeroblob(@maximum)); SELECT length(Representation) FROM {table}_quantized;";

        Assert.Equal((long)maximum, await command.ExecuteScalarAsync());

        foreach (string invalid in new[] { "zeroblob(@maximum + 1)", "CAST(zeroblob(@maximum) AS TEXT)" })
        {
            command.CommandText = $"UPDATE {table}_quantized SET Representation={invalid} WHERE {key}='a';";

            SqliteException failure = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());

            Assert.Equal(19, failure.SqliteErrorCode);

            command.CommandText = $"SELECT Representation FROM {table}_quantized WHERE {key}='a';";

            Assert.Equal(new byte[maximum], (byte[])(await command.ExecuteScalarAsync())!);

            command.CommandText = $"SELECT Embedding FROM {table} WHERE {key}='a';";

            Assert.Equal(source, (byte[])(await command.ExecuteScalarAsync())!);
        }

        command.CommandText = $"UPDATE {table}_quantized SET Representation=X'01' WHERE {key}='a'; SELECT length(Representation) FROM {table}_quantized;";

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backfill_skips_oversized_inherited_payloads_without_reading_overflow_pages(bool textPayload)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        SqliteConnectionStringBuilder settings = new(scratch.ConnectionString)
        {
            Password = "quantized-backfill-overflow-fixture",
        };

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(settings.ConnectionString, CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            CREATE TABLE entry_embeddings (EntryId TEXT PRIMARY KEY, Embedding BLOB, Dim INTEGER);
            CREATE TABLE entry_embeddings_quantized (EntryId TEXT PRIMARY KEY, Representation BLOB NOT NULL) WITHOUT ROWID;
            """;

        await command.ExecuteNonQueryAsync();

        command.CommandText = textPayload
            ? "INSERT INTO entry_embeddings VALUES ('a', CAST(zeroblob(@oversized) AS TEXT), @dimensions)"
            : "INSERT INTO entry_embeddings VALUES ('a', zeroblob(@oversized), @dimensions)";

        command.Parameters.AddWithValue("@oversized", 1024 * 1024);

        command.Parameters.AddWithValue("@dimensions", 1024 * 1024 / sizeof(float));

        await command.ExecuteNonQueryAsync();

        command.CommandText = """
            INSERT INTO entry_embeddings VALUES ('b', X'010203', 1), ('c', X'', 0), ('d', NULL, 0), ('e', 42, 1), ('z', @valid, 3);
            PRAGMA mmap_size=0;
            PRAGMA cache_size=-64;
            PRAGMA shrink_memory;
            """;

        command.Parameters.AddWithValue("@valid", EmbeddingBlobCodec.Encode([1f, 0f, 0f]));

        await command.ExecuteNonQueryAsync();

        Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_db_status(connection.Handle,
            SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out _, out _, 1));

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Assert.Equal(("z", 6), await QuantizedEmbeddingStore.BackfillBatchAsync(connection, transaction,
                "entry_embeddings", null, 128, CancellationToken.None));

            await transaction.CommitAsync();
        }

        Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_db_status(connection.Handle,
            SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out int misses, out _, 0));

        command.CommandText = "SELECT EntryId FROM entry_embeddings_quantized";

        Assert.Equal("z", await command.ExecuteScalarAsync());

        output.WriteLine($"Backfill with 1 MiB inherited {(textPayload ? "TEXT" : "BLOB")} payload used {misses} cold pager misses and indexed the following valid row.");

        Assert.True(misses < 32, $"Unsupported inherited payload read overflow pages: {misses} cold pager misses.");
    }

    [Fact]
    public async Task Backfill_worker_defers_while_maintenance_has_closed_work_admission()
    {
        GrimoireConnectionAdmissionGate admission = new(TimeProvider.System);

        CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(new byte[32]));

        var begun = admission.BeginOrResumeExclusive(owner);

        Assert.True(begun.IsSuccess);

        await using IGrimoireClosingOwner closing = begun.Value;

        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        QuantizedEmbeddingBackfillService worker = new(services.GetRequiredService<IServiceScopeFactory>(),
            admission, NullLogger<QuantizedEmbeddingBackfillService>.Instance);

        Assert.Equal(0, await worker.RunTickAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Worker_reconciles_bounded_pages_and_a_new_worker_revisits_repaired_rows()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        foreach ((string table, string key) in Families)
        {
            command.CommandText = $"CREATE TABLE {table} ({key} TEXT PRIMARY KEY, Embedding BLOB, Dim INTEGER);";

            await command.ExecuteNonQueryAsync();

            foreach (GrimoireSchemaObject item in GrimoireSchemaCatalog.CoreObjects.Where(item =>
                item.Name == table + "_quantized" || item.Name.StartsWith(table + "_quantized_", StringComparison.Ordinal)))
            {
                command.CommandText = item.Sql;

                await command.ExecuteNonQueryAsync();
            }
        }

        command.CommandText = """
            WITH RECURSIVE ids(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM ids WHERE i<129)
            INSERT INTO entry_embeddings SELECT printf('%04d',i), X'010203', 1 FROM ids;
            INSERT INTO entry_embeddings VALUES ('9999', @vector, 3);
            """;

        command.Parameters.AddWithValue("@vector", EmbeddingBlobCodec.Encode([1f, 0f, 0f]));

        await command.ExecuteNonQueryAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await using ServiceProvider services = new ServiceCollection().AddSingleton(db)
            .AddSingleton<IGrimoireOrdinaryConnectionFactory>(FixtureOrdinaryConnectionFactory.For(db))
            .BuildServiceProvider();

        QuantizedEmbeddingBackfillService CreateWorker() => new(services.GetRequiredService<IServiceScopeFactory>(),
            new GrimoireConnectionAdmissionGate(TimeProvider.System), NullLogger<QuantizedEmbeddingBackfillService>.Instance);

        using QuantizedEmbeddingBackfillService worker = CreateWorker();

        Assert.Equal(128, await worker.RunTickAsync(CancellationToken.None));

        Assert.Equal(2, await worker.RunTickAsync(CancellationToken.None));

        command.CommandText = "SELECT COUNT(*) FROM entry_embeddings_quantized";

        Assert.Equal(1L, await command.ExecuteScalarAsync());

        command.CommandText = "UPDATE entry_embeddings SET Embedding=@vector, Dim=3 WHERE EntryId='0001'";

        await command.ExecuteNonQueryAsync();

        using QuantizedEmbeddingBackfillService restarted = CreateWorker();

        Assert.Equal(128, await restarted.RunTickAsync(CancellationToken.None));

        command.CommandText = "SELECT COUNT(*) FROM entry_embeddings_quantized";

        Assert.Equal(2L, await command.ExecuteScalarAsync());

        using CancellationTokenSource cancelled = new();

        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarted.RunTickAsync(cancelled.Token));

        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Core36_upgrade_and_failed_Core37_publication_rolls_back_companions()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionThirtySixFixture.ChainSet(), 64, CancellationToken.None);

        GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        Assert.True(core.TryGetStep(36, out GrimoireSchemaVersionStep step));

        Assert.Equal(37, step.ToVersion);

        GrimoireSchemaVersionStep failing = step with
        {
            Statements = [.. step.Statements, new GrimoireSchemaTransitionStatement("InjectedFailure", 900, "InjectedFailure", "INSERT INTO MissingQuantizedTable VALUES(1);")],
        };

        GrimoireSchemaVersionChainSet chains = new(
        [
            new(core.HeadManifest, core.HeadObjects, [.. core.Steps.Select(item => item.ToVersion == 37 ? failing : item)]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.GuildCanonical),
        ]);

        await Assert.ThrowsAsync<SqliteException>(() => GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 64, CancellationToken.None));

        Assert.Equal(36, await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE '%_quantized'";

        Assert.Equal(0L, await command.ExecuteScalarAsync());

        await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 64, CancellationToken.None);

        Assert.Equal(GrimoireSchemaVersionChains.CoreSchemaVersion,
            await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));

        Assert.Equal(5L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("entry_embeddings", "EntryId")]
    [InlineData("workspace_file_embeddings", "ChunkId")]
    [InlineData("session_attachment_embeddings", "ChunkId")]
    [InlineData("saga_memory_embeddings", "MemoryId")]
    [InlineData("tapestry_node_embeddings", "NodeId")]
    public async Task Authoritative_update_invalidates_and_delete_cascades_the_companion(string table, string key)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        // Isolate the embedding ownership contract from unrelated Session and Saga provenance guards.
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"CREATE TABLE {table} ({key} TEXT PRIMARY KEY, Embedding BLOB, Dim INTEGER);";

        await command.ExecuteNonQueryAsync();

        foreach (GrimoireSchemaObject item in GrimoireSchemaCatalog.CoreObjects.Where(item =>
            item.Name == table + "_quantized" || item.Name.StartsWith(table + "_quantized_", StringComparison.Ordinal)))
        {
            command.CommandText = item.Sql;

            await command.ExecuteNonQueryAsync();
        }

        command.CommandText = $"INSERT INTO {table} VALUES ('a', X'0000803F', 1); INSERT INTO {table}_quantized VALUES ('a', X'01'); UPDATE {table} SET Embedding=X'0000003F' WHERE {key}='a'; SELECT COUNT(*) FROM {table}_quantized;";

        Assert.Equal(0L, await command.ExecuteScalarAsync());

        command.CommandText = $"INSERT INTO {table}_quantized VALUES ('a', X'01'); UPDATE {table} SET {key}='b' WHERE {key}='a'; SELECT COUNT(*) FROM {table}_quantized;";

        Assert.Equal(0L, await command.ExecuteScalarAsync());

        command.CommandText = $"INSERT INTO {table}_quantized VALUES ('b', X'01'); DELETE FROM {table}; SELECT COUNT(*) FROM {table}_quantized;";

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
