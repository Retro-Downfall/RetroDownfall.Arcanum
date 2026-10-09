using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Core version 13: the erasure evidence tables, the unfolded-disclosure index, and Lexicon FTS
/// secure delete, reached the same way from a fresh install and from a version-12 installation.
/// </summary>
/// <remarks>
/// Each case installs to the head this binary declares, so the version-12 installation crosses every
/// later step as well. The one-step, no-sweep case first verifies the exact version-13 transition;
/// the helper completes any later bounded sweep before asserting the installation reached the head.
/// </remarks>
public sealed class MemoryErasureSchemaEvolutionTests
{
    private const string Marker = "zqxerasuremarkerqz";

    private static readonly LexiconCurationScope Global = new(LexiconScopeKind.Global, null);

    static MemoryErasureSchemaEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task Fresh_and_evolved_version_thirteen_catalogs_have_identical_definitions()
    {
        IReadOnlyDictionary<string, string> fresh = await DefinitionsAsync(evolve: false);

        IReadOnlyDictionary<string, string> evolved = await DefinitionsAsync(evolve: true);

        foreach (string name in (string[])
        [
            "memory_erasure_fingerprints",
            "memory_erasure_receipts",
            "memory_erasure_receipt_subjects",
            "memory_erasure_receipts_guard_update",
            "idx_memory_erasure_fingerprints_store_key",
            "idx_memory_erasure_receipts_store_key",
            "idx_memory_erasure_receipts_pending",
            "idx_memory_erasure_receipt_subjects_digest",
            "idx_disclosure_subject_state_unfolded",
            // Version 14, which the evolved catalog reaches through the same chain.
            "IX_attachment_memory_consultations_SessionId_Norm",
            "IX_saga_extraction_watermarks_SessionId_Norm",
            "IX_SessionContextPins_SessionId_Norm",
        ])
        {
            Assert.Contains(name, fresh.Keys);
        }

        Assert.Equal(fresh.Keys.Order(StringComparer.Ordinal), evolved.Keys.Order(StringComparer.Ordinal));

        foreach ((string name, string definition) in fresh)
        {
            Assert.Equal(definition, evolved[name]);
        }
    }

    [Fact]
    public async Task Version_thirteen_evolves_in_one_step_with_no_sweep_and_empty_evidence_tables()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, CoreSchemaVersionTwelveFixture.ChainSet(), 12);

        GrimoireSchemaInstallResult versionThirteen = await GrimoireSchemaTestInstaller.InstallAsync(
            connection, CoreSchemaVersionThirteenFixture.ChainSet(), 1536, CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, versionThirteen.Core.Health);

        Assert.Equal(13, versionThirteen.Core.SchemaVersion);

        Assert.Null(await GrimoireSchemaTransitionJournal.ReadAsync(
            connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None));

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        Assert.Equal(
            0L,
            await ScalarAsync(
                connection,
                "SELECT count(*) FROM grimoire_schema_transitions WHERE FamilyCode = 0 AND TransactionTierCode = 0;"));

        foreach (string table in (string[])
            ["memory_erasure_fingerprints", "memory_erasure_receipts", "memory_erasure_receipt_subjects"])
        {
            Assert.Equal(0L, await ScalarAsync(connection, $"SELECT count(*) FROM {table};"));
        }
    }

    /// <summary>
    /// A token a pre-version-13 build left behind in the Lexicon index's segments is gone after the
    /// upgrade, even though nothing indexes the row that carried it.
    /// </summary>
    /// <remarks>
    /// Three FTS transactions stay below the automerge threshold, so without the one-time merge the
    /// insert's segment and the tombstones the correction and retirement wrote would all still be on
    /// disk. The positive control proves the marker really is there before the upgrade runs.
    /// </remarks>
    [Fact]
    public async Task Lexicon_residue_from_before_version_thirteen_leaves_no_token_after_the_upgrade()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, CoreSchemaVersionTwelveFixture.ChainSet(), 12);

        // A pre-version-13 build never enabled it, and this build's initializer already has.
        await ExecuteAsync(connection, "INSERT INTO lexicon_fts(lexicon_fts, rank) VALUES('secure-delete', 0);");

        await using (ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file))
        {
            LexiconService service = LexiconMidUpgradeCompatibilityTests.CreateService(db);

            var upserted = await service.UpsertAsync("Residue", "Concept", [$"holds {Marker}"], LexiconScope.Global);

            Assert.True(upserted.IsSuccess, upserted.Error.Message);

            var shown = await service.ShowExactAsync(Global, "Residue", null);

            Assert.True(shown.IsSuccess, shown.Error.Message);

            var corrected = await service.CorrectAsync(shown.Value.Value.Target, new("Concept", ["replaced fact"]), null);

            Assert.True(corrected.IsSuccess, corrected.Error.Message);

            var reshown = await service.ShowExactAsync(Global, "Residue", null);

            Assert.True(reshown.IsSuccess, reshown.Error.Message);

            var retired = await service.RetireAsync(reshown.Value.Value.Target, null);

            Assert.True(retired.IsSuccess, retired.Error.Message);
        }

        Assert.True(await ShadowCountAsync(connection, Marker) > 0);

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        Assert.Equal(0L, await ShadowCountAsync(connection, Marker));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT v FROM lexicon_fts_config WHERE k = 'secure-delete';"));

        Assert.Equal(
            0L,
            await ScalarAsync(connection, $"SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH '{Marker}';"));
    }

    [Fact]
    public async Task Core_install_converges_lexicon_secure_delete()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT v FROM lexicon_fts_config WHERE k = 'secure-delete';"));

        await ExecuteAsync(connection, "INSERT INTO lexicon_fts(lexicon_fts, rank) VALUES('secure-delete', 0);");

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT v FROM lexicon_fts_config WHERE k = 'secure-delete';"));

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT v FROM lexicon_fts_config WHERE k = 'secure-delete';"));

        // The one-time merge belongs to the version-13 step alone; converging it on every install would
        // rewrite the whole index each time the Grimoire opens.
        string initializer = await File.ReadAllTextAsync(Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/CoreGrimoireSchemaDataInitializer.cs"));

        Assert.DoesNotContain("optimize", initializer, StringComparison.Ordinal);
    }

    private static async Task InstallAsync(SqliteConnection connection, GrimoireSchemaVersionChainSet chains, int version)
    {
        GrimoireSchemaInstaller installer = GrimoireSchemaTestInstaller.Create(chains);

        GrimoireSchemaInitializationContext context = GrimoireSchemaTestInstaller.CreateContext();

        GrimoireSchemaInstallResult result = await installer.InstallAsync(
            connection, 1536, context, CancellationToken.None);

        if (result.Core.Health is GrimoireSchemaTierHealth.TransitionIncomplete)
        {
            GrimoireSchemaTransitionJournalRow journal = Assert.IsType<GrimoireSchemaTransitionJournalRow>(
                await GrimoireSchemaTransitionJournal.ReadAsync(
                    connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None));

            GrimoireSchemaBackfillRunner runner = new(installer, TimeProvider.System);

            _ = await runner.AdvanceAsync(
                connection,
                chains.ForTier(GrimoireSchemaTransactionTier.Core),
                journal,
                context,
                maxBatches: 128,
                CancellationToken.None);

            result = await installer.InstallAsync(connection, 1536, context, CancellationToken.None);
        }

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

        Assert.Equal(version, result.Core.SchemaVersion);
    }

    private static async Task<IReadOnlyDictionary<string, string>> DefinitionsAsync(bool evolve)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        if (evolve)
        {
            await InstallAsync(connection, CoreSchemaVersionTwelveFixture.ChainSet(), 12);
        }

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

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

    /// <summary>
    /// Counts the FTS5 shadow rows that still carry <paramref name="marker"/>: segment pages in
    /// <c>lexicon_fts_data</c> and segment-index terms in <c>lexicon_fts_idx</c>.
    /// </summary>
    private static async Task<long> ShadowCountAsync(SqliteConnection connection, string marker)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT (SELECT count(*) FROM lexicon_fts_data WHERE instr(block, CAST($m AS BLOB)) > 0)
                 + (SELECT count(*) FROM lexicon_fts_idx WHERE instr(term, CAST($m AS BLOB)) > 0);
            """;

        _ = command.Parameters.AddWithValue("$m", marker);

        return (long)(await command.ExecuteScalarAsync())!;
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

        object? result = await command.ExecuteScalarAsync();

        return result is DBNull ? null : result;
    }
}
