using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class GrimoireConnectionAcquisitionInventoryTests
{

    [Fact]
    public void Injected_unlisted_acquisition_fails_independently()
    {

        AcquisitionSource unlisted = Source("""
            using Microsoft.Data.Sqlite;
            sealed class Fixture
            {
                void Open()
                {
                    _ = new SqliteConnection("Data Source=fixture.db");
                }
            }
            """);

        IReadOnlyList<InventoryFailure> failures = GrimoireConnectionAcquisitionScanner.Validate(
            GrimoireConnectionAcquisitionScanner.Discover([unlisted]),
            []);

        Assert.Contains(failures, failure => failure.Code == InventoryFailureCode.UncataloguedDiscovery);

    }

    [Fact]
    public void Stale_catalog_entry_fails_independently()
    {

        AcquisitionIdentity stale = new(
            "Fixtures/Stale.cs",
            "Fixture",
            "Open()",
            AcquisitionConstructKind.ProviderObjectCreation,
            "SqliteConnection",
            1,
            "newSqliteConnection(\"DataSource=stale.db\")");

        IReadOnlyList<InventoryFailure> failures = GrimoireConnectionAcquisitionScanner.Validate(
            [],
            [Entry(stale)]);

        Assert.Contains(failures, failure => failure.Code == InventoryFailureCode.StaleCatalogEntry);

    }

    [Fact]
    public void Misclassified_canonical_live_acquisition_fails_independently()
    {

        AcquisitionSource live = Source("""
            using Microsoft.Data.Sqlite;
            sealed class Fixture
            {
                void Open()
                {
                    _ = new SqliteConnection(ArcanumPaths.GrimoireDatabaseFile);
                }
            }
            """);

        AcquisitionIdentity identity = Assert.Single(GrimoireConnectionAcquisitionScanner.Discover([live]));

        IReadOnlyList<InventoryFailure> failures = GrimoireConnectionAcquisitionScanner.Validate(
            [identity],
            [Entry(
                identity,
                GrimoirePathAuthority.ArchiveOrSnapshot,
                GrimoireAcquisitionKind.StagingOrArchive,
                new(ExactNonServingProofKind.TypedStagingOrSnapshot, "Fixture.Open()"))]);

        Assert.Contains(failures, failure => failure.Code == InventoryFailureCode.InvalidClassification);

    }

    [Fact]
    public void Nested_Task_ValueTask_and_Result_returns_require_a_marker()
    {

        AcquisitionSource source = Source("""
            using System;
            using System.Threading.Tasks;
            sealed class GrimoireConnectionAcquisitionRouteAttribute : Attribute { }
            sealed class Fixture
            {
                Task<Result<IGrimoireOrdinaryConnectionLease>> Unmarked() => throw null!;

                [GrimoireConnectionAcquisitionRoute]
                ValueTask<Result<IStoppedHostGrimoireConnectionLease>> Marked() => throw null!;
            }
            """);

        IReadOnlyList<InventoryFailure> failures =
            GrimoireConnectionAcquisitionScanner.ValidateMarkerCoverage([source]);

        Assert.Equal(
            1,
            failures.Count(failure => failure.Code == InventoryFailureCode.MissingRequiredRouteMarker));

    }

    [Fact]
    public void Duplicate_marked_route_names_fail_independently()
    {

        AcquisitionSource source = Source("""
            using System;
            sealed class GrimoireConnectionAcquisitionRouteAttribute : Attribute { }
            sealed class First
            {
                [GrimoireConnectionAcquisitionRoute]
                IGrimoireOrdinaryConnectionLease Open() => throw null!;
            }
            sealed class Second
            {
                [GrimoireConnectionAcquisitionRoute]
                IGrimoireOrdinaryConnectionLease Open() => throw null!;
            }
            """);

        IReadOnlyList<InventoryFailure> failures =
            GrimoireConnectionAcquisitionScanner.ValidateMarkerCoverage([source]);

        Assert.Contains(failures, failure => failure.Code == InventoryFailureCode.DuplicateMarkedRouteName);

    }

    [Fact]
    public void Exact_non_database_candidate_requires_one_negative_proof()
    {

        AcquisitionSource source = Source("""
            sealed class Fixture
            {
                void Open()
                {
                    _ = new DbConnection("not a database");
                }
            }
            """);

        AcquisitionIdentity identity = Assert.Single(GrimoireConnectionAcquisitionScanner.Discover([source]));

        IReadOnlyList<InventoryFailure> failures = GrimoireConnectionAcquisitionScanner.Validate(
            [identity],
            [Entry(
                identity,
                GrimoirePathAuthority.NotGrimoire,
                GrimoireAcquisitionKind.NonGrimoireCandidate)]);

        Assert.Contains(failures, failure => failure.Code == InventoryFailureCode.MissingNonServingProof);

    }

    [Fact]
    public void Production_inventory_is_bijective()
    {

        IReadOnlyList<InventoryFailure> failures = GrimoireConnectionAcquisitionScanner.Validate(
            GrimoireConnectionAcquisitionScanner.Discover(ProductionSources()),
            GrimoireConnectionAcquisitionScanner.Catalog());

        Assert.True(
            failures.Count == 0,
            string.Join(System.Environment.NewLine, failures.Select(static failure => failure.ToString())));

    }

    private static GrimoireAcquisitionCatalogEntry Entry(
        AcquisitionIdentity identity,
        GrimoirePathAuthority pathAuthority = GrimoirePathAuthority.LiveGrimoire,
        GrimoireAcquisitionKind acquisitionKind = GrimoireAcquisitionKind.ServingRawOrdinary,
        ExactNonServingProof? proof = null) =>
        new(
            identity,
            pathAuthority,
            acquisitionKind,
            pathAuthority == GrimoirePathAuthority.LiveGrimoire
                ? GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory
                : GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            proof);

    private static AcquisitionSource Source(string text) => new("Fixtures/Fixture.cs", text);

    private static IReadOnlyList<AcquisitionSource> ProductionSources()
    {

        Assert.NotEmpty(ProductionSourceInventory.Sources());

        string repositoryRoot = NativeSqlCipherTestPaths.RepositoryRoot();

        string sourceRoot = Path.Combine(repositoryRoot, "src");

        return
        [
            .. Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(static path =>
                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(path => new AcquisitionSource(
                    Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
                    File.ReadAllText(path))),
        ];

    }

}
