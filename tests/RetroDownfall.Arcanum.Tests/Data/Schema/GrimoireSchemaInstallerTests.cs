using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Generated;

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
    /// vec0 acceleration stays optional. sqlite-vec is not shipped, so the extension never loads and
    /// the install reports no acceleration — while every durable BLOB companion table is present and
    /// Divination keeps its complete managed cosine fallback (§21.2).
    /// </summary>
    [Fact]
    public async Task InstallAsync_reports_no_vector_acceleration_without_sqlite_vec()
    {

        await using SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult result = await GrimoireSchemaInstaller.InstallAsync(
            connection,
            Dimensions,
            logger: null,
            CancellationToken.None);

        Assert.False(result.VectorAccelerationAvailable);

        Assert.False(await TableExistsAsync(connection, "entry_embeddings_vec"));

        Assert.True(await TableExistsAsync(connection, "entry_embeddings"));

    }

    [Fact]
    public async Task InstallAsync_is_idempotent_when_reopened()
    {

        await using SqliteConnection connection = await InstallAsync();

        string first = await GrimoireSchemaIdentity.ComputeAsync(connection, CancellationToken.None);

        _ = await GrimoireSchemaInstaller.InstallAsync(
            connection,
            Dimensions,
            logger: null,
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
    /// Installation atomicity: the durable schema is one transaction, so a failure part-way through
    /// leaves the database exactly as it was rather than half-built.
    /// </summary>
    [Fact]
    public async Task InstallObjectsInTransactionAsync_rolls_back_every_object_when_one_fails()
    {

        await using SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync(CancellationToken.None);

        List<GrimoireSchemaObject> withBrokenTail =
        [
            .. GrimoireSchemaCatalog.CoreObjects,
            new GrimoireSchemaObject(GrimoireSchemaCategory.Tables, "Broken", "CREATE TABLE broken_syntax (;"),
        ];

        _ = await Assert.ThrowsAsync<SqliteException>(() =>
            GrimoireSchemaInstaller.InstallObjectsInTransactionAsync(
                connection,
                withBrokenTail,
                Dimensions,
                CancellationToken.None));

        Assert.False(await TableExistsAsync(connection, "Sessions"));

        Assert.False(await TableExistsAsync(connection, "lexicon_entries"));

        Assert.False(await TableExistsAsync(connection, "broken_syntax"));

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
    public async Task InstallAsync_writes_the_configured_embedding_dimension_into_accelerator_ddl()
    {

        // sqlite-vec is not shipped, so the accelerator DDL cannot be installed here; assert the
        // resolved statement instead — that is the whole templating contract.
        foreach (GrimoireSchemaObject accelerator in GrimoireSchemaCatalog.AcceleratorObjects)
        {

            Assert.Contains("FLOAT[768]", GrimoireSchemaCatalog.Resolve(accelerator, 768), StringComparison.Ordinal);

        }

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
    /// The embedding-dimension mismatch check is a best-effort post-install diagnostic (§5.4.5): it
    /// must log and continue. It ran unguarded and outside SqliteBusyRetry, so a probe failure —
    /// an older table shape, a locked database — escaped InstallAsync and aborted host startup with a
    /// raw SqliteException, not even the sanitized GrimoireDatabaseUnavailableException.
    /// </summary>
    [Fact]
    public async Task InstallAsync_survives_a_failing_embedding_dimension_probe()
    {

        await using SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync(CancellationToken.None);

        // CREATE TABLE IF NOT EXISTS leaves this pre-existing shape untouched, so the probe's
        // SELECT "Dim" raises 'no such column'.
        await using (SqliteCommand seed = connection.CreateCommand())
        {

            seed.CommandText = """CREATE TABLE "entry_embeddings" ("EntryId" TEXT NOT NULL);""";

            _ = await seed.ExecuteNonQueryAsync(CancellationToken.None);

        }

        _ = await GrimoireSchemaInstaller.InstallAsync(
            connection,
            Dimensions,
            logger: null,
            CancellationToken.None);

        Assert.True(await TableExistsAsync(connection, "Sessions"));

    }

    private static async Task<SqliteConnection> InstallAsync()
    {

        SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaInstaller.InstallAsync(
            connection,
            Dimensions,
            logger: null,
            CancellationToken.None);

        return connection;

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
