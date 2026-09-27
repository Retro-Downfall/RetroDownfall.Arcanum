using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class LexiconCurationEvolutionTests
{
    static LexiconCurationEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task Inherited_rows_remain_active_unpinned_at_generation_one_and_legacy_hash_format()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, CoreSchemaVersionTenFixture.ChainSet(), 10);

        await SeedAsync(connection);

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, 11);

        Assert.Null(await ScalarAsync(connection, "SELECT RetiredAtUtc FROM lexicon_entries"));

        Assert.Null(await ScalarAsync(connection, "SELECT PinnedAtUtc FROM lexicon_entries"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT CurationGeneration FROM lexicon_entries"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT ContentHashFormatCode FROM annal_versions"));

        Assert.Equal(1L, await HitsAsync(connection, "inherited"));
    }

    [Fact]
    public async Task Fresh_and_evolved_version_eleven_catalogs_have_identical_definitions()
    {
        IReadOnlyDictionary<string, string> fresh = await DefinitionsAsync(evolve: false);

        IReadOnlyDictionary<string, string> evolved = await DefinitionsAsync(evolve: true);

        Assert.Contains("lexicon_annal_fact_provenance", fresh.Keys);

        Assert.Equal(fresh.Keys.Order(StringComparer.Ordinal), evolved.Keys.Order(StringComparer.Ordinal));

        foreach ((string name, string definition) in fresh)
        {
            Assert.Equal(definition, evolved[name]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provenance_is_content_free_ordinal_keyed_and_owned_only_by_the_annal_version(bool evolve)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await PrepareAsync(connection, evolve);

        await SeedAsync(connection);

        Assert.Equal(
            "AnnalVersionId,FactOrdinal,SessionId,AttachmentId,LogicalKey,AttachmentVersion,AttachmentContentHash,MaterializedAt,SourceType",
            await ScalarAsync(connection, "SELECT group_concat(name, ',') FROM pragma_table_info('lexicon_annal_fact_provenance')"));

        Assert.Equal(
            "AnnalVersionId,FactOrdinal",
            await ScalarAsync(connection, "SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_table_info('lexicon_annal_fact_provenance') WHERE pk > 0 ORDER BY pk)"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM pragma_foreign_key_list('lexicon_annal_fact_provenance')"));

        Assert.Equal(
            "annal_versions:AnnalVersionId:VersionId:CASCADE",
            await ScalarAsync(connection, "SELECT \"table\" || ':' || \"from\" || ':' || \"to\" || ':' || on_delete FROM pragma_foreign_key_list('lexicon_annal_fact_provenance')"));

        const string insert = """
            INSERT INTO lexicon_annal_fact_provenance
                (AnnalVersionId, FactOrdinal, SessionId, AttachmentId, LogicalKey,
                 AttachmentVersion, AttachmentContentHash, MaterializedAt, SourceType)
            VALUES ('v1', 0, 'historical-session', 'historical-attachment', 'document', 2,
                    'attachment-hash', '2026-01-01T00:00:00.0000000Z', 'file');
            """;

        await ExecuteAsync(connection, insert);

        SqliteException duplicate = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, insert));

        Assert.Equal(19, duplicate.SqliteErrorCode);

        SqliteException negative = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(connection, insert.Replace("'v1', 0", "'v1', -1", StringComparison.Ordinal)));

        Assert.Equal(19, negative.SqliteErrorCode);

        SqliteException orphan = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(connection, insert.Replace("'v1', 0", "'missing', 0", StringComparison.Ordinal)));

        Assert.Equal(19, orphan.SqliteErrorCode);

        await ExecuteAsync(connection, "DELETE FROM lexicon_entries;");

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM lexicon_annal_fact_provenance"));

        await ExecuteAsync(connection, "DELETE FROM annal_versions WHERE VersionId = 'v1';");

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM lexicon_annal_fact_provenance"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_three_FTS_triggers_are_structurally_replaced_with_active_guards(bool evolve)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await PrepareAsync(connection, evolve);

        foreach (string trigger in new[] { "lexicon_entries_ai", "lexicon_entries_ad", "lexicon_entries_au" })
        {
            string sql = Assert.IsType<string>(await ScalarAsync(connection, $"SELECT sql FROM sqlite_schema WHERE name = '{trigger}'"));

            Assert.Contains("RetiredAtUtc IS NULL", sql, StringComparison.Ordinal);

            Assert.NotEqual(
                GrimoireSqlNormalizer.Normalize(CoreSchemaVersionTenFixture.Objects.Single(definition => definition.Name == trigger).Sql),
                GrimoireSqlNormalizer.Normalize(sql));
        }

        string updateSql = Assert.IsType<string>(
            await ScalarAsync(connection, "SELECT sql FROM sqlite_schema WHERE name = 'lexicon_entries_au'"));

        Assert.Contains("old.RetiredAtUtc IS NULL", updateSql, StringComparison.Ordinal);

        Assert.Contains("new.RetiredAtUtc IS NULL", updateSql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_three_replaced_triggers_keep_only_active_rows_searchable(bool evolve)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await PrepareAsync(connection, evolve);

        await ExecuteAsync(connection, """
            INSERT INTO lexicon_entries (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt)
            VALUES ('active', 'A', 'a', 'person', '[]', 'original', '2026-01-01T00:00:00.0000000Z');
            INSERT INTO lexicon_entries (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt, RetiredAtUtc)
            VALUES ('retired', 'R', 'r', 'person', '[]', 'hidden', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
            """);

        Assert.Equal(1L, await HitsAsync(connection, "original"));

        Assert.Equal(0L, await HitsAsync(connection, "hidden"));

        await ExecuteAsync(connection, "UPDATE lexicon_entries SET FactsText = 'revised' WHERE Id = 'active';");

        Assert.Equal(0L, await HitsAsync(connection, "original"));

        Assert.Equal(1L, await HitsAsync(connection, "revised"));

        await ExecuteAsync(connection, "UPDATE lexicon_entries SET RetiredAtUtc = UpdatedAt WHERE Id = 'active';");

        Assert.Equal(0L, await HitsAsync(connection, "revised"));

        await ExecuteAsync(connection, "UPDATE lexicon_entries SET FactsText = 'stillhidden' WHERE Id = 'retired';");

        Assert.Equal(0L, await HitsAsync(connection, "stillhidden"));

        await ExecuteAsync(connection, "UPDATE lexicon_entries SET RetiredAtUtc = NULL, FactsText = 'returned' WHERE Id = 'active';");

        Assert.Equal(1L, await HitsAsync(connection, "returned"));

        await ExecuteAsync(connection, "DELETE FROM lexicon_entries WHERE Id = 'retired';");

        Assert.Equal(1L, await HitsAsync(connection, "returned"));

        await ExecuteAsync(connection, "DELETE FROM lexicon_entries WHERE Id = 'active';");

        Assert.Equal(0L, await HitsAsync(connection, "returned"));
    }

    [Fact]
    public async Task Projection_rebuild_discards_stale_tokens_and_repopulates_only_active_rows()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await PrepareAsync(connection, evolve: false);

        await SeedAsync(connection);

        await ExecuteAsync(connection, """
            INSERT INTO lexicon_entries (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt, RetiredAtUtc)
            VALUES ('retired', 'Retired', 'retired', 'person', '[]', 'hidden', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
            INSERT INTO lexicon_fts(rowid, Name, Type, FactsText)
            SELECT rowid, Name, Type, FactsText FROM lexicon_entries WHERE Id = 'retired';
            INSERT INTO lexicon_fts(rowid, Name, Type, FactsText)
            VALUES (999, 'Orphan', 'person', 'staletoken');
            """);

        Assert.Equal(1L, await HitsAsync(connection, "hidden"));

        Assert.Equal(1L, await HitsAsync(connection, "staletoken"));

        foreach (GrimoireSchemaTransitionStatementResource statement in GrimoireSchemaCatalog.TransitionStatements.Where(
                     static statement => statement.TransactionTier == GrimoireSchemaTransactionTier.Core
                         && statement.ToVersion == 11 && statement.Ordinal is 90 or 100))
        {
            await ExecuteAsync(connection, statement.Sql);
        }

        Assert.Equal(1L, await HitsAsync(connection, "inherited"));

        Assert.Equal(0L, await HitsAsync(connection, "hidden"));

        Assert.Equal(0L, await HitsAsync(connection, "staletoken"));
    }

    [Fact]
    public async Task Version_nine_sweep_still_reads_only_its_historical_columns()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, CoreSchemaVersionNineFixture.ChainSet(), 9);

        await SeedAsync(connection);

        await ExecuteAsync(connection, "UPDATE lexicon_entries SET UpdatedAt = '2026-01-01T01:00:00.0000000+01:00';");

        Assert.Null(await ScalarAsync(connection, "SELECT name FROM pragma_table_info('lexicon_entries') WHERE name = 'RetiredAtUtc'"));

        GrimoireSchemaVersionStep step = GrimoireSchemaVersionChains.Default
            .ForTier(GrimoireSchemaTransactionTier.Core).Steps.Single(static step => step.ToVersion == 9);

        IGrimoireSchemaBackfill backfill = Assert.IsAssignableFrom<IGrimoireSchemaBackfill>(step.Backfill);

        string? cursor = null;

        bool completed = false;

        for (int page = 0; page < 200; page++)
        {
            await using SqliteTransaction transaction = connection.BeginTransaction();

            GrimoireSchemaBackfillBatch batch = await backfill.AdvanceBatchAsync(
                connection, transaction, cursor, CancellationToken.None);

            await transaction.CommitAsync();

            cursor = batch.NextCursor;

            if (batch.IsComplete)
            {
                completed = true;

                break;
            }
        }

        Assert.True(completed);

        Assert.Equal("2026-01-01T00:00:00.0000000Z", await ScalarAsync(connection, "SELECT UpdatedAt FROM lexicon_entries"));
    }

    private static async Task PrepareAsync(SqliteConnection connection, bool evolve)
    {
        if (evolve)
        {
            await InstallAsync(connection, CoreSchemaVersionTenFixture.ChainSet(), 10);
        }

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, 11);
    }

    private static async Task InstallAsync(SqliteConnection connection, GrimoireSchemaVersionChainSet chains, int version)
    {
        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
            connection, chains, 1536, CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

        Assert.Equal(version, result.Core.SchemaVersion);
    }

    private static async Task<IReadOnlyDictionary<string, string>> DefinitionsAsync(bool evolve)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await PrepareAsync(connection, evolve);

        Dictionary<string, string> definitions = new(StringComparer.Ordinal);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT name, sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%';";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            definitions.Add(reader.GetString(0), GrimoireSqlNormalizer.Normalize(reader.GetString(1)));
        }

        return definitions;
    }

    private static Task SeedAsync(SqliteConnection connection) => ExecuteAsync(connection, """
        INSERT INTO lexicon_entries (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt)
        VALUES ('entry', 'Inherited', 'inherited', 'person', '[]', 'inherited', '2026-01-01T00:00:00.0000000Z');
        INSERT INTO annal_claims (ClaimId, SubjectStoreCode, SubjectId, CreatedAtUtc)
        VALUES ('claim', 2, 'entry', '2026-01-01T00:00:00.0000000Z');
        INSERT INTO annal_versions
            (VersionId, ClaimId, Revision, OperationCode, OriginCode, ScopeKindCode,
             SensitivityCode, ContentHash, ValidFromUtc, RecordedAtUtc)
        VALUES ('v1', 'claim', 1, 1, 4, 1, 0, zeroblob(32),
                '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
        """);

    private static Task<object?> HitsAsync(SqliteConnection connection, string term) =>
        ScalarAsync(connection, $"SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH '{term}'");

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

        object? result = await command.ExecuteScalarAsync();

        return result is DBNull ? null : result;
    }
}
