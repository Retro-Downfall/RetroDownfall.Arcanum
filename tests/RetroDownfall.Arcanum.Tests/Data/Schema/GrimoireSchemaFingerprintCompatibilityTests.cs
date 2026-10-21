using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Source edits are admitted against the identity a real installation recorded, while historical
/// reconstructions retain the algorithm each version originally published.
/// </summary>
public sealed class GrimoireSchemaFingerprintCompatibilityTests
{
    static GrimoireSchemaFingerprintCompatibilityTests() => SqliteNativeRuntime.Instance.Initialize();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_installed_core_reopens_after_comment_or_whitespace_only_source_edits(bool commentsOnly)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        (string[] Metadata, string[] Catalog, string[] Data) before;

        await using (SqliteConnection first = await file.OpenAsync(CancellationToken.None))
        {
            GrimoireSchemaInstallResult installed = await InstallAsync(first, GrimoireSchemaVersionChains.Default);

            Assert.Equal(GrimoireSchemaTierHealth.Healthy, installed.Core.Health);

            await SeedSessionAsync(first);

            before = await SnapshotAsync(first);
        }

        IReadOnlyList<GrimoireSchemaObject> edited =
        [
            .. GrimoireSchemaCatalog.CoreObjects.Select(definition => definition with
            {
                Sql = commentsOnly
                    ? "-- revised documentation\n/* no schema change */\n" + definition.Sql + "\n-- end documentation\n"
                    : "\n\n" + string.Join("\n", definition.Sql.Split('\n').Select(static line => "    " + line)) + "\n\n",
            }),
        ];

        // The edited binary recomputes its identity; it must not borrow the recorded fingerprint.
        GrimoireSchemaVersionChainSet editedChains = WithCoreDefinitions(edited);

        await using SqliteConnection reopened = await file.OpenAsync(CancellationToken.None);

        // Reopening unchanged source succeeds first, including under the raw-hash negative control.
        GrimoireSchemaInstallResult unchanged = await InstallAsync(reopened, GrimoireSchemaVersionChains.Default);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, unchanged.Core.Health);

        await AssertSnapshotAsync(reopened, before);

        GrimoireSchemaInstallResult converged = await InstallAsync(reopened, editedChains);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, converged.Core.Health);

        Assert.Equal(GrimoireSchemaVersionChains.CoreSchemaVersion, converged.Core.SchemaVersion);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, converged.CovenantCanonical.Health);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, converged.CovenantAccelerator.Health);

        await AssertSnapshotAsync(reopened, before);

        Assert.Null(await GrimoireSchemaEvolutionInstallerTests.ReadJournalAsync(reopened));
    }

    [Fact]
    public async Task A_meaningful_source_edit_at_the_installed_version_is_refused_without_mutation()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        (string[] Metadata, string[] Catalog, string[] Data) before;

        await using (SqliteConnection first = await file.OpenAsync(CancellationToken.None))
        {
            _ = await InstallAsync(first, GrimoireSchemaVersionChains.Default);

            await SeedSessionAsync(first);

            before = await SnapshotAsync(first);
        }

        IReadOnlyList<GrimoireSchemaObject> changed =
        [
            .. GrimoireSchemaCatalog.CoreObjects.Select(static definition => definition.Name == "Sessions"
                ? definition with { Sql = definition.Sql + "\nCREATE INDEX IF NOT EXISTS ix_fingerprint_regression ON Sessions(Status);\n" }
                : definition),
        ];

        GrimoireSchemaVersionChainSet changedChains = WithCoreDefinitions(changed);

        Assert.NotEqual(
            GrimoireSchemaCatalog.CoreSchemaFingerprint,
            changedChains.ForTier(GrimoireSchemaTransactionTier.Core).HeadManifest.SourceDefinitionFingerprint);

        await using SqliteConnection reopened = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaRefusedException refused = await Assert.ThrowsAsync<GrimoireSchemaRefusedException>(
            () => InstallAsync(reopened, changedChains));

        Assert.Equal(GrimoireSchemaTransactionTier.Core, refused.Tier);

        Assert.Equal(GrimoireSchemaTierHealth.SourceDefinitionMismatch, refused.Health);

        await AssertSnapshotAsync(reopened, before);

        Assert.Null(await GrimoireSchemaEvolutionInstallerTests.ReadJournalAsync(reopened));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public void Every_historical_core_reconstruction_matches_its_published_chain(int version)
    {
        (IReadOnlyList<GrimoireSchemaObject> objects, string fingerprint) = version switch
        {
            1 => (CoreSchemaVersionOneFixture.Objects, CoreSchemaVersionOneFixture.Fingerprint),
            2 => (CoreSchemaVersionTwoFixture.Objects, CoreSchemaVersionTwoFixture.Fingerprint),
            3 => (CoreSchemaVersionThreeFixture.Objects, CoreSchemaVersionThreeFixture.Fingerprint),
            4 => (CoreSchemaVersionFourFixture.Objects, CoreSchemaVersionFourFixture.Fingerprint),
            5 => (CoreSchemaVersionFiveFixture.Objects, CoreSchemaVersionFiveFixture.Fingerprint),
            6 => (CoreSchemaVersionSixFixture.Objects, CoreSchemaVersionSixFixture.Fingerprint),
            7 => (CoreSchemaVersionSevenFixture.Objects, CoreSchemaVersionSevenFixture.Fingerprint),
            8 => (CoreSchemaVersionEightFixture.Objects, CoreSchemaVersionEightFixture.Fingerprint),
            9 => (CoreSchemaVersionNineFixture.Objects, CoreSchemaVersionNineFixture.Fingerprint),
            10 => (CoreSchemaVersionTenFixture.Objects, CoreSchemaVersionTenFixture.Fingerprint),
            11 => (CoreSchemaVersionElevenFixture.Objects, CoreSchemaVersionElevenFixture.Fingerprint),
            12 => (CoreSchemaVersionTwelveFixture.Objects, CoreSchemaVersionTwelveFixture.Fingerprint),
            13 => (CoreSchemaVersionThirteenFixture.Objects, CoreSchemaVersionThirteenFixture.Fingerprint),
            14 => (CoreSchemaVersionFourteenFixture.Objects, CoreSchemaVersionFourteenFixture.Fingerprint),
            15 => (CoreSchemaVersionFifteenFixture.Objects, CoreSchemaVersionFifteenFixture.Fingerprint),
            16 => (CoreSchemaVersionSixteenFixture.Objects, CoreSchemaVersionSixteenFixture.Fingerprint),
            17 => (CoreSchemaVersionSeventeenFixture.Objects, CoreSchemaVersionSeventeenFixture.Fingerprint),
            18 => (CoreSchemaVersionEighteenFixture.Objects, CoreSchemaVersionEighteenFixture.Fingerprint),
            19 => (CoreSchemaVersionNineteenFixture.Objects, CoreSchemaVersionNineteenFixture.Fingerprint),
            _ => throw new ArgumentOutOfRangeException(nameof(version)),
        };

        string recomputed = version <= 5
            ? GrimoireSchemaCatalog.ComputeRawSourceFingerprint(objects)
            : GrimoireSchemaCatalog.ComputeSourceFingerprint(objects);

        Assert.Equal(recomputed, fingerprint);

        Assert.Equal(
            fingerprint,
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core)
                .SourceDefinitionFingerprintFor(version));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Every_historical_covenant_reconstruction_matches_its_published_chain(int version)
    {
        (IReadOnlyList<GrimoireSchemaObject> objects, string fingerprint) = version switch
        {
            1 => (CovenantCanonicalSchemaVersionOneFixture.Objects, CovenantCanonicalSchemaVersionOneFixture.Fingerprint),
            2 => (CovenantCanonicalSchemaVersionTwoFixture.Objects, CovenantCanonicalSchemaVersionTwoFixture.Fingerprint),
            3 => (CovenantCanonicalSchemaVersionThreeFixture.Objects, CovenantCanonicalSchemaVersionThreeFixture.Fingerprint),
            4 => (CovenantCanonicalSchemaVersionFourFixture.Objects, CovenantCanonicalSchemaVersionFourFixture.Fingerprint),
            5 => (CovenantCanonicalSchemaVersionFiveFixture.Objects, CovenantCanonicalSchemaVersionFiveFixture.Fingerprint),
            _ => throw new ArgumentOutOfRangeException(nameof(version)),
        };

        Assert.Equal(GrimoireSchemaCatalog.ComputeRawSourceFingerprint(objects), fingerprint);

        Assert.Equal(
            fingerprint,
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical)
                .SourceDefinitionFingerprintFor(version));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Each_current_tier_publishes_the_computation_its_installed_metadata_requires(int tierCode)
    {
        GrimoireSchemaTransactionTier tier = (GrimoireSchemaTransactionTier)tierCode;

        (IReadOnlyList<GrimoireSchemaObject> objects, string published) = tier switch
        {
            GrimoireSchemaTransactionTier.Core =>
                (GrimoireSchemaCatalog.CoreObjects, GrimoireSchemaCatalog.CoreSchemaFingerprint),
            GrimoireSchemaTransactionTier.CovenantCanonical =>
                (GrimoireSchemaCatalog.CovenantCanonicalObjects, GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint),
            GrimoireSchemaTransactionTier.CovenantAccelerator =>
                (GrimoireSchemaCatalog.CovenantAcceleratorObjects, GrimoireSchemaCatalog.CovenantAcceleratorSchemaFingerprint),
            _ => throw new ArgumentOutOfRangeException(nameof(tierCode)),
        };

        string computed = tier == GrimoireSchemaTransactionTier.Core
            ? GrimoireSchemaCatalog.ComputeSourceFingerprint(objects)
            : GrimoireSchemaCatalog.ComputeRawSourceFingerprint(objects);

        Assert.Equal(computed, published);

        GrimoireSchemaVersionChain chain = GrimoireSchemaVersionChains.Default.ForTier(tier);

        Assert.Equal(computed, chain.SourceDefinitionFingerprintFor(chain.HeadVersion));
    }

    private static GrimoireSchemaVersionChainSet WithCoreDefinitions(IReadOnlyList<GrimoireSchemaObject> definitions)
    {
        GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        return new GrimoireSchemaVersionChainSet(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    core.HeadVersion,
                    GrimoireSchemaCatalog.ComputeSourceFingerprint(definitions),
                    definitions),
                definitions,
                core.Steps),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);
    }

    private static Task<GrimoireSchemaInstallResult> InstallAsync(
        SqliteConnection connection,
        GrimoireSchemaVersionChainSet chains) =>
        GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 1536, CancellationToken.None);

    private static async Task SeedSessionAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO Sessions (Id, Status, CreatedAt, UpdatedAt)
            VALUES ('B0000000-0000-4000-8000-000000000224', 'active',
                    '2026-10-21T00:00:00.0000000Z', '2026-10-21T00:00:00.0000000Z');
            """;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<(string[] Metadata, string[] Catalog, string[] Data)> SnapshotAsync(SqliteConnection connection) =>
        (
            await ReadRowsAsync(connection, "SELECT * FROM grimoire_feature_schemas ORDER BY TransactionTierCode;"),
            await ReadRowsAsync(connection, "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name;"),
            await ReadRowsAsync(connection, "SELECT * FROM Sessions ORDER BY Id;")
        );

    private static async Task AssertSnapshotAsync(
        SqliteConnection connection,
        (string[] Metadata, string[] Catalog, string[] Data) expected)
    {
        (string[] metadata, string[] catalog, string[] data) = await SnapshotAsync(connection);

        Assert.Equal(expected.Metadata, metadata);

        Assert.Equal(expected.Catalog, catalog);

        Assert.Equal(expected.Data, data);
    }

    private static async Task<string[]> ReadRowsAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        List<string> rows = [];

        while (await reader.ReadAsync(CancellationToken.None))
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index)
                    ? "<null>"
                    : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
        }

        return [.. rows];
    }
}
