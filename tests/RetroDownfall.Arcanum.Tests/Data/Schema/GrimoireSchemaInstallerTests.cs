using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The Grimoire schema contract: one declarative source installs everything, atomically, on a fresh
/// database — no migration chain, no <c>__EFMigrationsHistory</c>, no per-feature runtime
/// initializers (DESIGN §5.4.4, §5.4.5).
/// </summary>
public sealed class GrimoireSchemaInstallerTests
{

    /// <summary>
    /// These tests open SQLCipher connections directly, so the provider has to be installed before
    /// the first one is constructed. Without this the class only passes when some earlier test in
    /// the run happens to have initialized it, which makes a filtered run fail for a reason that has
    /// nothing to do with the schema.
    /// </summary>
    static GrimoireSchemaInstallerTests() => SqliteNativeRuntime.Instance.Initialize();

    private const int Dimensions = 1536;

    [Fact]
    public async Task InstallAsync_creates_the_grimoire_core_tables()
    {

        await using SqliteConnection connection = await InstallAsync();

        foreach (string table in new[]
        {
            "Sessions",
            "Entries",
            "Campaigns",
            "Prompts",
            "Apprentices",
            "WorkspaceContexts",
            "MageSettings",
            "SessionAttachments",
            "SessionContextPins",
            "LongRunningOperations",
            "InferenceRuns",
            "BillableOperations",
            "BudgetReservations",
            "CostAdjustments",
            "IdempotencyClaims",
            "IdempotencyKeys",
            "UnseenServantWatermarks",
            "SanctumBreaches",
            "UploadedFiles",
            "Batches",
            "BatchLineCheckpoints",
            "BudgetAlerts",
        })
        {

            Assert.True(await TableExistsAsync(connection, table), $"missing table {table}");

        }

        Assert.True(await TableExistsAsync(connection, "Entries_fts"));

        Assert.True(await IndexExistsAsync(connection, "IX_Entries_SessionId_Sequence"));

        Assert.True(await IndexExistsAsync(connection, "UX_SessionAttachments_Bound"));

        Assert.True(await IndexExistsAsync(connection, "UX_SessionAttachments_Pending"));

        Assert.True(await TriggerExistsAsync(connection, "Entries_ai"));

        Assert.True(await TriggerExistsAsync(connection, "TR_BatchLineCheckpoints_IncrementTotal"));

        Assert.True(await TriggerExistsAsync(connection, "TR_BatchLineCheckpoints_IncrementOutcome"));

    }

    /// <summary>
    /// Both self-referencing <c>ON DELETE RESTRICT</c> columns on the durable operation ledger must
    /// be index-backed (§10.8). Retention's leaf-first prune predicate looks children up by
    /// <c>ParentOperationId</c> <b>or</b> <c>RootOperationId</c>, and SQLite enforces RESTRICT with
    /// the same lookups, so an unindexed side turns every candidate into a full ledger scan.
    /// </summary>
    [Theory]
    [InlineData("ParentOperationId")]
    [InlineData("RootOperationId")]
    public async Task LongRunningOperations_child_lookups_are_index_backed(string column)
    {

        await using SqliteConnection connection = await InstallAsync();

        string plan = await ExplainQueryPlanAsync(
            connection,
            $"""SELECT 1 FROM "LongRunningOperations" child WHERE child."{column}" = 'x';""");

        Assert.Contains("INDEX", plan, StringComparison.Ordinal);

        Assert.DoesNotContain("SCAN", plan, StringComparison.Ordinal);

    }

    /// <summary>
    /// <c>Entries_fts</c> is a standalone FTS5 table whose <c>Id</c> column is <c>UNINDEXED</c>, and
    /// FTS5 can only satisfy MATCH, rowid, and rank constraints — an equality on a stored column
    /// scans the whole index. Its maintenance triggers therefore key each row by the rowid of the
    /// entry it indexes, the way <c>lexicon_entries</c> already does, so deleting a session's entries
    /// stays linear instead of scanning the index once per row.
    /// </summary>
    [Fact]
    public async Task Entries_fts_rows_are_keyed_by_the_rowid_of_the_entry_they_index()
    {

        await using SqliteConnection connection = await InstallAsync();

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Sessions" ("Id", "Status", "CreatedAt", "UpdatedAt")
            VALUES ('session-1', 'active', '2026-01-01', '2026-01-01');

            INSERT INTO "Entries"
                ("rowid", "Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
            VALUES
                (1000, 'entry-a', 'session-1', 0, 'alpha', 'test-model', '2026-01-01', 1),
                (2000, 'entry-b', 'session-1', 0, 'beta', 'test-model', '2026-01-01', 2);
            """);

        Assert.Equal([1000L, 2000L], await ReadFtsRowIdsAsync(connection));

        await ExecuteAsync(
            connection,
            """UPDATE "Entries" SET "Content" = 'gamma' WHERE "Id" = 'entry-a';""");

        Assert.Equal([1000L, 2000L], await ReadFtsRowIdsAsync(connection));

        await ExecuteAsync(
            connection,
            """DELETE FROM "Entries" WHERE "Id" = 'entry-b';""");

        Assert.Equal([1000L], await ReadFtsRowIdsAsync(connection));

    }

    /// <summary>
    /// One schema source: The Weave, Saga, The Tapestry, and The Lexicon install from the same tree
    /// as everything else, not from separate runtime initializers.
    /// </summary>
    [Fact]
    public async Task InstallAsync_creates_weave_saga_tapestry_and_lexicon_tables_from_the_same_source()
    {

        await using SqliteConnection connection = await InstallAsync();

        foreach (string table in new[]
        {
            "entry_embeddings",
            "workspace_file_chunks",
            "workspace_file_embeddings",
            "session_attachment_chunks",
            "session_attachment_embeddings",
            "session_attachment_index_state",
            "saga_memories",
            "saga_memory_embeddings",
            "saga_extraction_watermarks",
            "saga_memory_attachment_provenance",
            "attachment_memory_consultations",
            "tapestry_generations",
            "tapestry_nodes",
            "tapestry_node_embeddings",
            "lexicon_entries",
            "lexicon_fts",
            "lexicon_fact_attachment_provenance",
        })
        {

            Assert.True(await TableExistsAsync(connection, table), $"missing table {table}");

        }

        Assert.True(await TriggerExistsAsync(connection, "lexicon_entries_ai"));

        Assert.True(await TriggerExistsAsync(connection, "lexicon_entries_ad"));

        Assert.True(await TriggerExistsAsync(connection, "lexicon_entries_au"));

    }

    /// <summary>
    /// There is no vec0 acceleration tier to report. The hermetic SQLCipher runtime ships without
    /// extension loading, so the templated <c>entry_embeddings_vec</c> shadow is simply absent and the
    /// durable BLOB companion is the source of truth Divination's managed cosine search reads (§21.2).
    /// </summary>
    [Fact]
    public async Task InstallAsync_installs_no_vec0_shadow_over_the_durable_embedding_table()
    {

        await using SqliteConnection connection = await InstallAsync();

        Assert.False(await TableExistsAsync(connection, "entry_embeddings_vec"));

        Assert.True(await TableExistsAsync(connection, "entry_embeddings"));

    }

    /// <summary>
    /// Every tier installs healthy on a database this build has never touched. The three are reported
    /// separately because they fail separately, so a green Core beside a failed Covenant tier is a
    /// legitimate outcome the caller has to be able to see.
    /// </summary>
    [Fact]
    public async Task InstallAsync_reports_every_tier_healthy_on_a_fresh_database()
    {

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            "Data Source=:memory:",
            CancellationToken.None);

        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            Dimensions,
            CancellationToken.None);

        Assert.True(result.Core.IsHealthy);

        Assert.True(result.CovenantCanonical.IsHealthy);

        Assert.True(result.CovenantAccelerator.IsHealthy);

    }

    [Fact]
    public async Task InstallAsync_is_idempotent_when_reopened()
    {

        await using SqliteConnection connection = await InstallAsync();

        string first = await GrimoireSchemaIdentity.ComputeAsync(connection, CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            Dimensions,
            CancellationToken.None);

        string second = await GrimoireSchemaIdentity.ComputeAsync(connection, CancellationToken.None);

        Assert.Equal(first, second);

    }

    [Fact]
    public async Task InstallAsync_leaves_no_migration_bookkeeping_behind()
    {

        await using SqliteConnection connection = await InstallAsync();

        Assert.False(await TableExistsAsync(connection, "__EFMigrationsHistory"));

    }

    [Fact]
    public async Task InstallAsync_passes_integrity_and_foreign_key_checks()
    {

        await using SqliteConnection connection = await InstallAsync();

        await using (SqliteCommand quickCheck = connection.CreateCommand())
        {

            quickCheck.CommandText = "PRAGMA quick_check;";

            Assert.Equal(
                "ok",
                (await quickCheck.ExecuteScalarAsync(CancellationToken.None)) as string);

        }

        await using SqliteCommand foreignKeys = connection.CreateCommand();

        foreignKeys.CommandText = "PRAGMA foreign_key_check;";

        await using SqliteDataReader reader = await foreignKeys.ExecuteReaderAsync(CancellationToken.None);

        Assert.False(await reader.ReadAsync(CancellationToken.None));

    }

    /// <summary>
    /// Core installation atomicity: the tier's DDL and its seeded rows are one transaction, so a
    /// failure part-way through leaves the database exactly as it was rather than half-built.
    /// </summary>
    /// <remarks>
    /// The fault is injected through the tier's data initializer, which runs after all of Core's DDL
    /// and inside the same transaction. That is the latest point at which the tier can still fail, so
    /// a rollback proven here covers every earlier statement too. Core also rethrows rather than
    /// degrading, which is the other half of the contract: nothing else can be trusted without it.
    /// </remarks>
    [Fact]
    public async Task InstallAsync_rolls_the_whole_core_tier_back_when_its_seed_fails()
    {

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            "Data Source=:memory:",
            CancellationToken.None);

        GrimoireSchemaInstaller installer = new(
            new GrimoireSchemaManifestInspector(GrimoireSchemaTierOwnershipRegistry.CreateDefault()),
            new GrimoireSchemaDataInitializers(
            [
                new ThrowingCoreDataInitializer(),
                new CovenantCanonicalSchemaDataInitializer(),
                new CovenantAcceleratorSchemaDataInitializer(),
            ]));

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(
                connection,
                Dimensions,
                GrimoireSchemaTestInstaller.CreateContext(),
                CancellationToken.None));

        Assert.False(await TableExistsAsync(connection, "Sessions"));

        Assert.False(await TableExistsAsync(connection, "lexicon_entries"));

    }

    [Fact]
    public async Task InstallAsync_creates_non_null_reasoning_tokens_column_with_zero_default()
    {

        await using SqliteConnection connection = await InstallAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """PRAGMA table_info("BillableOperations");""";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {

            if (!string.Equals(reader.GetString(1), "ReasoningTokens", StringComparison.Ordinal))
            {

                continue;

            }

            Assert.Equal(1, reader.GetInt32(3));

            Assert.Equal("0", reader.GetString(4));

            return;

        }

        Assert.Fail("BillableOperations.ReasoningTokens was not installed.");

    }

    [Fact]
    public async Task InstallAsync_creates_the_managed_embedding_companion_tables()
    {

        // The templated vec0 accelerators these tables used to shadow are gone: the hermetic
        // SQLCipher runtime omits extension loading, so managed cosine over these BLOB tables is
        // the only search path and they are the source of truth rather than a fallback.
        await using SqliteConnection connection = await InstallAsync();

        Assert.True(await TableExistsAsync(connection, "entry_embeddings"));

    }

    [Fact]
    public void Compiled_ef_model_does_not_map_raw_sql_tables()
    {

        foreach (string table in new[] { "BillableOperations", "SessionContextPins", "LongRunningOperations", "SessionAttachments" })
        {

            Assert.DoesNotContain(
                ArcanumDbContextModel.Instance.GetEntityTypes(),
                entityType => string.Equals(entityType.GetTableName(), table, StringComparison.Ordinal));

        }

    }

    /// <summary>
    /// Every table the compiled EF model maps must exist in the installed schema — the two sources
    /// are allowed to differ in breadth (many tables have no EF entity) but never in agreement.
    /// </summary>
    [Fact]
    public async Task Every_compiled_ef_model_table_exists_in_the_installed_schema()
    {

        await using SqliteConnection connection = await InstallAsync();

        foreach (Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType in ArcanumDbContextModel.Instance.GetEntityTypes())
        {

            string? table = entityType.GetTableName();

            if (table is null)
            {

                continue;

            }

            Assert.True(await TableExistsAsync(connection, table), $"compiled model maps missing table {table}");

            List<string> columns = await ReadColumnNamesAsync(connection, table);

            foreach (Microsoft.EntityFrameworkCore.Metadata.IProperty property in entityType.GetProperties())
            {

                Assert.Contains(property.GetColumnName(), columns, StringComparer.Ordinal);

            }

        }

    }

    /// <summary>
    /// A manifest object that exists with no metadata row proves nothing about what installed it, so
    /// Core refuses rather than converging onto it.
    /// </summary>
    /// <remarks>
    /// This shape used to be tolerated: <c>CREATE TABLE IF NOT EXISTS</c> left the foreign table
    /// alone and the install continued over it. That is precisely the guess the tier gate now
    /// declines to make, and Core refuses loudly instead of leaving a database half this build's and
    /// half something else's.
    /// </remarks>
    [Fact]
    public async Task InstallAsync_refuses_core_when_a_manifest_object_has_no_metadata_row()
    {

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            "Data Source=:memory:",
            CancellationToken.None);

        await ExecuteAsync(
            connection,
            """CREATE TABLE "entry_embeddings" ("EntryId" TEXT NOT NULL);""");

        GrimoireSchemaRefusedException refused =
            await Assert.ThrowsAsync<GrimoireSchemaRefusedException>(() =>
                GrimoireSchemaTestInstaller.InstallAsync(
                    connection,
                    Dimensions,
                    CancellationToken.None));

        Assert.Equal(GrimoireSchemaTransactionTier.Core, refused.Tier);

        Assert.Equal(GrimoireSchemaTierHealth.MetadataMissing, refused.Health);

        Assert.False(await TableExistsAsync(connection, "Sessions"));

    }

    /// <summary>
    /// The embedding-dimension mismatch check is a best-effort post-install diagnostic (§5.4.5): it
    /// warns and continues, and it never truncates.
    /// </summary>
    /// <remarks>
    /// Deleting the stale vectors would be the tempting "fix" and the wrong one. Re-embedding is the
    /// operator's decision because it costs provider spend, and a startup path that quietly discarded
    /// the old vectors would make that decision for them and lose the only copy in the process.
    /// </remarks>
    [Fact]
    public async Task InstallAsync_warns_but_keeps_embeddings_written_at_another_dimension()
    {

        await using SqliteConnection connection = await InstallAsync();

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "entry_embeddings" ("EntryId", "Embedding", "Dim")
            VALUES ('entry-a', X'00', 8);
            """);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            Dimensions,
            CancellationToken.None);

        Assert.True(await TableExistsAsync(connection, "Sessions"));

        await using SqliteCommand surviving = connection.CreateCommand();

        surviving.CommandText = """SELECT COUNT(*) FROM "entry_embeddings" WHERE "Dim" = 8;""";

        Assert.Equal(1L, (long)(await surviving.ExecuteScalarAsync(CancellationToken.None))!);

    }

    private static async Task<SqliteConnection> InstallAsync()
    {

        SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            "Data Source=:memory:",
            CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            Dimensions,
            CancellationToken.None);

        return connection;

    }

    /// <summary>
    /// A Core initializer that fails at the one point where the tier's DDL is fully applied but the
    /// transaction has not committed.
    /// </summary>
    private sealed class ThrowingCoreDataInitializer : IGrimoireSchemaDataInitializer
    {

        public GrimoireSchemaTransactionTier TransactionTier => GrimoireSchemaTransactionTier.Core;

        public Task InitializeAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            GrimoireSchemaInitializationContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The core tier seed failed for this test.");

    }

    private static Task<bool> TableExistsAsync(SqliteConnection connection, string name) =>
        ObjectExistsAsync(connection, name, "table");

    private static Task<bool> IndexExistsAsync(SqliteConnection connection, string name) =>
        ObjectExistsAsync(connection, name, "index");

    private static Task<bool> TriggerExistsAsync(SqliteConnection connection, string name) =>
        ObjectExistsAsync(connection, name, "trigger");

    private static async Task<bool> ObjectExistsAsync(SqliteConnection connection, string name, string type)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE "type" = $type AND "name" = $name
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$type", type);

        command.Parameters.AddWithValue("$name", name);

        object? result = await command.ExecuteScalarAsync(CancellationToken.None);

        return result is not null && result != DBNull.Value;

    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static async Task<List<long>> ReadFtsRowIdsAsync(SqliteConnection connection)
    {

        List<long> rowIds = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """SELECT "rowid" FROM "Entries_fts" ORDER BY "rowid";""";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {

            rowIds.Add(reader.GetInt64(0));

        }

        return rowIds;

    }

    private static async Task<string> ExplainQueryPlanAsync(SqliteConnection connection, string sql)
    {

        List<string> details = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {

            details.Add(reader.GetString(reader.GetOrdinal("detail")));

        }

        return string.Join('\n', details);

    }

    private static async Task<List<string>> ReadColumnNamesAsync(SqliteConnection connection, string table)
    {

        List<string> columns = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"PRAGMA table_info(\"{table}\");";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {

            columns.Add(reader.GetString(1));

        }

        return columns;

    }

}
