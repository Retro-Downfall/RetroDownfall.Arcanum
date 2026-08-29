using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The step that gives Saga curation its storage: two nullable lifecycle columns on
/// <c>saga_memories</c> and the two keyed retirement-evidence tables, which the curation verbs write.
/// </summary>
/// <remarks>
/// Version 4 both edits an existing object and adds two new ones, which is the one shape a fresh
/// installation and an evolved one can still disagree about even when every transition statement is
/// correct: <c>saga_memories</c> reaches an evolved installation through two <c>ALTER TABLE</c>
/// statements rather than the <c>CREATE TABLE</c> a fresh install runs, and SQLite stores the two
/// results as different text unless the head file is laid out in the exact shape <c>ALTER</c> produces.
/// </remarks>
public sealed class SagaCurationEvolutionTests
{

    static SagaCurationEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    /// <summary>
    /// The pin is a literal captured before the version-3 tree was edited, and nothing can recompute it
    /// from a tree that no longer exists. Reconstructing that tree and hashing it is the only check that
    /// the pinned value is the one version 3 actually published — and a wrong pin means every version-3
    /// installation refuses the upgrade with <c>SourceDefinitionMismatch</c>.
    /// </summary>
    [Fact]
    public void Version_three_reconstruction_matches_the_pinned_fingerprint()
    {

        Assert.Equal(
            "2CC5BB384111470F86668C4928B54306C7B8F7DCFDBBB152DF9F7C0CF162CC2F",
            CoreSchemaVersionThreeFixture.Fingerprint);

        GrimoireSchemaVersionChain core =
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        Assert.Equal(CoreSchemaVersionThreeFixture.Fingerprint, core.SourceDefinitionFingerprintFor(3));

    }

    /// <summary>
    /// The one failure this step could produce that no unit test would see: an evolved installation
    /// whose stored <c>saga_memories</c> declaration differs from the head file by so much as a space,
    /// because <c>ALTER TABLE ... ADD COLUMN</c> never re-runs the head <c>CREATE TABLE</c> text.
    /// </summary>
    [Fact]
    public async Task Evolving_a_version_three_installation_reaches_the_shipped_version_four_tree()
    {

        IReadOnlyDictionary<string, string> evolved = await SagaCurationDefinitionsAsync(evolve: true);

        IReadOnlyDictionary<string, string> fresh = await SagaCurationDefinitionsAsync(evolve: false);

        Assert.NotEmpty(fresh);

        Assert.Equal(
            fresh.Keys.OrderBy(static name => name, StringComparer.Ordinal),
            evolved.Keys.OrderBy(static name => name, StringComparer.Ordinal));

        foreach ((string name, string definition) in fresh)
        {

            Assert.Equal(
                GrimoireSqlNormalizer.Normalize(definition),
                GrimoireSqlNormalizer.Normalize(evolved[name]));

        }

    }

    /// <summary>
    /// A memory an operator never curated stays exactly that after the upgrade: nothing retired it and
    /// nothing pinned it, because the step adds storage and writes to none of it.
    /// </summary>
    [Fact]
    public async Task Version_four_adds_the_two_lifecycle_columns_and_leaves_every_row_active()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionThreeFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, installed.Core.Health);

        Assert.Equal(3, installed.Core.SchemaVersion);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode)
            VALUES ('m-1', 'a memory written before curation existed', '2026-01-01T00:00:00.0000000+00:00', 1)
            """);

        // Evolved to version 4 rather than to head, because this case is about what version 4 does to a
        // version-3 row. A later version carries a sweep, so an upgrade to head stops at
        // TransitionIncomplete until the shipped driver drains it, and asserting Healthy here would be
        // asserting something about that later step rather than about the lifecycle columns.
        GrimoireSchemaInstallResult evolved = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionFourFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, evolved.Core.Health);

        Assert.Equal(4, evolved.Core.SchemaVersion);

        Assert.Null(await ScalarAsync(connection, "SELECT RetiredAtUtc FROM saga_memories WHERE Id = 'm-1'"));

        Assert.Null(await ScalarAsync(connection, "SELECT PinnedAtUtc FROM saga_memories WHERE Id = 'm-1'"));

    }

    /// <summary>
    /// Every stored definition version 4 touches, evolved and fresh. Widened past an exact name list so
    /// the two new tables' own definitions — not just <c>saga_memories</c> — are covered, the same way
    /// <c>CovenantCurationEvolutionTests</c> widens its own <c>LIKE</c> filter past the object it names.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> SagaCurationDefinitionsAsync(bool evolve)
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        if (evolve)
        {

            GrimoireSchemaInstallResult seed = await GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                CoreSchemaVersionThreeFixture.ChainSet(),
                1536,
                CancellationToken.None);

            // If this fixture ever regressed to producing a version-4 tree, the "evolved" arm below
            // would install nothing further and the comparison against "fresh" would pass vacuously —
            // exactly the failure mode this precondition rules out.
            Assert.Equal(3, seed.Core.SchemaVersion);

        }

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            GrimoireSchemaVersionChains.Default,
            1536,
            CancellationToken.None);

        Dictionary<string, string> definitions = new(StringComparer.Ordinal);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT name, sql FROM sqlite_master
            WHERE name LIKE '%saga_memories%'
               OR name LIKE '%saga_retirement_suppressions%'
               OR name LIKE '%saga_suppression_key%';
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {

            if (!reader.IsDBNull(1))
            {

                definitions[reader.GetString(0)] = reader.GetString(1);

            }

        }

        return definitions;

    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    /// <summary>
    /// <see cref="SqliteDataReader"/> hands back <see cref="DBNull"/> for a SQL <c>NULL</c>, and a
    /// caller asserting the column is unset wants the CLR null that actually means.
    /// </summary>
    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? result = await command.ExecuteScalarAsync(CancellationToken.None);

        return result is DBNull ? null : result;

    }

}
