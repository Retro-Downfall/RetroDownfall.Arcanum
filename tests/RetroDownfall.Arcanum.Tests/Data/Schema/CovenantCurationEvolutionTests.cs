using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The step that gives the Covenant canonical tier its curation substrate.
/// </summary>
/// <remarks>
/// Version 2 adds objects and edits none, which is what lets a fresh installation and an evolved one
/// agree: <c>CREATE TABLE</c> stores its statement verbatim, so the two trees describe the same text
/// as long as each transition file carries its head file's statement character for character.
/// </remarks>
public sealed class CovenantCurationEvolutionTests
{

    /// <summary>
    /// The pin is a literal captured before the version-1 tree was edited, and nothing can recompute it
    /// from a tree that no longer exists. A wrong pin means every version-1 installation refuses the
    /// upgrade with <c>SourceDefinitionMismatch</c>, so it has to fail here instead.
    /// </summary>
    [Fact]
    public void The_shipped_chain_pins_the_fingerprint_the_version_one_tree_published()
    {

        GrimoireSchemaVersionChain canonical =
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical);

        Assert.Equal(
            CovenantCanonicalSchemaVersionOneFixture.Fingerprint,
            canonical.SourceDefinitionFingerprintFor(1));

    }

    /// <summary>
    /// The head fingerprint has to answer for the head version, or an installation already at version 2
    /// is compared against the tree of the version below it.
    /// </summary>
    [Fact]
    public void The_head_fingerprint_answers_for_version_two()
    {

        GrimoireSchemaVersionChain canonical =
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical);

        Assert.Equal(2, canonical.HeadVersion);

        Assert.Equal(
            GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint,
            canonical.SourceDefinitionFingerprintFor(2));

    }

    /// <summary>
    /// The reconstruction has to remove something. A filter that matched nothing would make the pin
    /// assertion above a comparison of the head tree against itself, which passes whatever the pin says.
    /// </summary>
    [Fact]
    public void The_version_one_reconstruction_is_smaller_than_the_head_tree()
    {

        Assert.True(
            CovenantCanonicalSchemaVersionOneFixture.Objects.Count
                < GrimoireSchemaCatalog.CovenantCanonicalObjects.Count,
            "The version-1 reconstruction removed no object, so it cannot describe version 1.");

        Assert.NotEqual(
            CovenantCanonicalSchemaVersionOneFixture.Fingerprint,
            GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint);

    }

    /// <summary>
    /// The one failure this step could produce and no unit test would see: an evolved installation whose
    /// stored definitions differ from the head files by so much as a space.
    /// </summary>
    /// <remarks>
    /// <c>GrimoireSchemaManifestInspector</c> compares normalized <c>sqlite_master</c> text against the
    /// normalized head files, and the evolve path never re-runs head DDL — it runs the step's statements
    /// and then inspects. A developer's own database is always fresh, so this is the shape of drift that
    /// reaches every operator and no author. Installing version 1, evolving it, and comparing the result
    /// against a database that only ever knew version 2 is the only thing that catches it.
    /// </remarks>
    [Fact]
    public async Task An_evolved_installation_stores_the_same_definitions_as_a_fresh_one()
    {

        IReadOnlyDictionary<string, string> evolved = await CurationDefinitionsAsync(evolve: true);

        IReadOnlyDictionary<string, string> fresh = await CurationDefinitionsAsync(evolve: false);

        Assert.NotEmpty(fresh);

        Assert.Equal(fresh.Keys.OrderBy(static name => name, StringComparer.Ordinal), evolved.Keys.OrderBy(static name => name, StringComparer.Ordinal));

        foreach ((string name, string definition) in fresh)
        {

            Assert.Equal(
                GrimoireSqlNormalizer.Normalize(definition),
                GrimoireSqlNormalizer.Normalize(evolved[name]));

        }

    }

    /// <summary>
    /// Health is the verdict the inspector reaches over those definitions, and it is what an operator's
    /// host acts on. An evolved tier that inspected as anything but healthy would refuse Covenant work.
    /// </summary>
    [Fact]
    public async Task An_evolved_canonical_tier_reports_healthy_at_version_two()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult first = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CovenantCanonicalSchemaVersionOneFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, first.CovenantCanonical.Health);

        Assert.Equal(1, first.CovenantCanonical.SchemaVersion);

        GrimoireSchemaInstallResult evolved = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            GrimoireSchemaVersionChains.Default,
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, evolved.CovenantCanonical.Health);

        Assert.Equal(2, evolved.CovenantCanonical.SchemaVersion);

    }

    private static async Task<IReadOnlyDictionary<string, string>> CurationDefinitionsAsync(bool evolve)
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        if (evolve)
        {

            _ = await GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                CovenantCanonicalSchemaVersionOneFixture.ChainSet(),
                1536,
                CancellationToken.None);

        }

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            GrimoireSchemaVersionChains.Default,
            1536,
            CancellationToken.None);

        Dictionary<string, string> definitions = new(StringComparer.Ordinal);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT name, sql FROM sqlite_master WHERE name LIKE 'covenant_curation%' OR name LIKE '%covenant_curation%';";

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

}
