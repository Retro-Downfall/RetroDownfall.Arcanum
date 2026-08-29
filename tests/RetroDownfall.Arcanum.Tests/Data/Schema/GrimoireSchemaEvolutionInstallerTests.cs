using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Evolution as a caller reaches it: install one chain through the real installer, then hand the same
/// installer a longer chain for the same tier against the same file.
/// </summary>
/// <remarks>
/// Nothing here writes a <c>grimoire_feature_schemas</c> row or a journal row to set up a state it
/// then asserts. Every starting state is produced by a real install, because a suite that seeds the
/// precondition it is testing can never discover that production cannot reach that state.
/// </remarks>
public sealed class GrimoireSchemaEvolutionInstallerTests
{

    static GrimoireSchemaEvolutionInstallerTests() => SqliteNativeRuntime.Instance.Initialize();

    /// <summary>
    /// A fresh install runs no step at all: the head tree's <c>IF NOT EXISTS</c> statements build the
    /// head shape directly, whatever version that head happens to be.
    /// </summary>
    [Fact]
    public async Task A_fresh_install_of_a_two_version_chain_records_head_and_opens_no_run()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult result = await InstallAsync(
            connection,
            GrimoireSchemaEvolutionFixture.TwoVersionChainSet());

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

        Assert.Equal(2, result.Core.SchemaVersion);

        Assert.True(await TableExistsAsync(connection, "evolution_target"));

        Assert.Null(await ReadJournalAsync(connection));

    }

    [Fact]
    public async Task A_backfill_free_step_evolves_the_tier_and_records_head()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection first = await file.OpenAsync(CancellationToken.None))
        {

            GrimoireSchemaInstallResult installed = await InstallAsync(
                first,
                GrimoireSchemaEvolutionFixture.OneVersionChainSet());

            Assert.Equal(GrimoireSchemaTierHealth.Healthy, installed.Core.Health);

            Assert.Equal(1, installed.Core.SchemaVersion);

            Assert.False(await TableExistsAsync(first, "evolution_target"));

        }

        await using SqliteConnection second = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult evolved = await InstallAsync(
            second,
            GrimoireSchemaEvolutionFixture.TwoVersionChainSet());

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, evolved.Core.Health);

        Assert.Equal(2, evolved.Core.SchemaVersion);

        Assert.True(await TableExistsAsync(second, "evolution_target"));

        Assert.Equal(2, await RecordedVersionAsync(second));

        Assert.Null(await ReadJournalAsync(second));

    }

    /// <summary>
    /// A step that depends on a sweep commits its DDL and stops. The version is not recorded, because
    /// the work the version promises has not been done.
    /// </summary>
    [Fact]
    public async Task A_step_with_a_backfill_commits_its_ddl_and_stops_at_the_journal()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection first = await file.OpenAsync(CancellationToken.None))
        {

            _ = await InstallAsync(first, GrimoireSchemaEvolutionFixture.OneVersionChainSet());

        }

        await using SqliteConnection second = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult result = await InstallAsync(
            second,
            GrimoireSchemaEvolutionFixture.TwoVersionChainSet(new TestBackfill("fill-target")));

        Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, result.Core.Health);

        Assert.Equal("Grimoire.Schema.TransitionIncomplete", result.Core.DiagnosticCode);

        // The DDL committed, so the object is there...
        Assert.True(await TableExistsAsync(second, "evolution_target"));

        // ...and the version deliberately did not move.
        Assert.Equal(1, await RecordedVersionAsync(second));

        GrimoireSchemaTransitionJournalRow? journal = await ReadJournalAsync(second);

        Assert.NotNull(journal);

        Assert.Equal("fill-target", journal.BackfillName);

        Assert.Equal(1, journal.CompletedThroughVersion);

        Assert.Equal(2, journal.TargetVersion);

    }

    /// <summary>
    /// The single easiest thing to get wrong: a pending sweep means that step's DDL already
    /// committed, so resuming must not run it again. Re-running <c>CREATE TABLE</c> without
    /// <c>IF NOT EXISTS</c> - or any real step's <c>ALTER TABLE ADD COLUMN</c> - throws, and on Core
    /// that would abort startup and make the sweep unrunnable by the only process able to run it.
    /// </summary>
    [Fact]
    public async Task Resuming_a_pending_sweep_does_not_re_execute_its_committed_ddl()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection first = await file.OpenAsync(CancellationToken.None))
        {

            _ = await InstallAsync(first, GrimoireSchemaEvolutionFixture.OneVersionChainSet());

        }

        await using (SqliteConnection second = await file.OpenAsync(CancellationToken.None))
        {

            _ = await InstallAsync(
                second,
                GrimoireSchemaEvolutionFixture.TwoVersionChainSet(new TestBackfill("fill-target")));

        }

        await using SqliteConnection third = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult resumed = await InstallAsync(
            third,
            GrimoireSchemaEvolutionFixture.TwoVersionChainSet(new TestBackfill("fill-target")));

        Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, resumed.Core.Health);

        Assert.Equal(1, await RecordedVersionAsync(third));

    }

    /// <summary>
    /// A Core tier mid-run does not abort startup, and the tiers that depend on it stand down rather
    /// than installing against a catalog between versions.
    /// </summary>
    [Fact]
    public async Task A_core_tier_mid_run_returns_rather_than_throwing_and_stands_its_dependents_down()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection first = await file.OpenAsync(CancellationToken.None))
        {

            _ = await InstallAsync(first, GrimoireSchemaEvolutionFixture.OneVersionChainSet());

        }

        await using SqliteConnection second = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult result = await InstallAsync(
            second,
            GrimoireSchemaEvolutionFixture.TwoVersionChainSet(new TestBackfill("fill-target")));

        Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, result.Core.Health);

        Assert.Equal(GrimoireSchemaTierHealth.DependencyUnavailable, result.CovenantCanonical.Health);

        Assert.Equal(GrimoireSchemaTierHealth.DependencyUnavailable, result.CovenantAccelerator.Health);

    }

    /// <summary>
    /// A database written by a build ahead of this one is refused rather than downgraded, and Core's
    /// refusal aborts startup as it always has.
    /// </summary>
    [Fact]
    public async Task A_version_above_head_is_refused_and_core_throws()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection first = await file.OpenAsync(CancellationToken.None))
        {

            _ = await InstallAsync(first, GrimoireSchemaEvolutionFixture.TwoVersionChainSet());

        }

        await using SqliteConnection second = await file.OpenAsync(CancellationToken.None);

        Exception refused = await Assert.ThrowsAnyAsync<Exception>(
            () => InstallAsync(second, GrimoireSchemaEvolutionFixture.OneVersionChainSet()));

        Assert.Contains("IncompatibleNewerVersion", refused.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// The installed version 1 is not the version 1 this chain knows by that number, so no step runs
    /// against it. This is the whole reason a step carries a pinned fingerprint at all.
    /// </summary>
    [Fact]
    public async Task An_older_version_that_is_not_the_pinned_one_is_refused_and_runs_no_step()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection first = await file.OpenAsync(CancellationToken.None))
        {

            _ = await InstallAsync(first, GrimoireSchemaEvolutionFixture.OneVersionChainSet());

        }

        await using SqliteConnection second = await file.OpenAsync(CancellationToken.None);

        Exception refused = await Assert.ThrowsAnyAsync<Exception>(
            () => InstallAsync(
                second,
                GrimoireSchemaEvolutionFixture.TwoVersionChainSet(
                    "4444444444444444444444444444444444444444444444444444444444444444")));

        Assert.Contains("SourceDefinitionMismatch", refused.Message, StringComparison.Ordinal);

        Assert.False(await TableExistsAsync(second, "evolution_target"));

        Assert.Equal(1, await RecordedVersionAsync(second));

    }

    /// <summary>
    /// A catalog advanced without its metadata is refused rather than stamped.
    /// </summary>
    /// <remarks>
    /// This is the one case in this suite that writes schema metadata directly, and it has to: no
    /// production path can produce it. A failed finalization leaves the journal row, which routes
    /// classification to the journal arm instead; only something outside this engine - a restore, or a
    /// hand edit - can leave objects at head with the metadata behind them and no run recorded. The
    /// test therefore plays that outside actor, and asserts the refusal rather than the state it
    /// wrote.
    /// </remarks>
    [Fact]
    public async Task A_catalog_advanced_without_its_metadata_is_refused_as_mixed()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection first = await file.OpenAsync(CancellationToken.None))
        {

            _ = await InstallAsync(first, GrimoireSchemaEvolutionFixture.OneVersionChainSet());

        }

        await using SqliteConnection second = await file.OpenAsync(CancellationToken.None);

        // A real run gets the catalog to version 2 and stops, because its step depends on a sweep.
        _ = await InstallAsync(
            second,
            GrimoireSchemaEvolutionFixture.TwoVersionChainSet(new TestBackfill("fill-target")));

        // The outside actor: the run's record is gone while its effect on the catalog remains.
        await using (SqliteCommand forget = second.CreateCommand())
        {

            forget.CommandText = "DELETE FROM grimoire_schema_transitions;";

            _ = await forget.ExecuteNonQueryAsync(CancellationToken.None);

        }

        Exception refused = await Assert.ThrowsAnyAsync<Exception>(
            () => InstallAsync(
                second,
                GrimoireSchemaEvolutionFixture.TwoVersionChainSet(new TestBackfill("fill-target"))));

        Assert.Contains("MixedCatalogVersions", refused.Message, StringComparison.Ordinal);

        Assert.Equal(1, await RecordedVersionAsync(second));

    }

    /// <summary>
    /// An object no manifest declares is drift, and evolution does not soften that.
    /// </summary>
    /// <remarks>
    /// The rule is narrower than "any object nobody declared", and stating it exactly is the point of
    /// naming both cases here. An unowned ordinary table is tolerated: a local database may carry an
    /// operator's own scratch table and refusing to open it would be worse than ignoring it. What is
    /// refused is an object that would be <i>trusted by name</i> - a <c>covenant_</c>-prefixed one -
    /// and an explicitly declared index nobody owns, which silently changes what an invariant means.
    /// </remarks>
    [Theory]
    [InlineData("CREATE TABLE covenant_intruder (Id INTEGER PRIMARY KEY);")]
    [InlineData("CREATE INDEX ix_evolution_intruder ON evolution_source (Value);")]
    public async Task An_object_no_manifest_declares_is_refused_as_drift(string intruderSql)
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await InstallAsync(connection, GrimoireSchemaEvolutionFixture.OneVersionChainSet());

        await using (SqliteCommand intruder = connection.CreateCommand())
        {

            intruder.CommandText = intruderSql;

            _ = await intruder.ExecuteNonQueryAsync(CancellationToken.None);

        }

        Exception refused = await Assert.ThrowsAnyAsync<Exception>(
            () => InstallAsync(connection, GrimoireSchemaEvolutionFixture.OneVersionChainSet()));

        Assert.Contains("InstalledCatalogDrift", refused.Message, StringComparison.Ordinal);

    }

    internal static Task<GrimoireSchemaInstallResult> InstallAsync(
        SqliteConnection connection,
        GrimoireSchemaVersionChainSet chains) =>
        GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 1536, CancellationToken.None);

    internal static Task<GrimoireSchemaTransitionJournalRow?> ReadJournalAsync(SqliteConnection connection) =>
        GrimoireSchemaTransitionJournal.ReadAsync(
            connection,
            transaction: null,
            GrimoireSchemaTransactionTier.Core,
            CancellationToken.None);

    internal static async Task<int?> RecordedVersionAsync(SqliteConnection connection)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT SchemaVersion FROM grimoire_feature_schemas WHERE TransactionTierCode = 0;
            """;

        object? result = await command.ExecuteScalarAsync(CancellationToken.None);

        return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);

    }

    internal static async Task<bool> TableExistsAsync(SqliteConnection connection, string name)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """SELECT 1 FROM sqlite_master WHERE "type" = 'table' AND "name" = $name;""";

        _ = command.Parameters.AddWithValue("$name", name);

        return await command.ExecuteScalarAsync(CancellationToken.None) is not (null or DBNull);

    }

}
