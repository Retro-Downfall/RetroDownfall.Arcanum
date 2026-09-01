using System.Reflection;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class GrimoireConnectionAcquisitionInventoryTests
{

    private const int ExpectedProductionAcquisitionCount = 365;

    private static readonly HashSet<(string RelativePath, string EnclosingMember)> ScopedMigrationMembers =
    [
        ("src/RetroDownfall.Arcanum.Api/Health/GrimoireLivenessProbe.cs", "ExecuteProbeAsync(1)"),
        ("src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs", "JoinWorkspaceChunkMetadataAsync(5)"),
        ("src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs", "OpenConnectionAsync(3)"),
        ("src/RetroDownfall.Arcanum.Api/Tower/SessionDivinationEndpoints.cs", "JoinSessionMetadataAsync(7)"),
        ("src/RetroDownfall.Arcanum.Api/Workspaces/WorkspaceDivinationEndpoints.cs", "JoinWorkspaceChunksAsync(6)"),
        ("src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantCampaignScopeProbe.cs", "HasDeletionEventAsync(4)"),
        ("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ICovenantConnectionSource.cs", "GetOpenCoreConnectionAsync(1)"),
        ("src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.TurnCommit.cs", "CommitWithinImmediateTransactionAsync(2)"),
        ("src/RetroDownfall.Arcanum.Infrastructure/Weave/EmbeddingsResetService.cs", "PurgeLabeledKindAsync(2)"),
    ];

    private static readonly (string RelativePath, string EnclosingType, string EnclosingMember)[]
        FreshMigrationMembers =
    [
        ("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "RenewLeaseAsync(5)"),
        ("src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionEntryPersistence.cs", "SessionEntryPersistence", "ReadProbeOnFreshConnectionAsync(2)"),
        ("src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionEntryPersistence.cs", "SessionEntryPersistence", "ReadReceiptOnFreshConnectionAsync(3)"),
        ("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureWriter.cs", "CovenantDisclosureWriter", "OpenVerifiedAsync(3)"),
        ("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureInventorySource.cs", "CovenantErasureInventorySource", "WithOwnedSnapshotAsync(2)"),
        ("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantHealthyCatalogErasureGuard.cs", "CovenantHealthyCatalogErasureGuard", "RequireHealthyAsync(1)"),
    ];

    private static readonly string[] Task9AmbientMaintenanceTestBridge =
    [
        "tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs",
        "tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantCanonicalErasureFixture.cs",
        "tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantCanonicalErasureTransactionTests.cs",
        "tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantDisclosureWriterTests.cs",
        "tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureFreshProcessRecoveryTests.cs",
        "tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureInventorySourceTests.cs",
        "tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs",
        "tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantMaintenanceConnectionFactoryTests.cs",
        "tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantV3MaintenanceTestAuthority.cs",
        "tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs",
        "tests/RetroDownfall.Arcanum.Tests/Fixtures/CovenantSchemaScratchDatabase.cs",
        "tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetCoordinatorTests.cs",
        "tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetDatabaseTests.cs",
    ];

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
    public void Serving_route_requires_live_authority_and_its_matching_runtime_route()
    {

        AcquisitionIdentity identity = Assert.Single(GrimoireConnectionAcquisitionScanner.Discover(
        [
            Source("""
                using Microsoft.Data.Sqlite;
                sealed class Fixture
                {
                    void Open() => _ = new SqliteConnection("Data Source=fixture.db");
                }
                """),
        ]));

        IReadOnlyList<InventoryFailure> failures = GrimoireConnectionAcquisitionScanner.Validate(
            [identity],
            [Entry(
                identity,
                GrimoirePathAuthority.LiveGrimoire,
                GrimoireAcquisitionKind.ServingRawOrdinary,
                runtimeRoute: GrimoireRuntimeAdmissionRoute.ExactNonServingProof)]);

        Assert.Contains(failures, failure => failure.Code == InventoryFailureCode.InvalidClassification);

    }

    [Fact]
    public void Non_serving_authority_requires_its_matching_kind_route_and_proof()
    {

        AcquisitionIdentity identity = Assert.Single(GrimoireConnectionAcquisitionScanner.Discover(
        [
            Source("""
                using Microsoft.Data.Sqlite;
                sealed class Fixture
                {
                    void Open() => _ = new SqliteConnection("Data Source=fixture.db");
                }
                """),
        ]));

        IReadOnlyList<InventoryFailure> failures = GrimoireConnectionAcquisitionScanner.Validate(
            [identity],
            [Entry(
                identity,
                GrimoirePathAuthority.ShutdownGrimoire,
                GrimoireAcquisitionKind.BootstrapOrShutdown,
                new(ExactNonServingProofKind.PreReadinessHeldLock, "Fixture.Open()"),
                GrimoireRuntimeAdmissionRoute.ExactNonServingProof)]);

        Assert.Contains(failures, failure => failure.Code == InventoryFailureCode.InvalidClassification);

    }

    [Fact]
    public void Legacy_v3_maintenance_requires_its_exact_lease_proof()
    {

        AcquisitionIdentity identity = Assert.Single(GrimoireConnectionAcquisitionScanner.Discover(
        [
            Source("""
                using Microsoft.Data.Sqlite;
                sealed class Fixture
                {
                    void Open() => _ = new SqliteConnection("Data Source=fixture.db");
                }
                """),
        ]));

        IReadOnlyList<InventoryFailure> failures = GrimoireConnectionAcquisitionScanner.Validate(
            [identity],
            [Entry(
                identity,
                GrimoirePathAuthority.LiveGrimoire,
                GrimoireAcquisitionKind.LegacyV3Maintenance,
                new(ExactNonServingProofKind.LegacyV3ExclusiveLease, "Fixture.Open()", 124),
                GrimoireRuntimeAdmissionRoute.MaintenanceConnectionFactory)]);

        Assert.Contains(failures, failure => failure.Code == InventoryFailureCode.InvalidClassification);

    }

    [Fact]
    public void Local_function_acquisition_uses_the_local_function_identity()
    {

        AcquisitionIdentity identity = Assert.Single(GrimoireConnectionAcquisitionScanner.Discover(
        [
            Source("""
                using Microsoft.Data.Sqlite;
                sealed class Fixture
                {
                    void Outer()
                    {
                        void Local() => _ = new SqliteConnection("Data Source=fixture.db");
                    }
                }
                """),
        ]));

        Assert.Equal("Local(0)", identity.EnclosingMember);

    }

    [Fact]
    public void Ordinary_factory_marker_declaration_and_route_inventory_are_exact()
    {

        AttributeUsageAttribute usage = Assert.Single(
            typeof(GrimoireConnectionAcquisitionRouteAttribute)
                .GetCustomAttributes<AttributeUsageAttribute>());

        Assert.Equal(AttributeTargets.Method, usage.ValidOn);

        Assert.False(usage.AllowMultiple);

        Assert.False(usage.Inherited);

        IReadOnlyList<AcquisitionSource> sources = ProductionSources();

        IReadOnlyList<AcquisitionIdentity> discoveries =
            GrimoireConnectionAcquisitionScanner.Discover(sources);

        IReadOnlyList<AcquisitionIdentity> ordinaryRoutes =
        [
            .. discoveries.Where(static identity =>
                identity.RelativePath.EndsWith(
                    "/GrimoireOrdinaryConnectionFactory.cs",
                    StringComparison.Ordinal)
                && identity.ConstructKind == AcquisitionConstructKind.MarkedRouteDeclaration),
        ];

        Assert.Equal(
            ["AcquireScopedAsync", "OpenFreshAsync"],
            ordinaryRoutes.Select(static identity => identity.CalleeOrConstructedType).Order());

        IReadOnlyList<InventoryFailure> markerFailures =
            GrimoireConnectionAcquisitionScanner.ValidateMarkerCoverage(
                sources.Where(static source => source.RelativePath.EndsWith(
                    "/GrimoireOrdinaryConnectionFactory.cs",
                    StringComparison.Ordinal)));

        Assert.Empty(markerFailures);

        HashSet<AcquisitionIdentity> catalog =
        [
            .. GrimoireConnectionAcquisitionScanner.Catalog()
                .Select(static entry => entry.Identity),
        ];

        HashSet<(string Name, int Arity)> routes =
        [
            .. ordinaryRoutes.Select(static identity =>
                (identity.CalleeOrConstructedType, identity.Arity)),
        ];

        AcquisitionIdentity[] routeSurface =
        [
            .. discoveries.Where(identity =>
                ordinaryRoutes.Contains(identity)
                || (identity.ConstructKind == AcquisitionConstructKind.MarkedRouteInvocation
                    && routes.Contains((identity.CalleeOrConstructedType, identity.Arity)))),
        ];

        Assert.All(routeSurface, identity => Assert.Contains(identity, catalog));

    }

    [Fact]
    public void Journal_maintenance_factory_contract_marker_and_inert_route_inventory_are_exact()
    {

        MethodInfo contract = Assert.Single(
            typeof(IGrimoireMaintenanceConnectionFactory).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));

        Assert.Equal("OpenJournalCanonicalErasureAsync", contract.Name);

        Assert.Equal(
            [
                typeof(IGrimoireMaintenanceConnectionCapability),
                typeof(IGrimoireMaintenanceIoLane),
                typeof(CancellationToken),
            ],
            contract.GetParameters().Select(static parameter => parameter.ParameterType));

        Assert.Empty(contract.GetCustomAttributes<GrimoireConnectionAcquisitionRouteAttribute>());

        PropertyInfo leaseProperty = Assert.Single(
            typeof(IGrimoireMaintenanceConnectionLease).GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));

        Assert.Equal("Connection", leaseProperty.Name);

        Assert.Equal(typeof(Microsoft.Data.Sqlite.SqliteConnection), leaseProperty.PropertyType);

        MethodInfo implementation = typeof(GrimoireMaintenanceConnectionFactory).GetMethod(
            "OpenJournalCanonicalErasureAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            ?? throw new InvalidOperationException("The journal maintenance acquisition route is missing.");

        _ = Assert.Single(
            implementation.GetCustomAttributes<GrimoireConnectionAcquisitionRouteAttribute>());

        IReadOnlyList<AcquisitionSource> sources = ProductionSources();

        IReadOnlyList<AcquisitionIdentity> discoveries =
            GrimoireConnectionAcquisitionScanner.Discover(sources);

        AcquisitionIdentity declaration = Assert.Single(
            discoveries,
            static identity =>
                identity.RelativePath.EndsWith(
                    "/GrimoireMaintenanceConnectionFactory.cs",
                    StringComparison.Ordinal)
                && identity.ConstructKind == AcquisitionConstructKind.MarkedRouteDeclaration
                && identity.CalleeOrConstructedType == "OpenJournalCanonicalErasureAsync"
                && identity.Arity == 3);

        Assert.Contains(
            GrimoireConnectionAcquisitionScanner.Catalog(),
            entry => entry.Identity == declaration
                && entry.PathAuthority == GrimoirePathAuthority.LiveGrimoire
                && entry.AcquisitionKind == GrimoireAcquisitionKind.JournalMaintenance
                && entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.MaintenanceConnectionFactory
                && entry.NonServingProof is null);

        Assert.DoesNotContain(
            discoveries,
            static identity =>
                identity.ConstructKind == AcquisitionConstructKind.MarkedRouteInvocation
                && identity.CalleeOrConstructedType == "OpenJournalCanonicalErasureAsync");

        IReadOnlyList<InventoryFailure> markerFailures =
            GrimoireConnectionAcquisitionScanner.ValidateMarkerCoverage(
                sources.Where(static source => source.RelativePath.EndsWith(
                    "/GrimoireMaintenanceConnectionFactory.cs",
                    StringComparison.Ordinal)));

        Assert.Empty(markerFailures);

    }

    [Fact]
    public void Scoped_serving_raw_members_have_no_direct_provider_open()
    {

        Assert.DoesNotContain(
            ProductionServingRawDiscoveries(),
            discovery => discovery.Identity.ConstructKind
                is AcquisitionConstructKind.ProviderOpen
                && ScopedMigrationMembers.Contains(
                    (discovery.Identity.RelativePath, discovery.Identity.EnclosingMember)));

    }

    [Fact]
    public void Fresh_serving_raw_members_use_only_the_marked_ordinary_factory_route()
    {

        IReadOnlyList<AcquisitionSource> sources = ProductionSources();

        IReadOnlyList<AcquisitionIdentity> discoveries =
            GrimoireConnectionAcquisitionScanner.Discover(sources);

        foreach ((string relativePath, string enclosingType, string enclosingMember)
            in FreshMigrationMembers)
        {

            AcquisitionIdentity route = Assert.Single(
                discoveries,
                discovery =>
                    discovery.RelativePath == relativePath
                    && discovery.EnclosingType == enclosingType
                    && discovery.EnclosingMember == enclosingMember
                    && discovery.ConstructKind == AcquisitionConstructKind.MarkedRouteInvocation);

            Assert.Equal("OpenFreshAsync", route.CalleeOrConstructedType);

            Assert.Equal(2, route.Arity);

            Assert.DoesNotContain(
                discoveries,
                discovery =>
                    discovery.RelativePath == relativePath
                    && discovery.EnclosingType == enclosingType
                    && discovery.EnclosingMember == enclosingMember
                    && discovery.ConstructKind is
                        AcquisitionConstructKind.ProviderOpen
                        or AcquisitionConstructKind.ProviderObjectCreation);

            AcquisitionSource source = Assert.Single(
                sources,
                candidate => candidate.RelativePath == relativePath);

            CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(source.Text)
                .GetCompilationUnitRoot();

            string methodName = enclosingMember[..enclosingMember.IndexOf('(')];

            int parameterCount = int.Parse(
                enclosingMember[(enclosingMember.IndexOf('(') + 1)..^1],
                System.Globalization.CultureInfo.InvariantCulture);

            MethodDeclarationSyntax[] methods =
            [
                .. root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .Where(method =>
                        method.Identifier.ValueText == methodName
                        && method.ParameterList.Parameters.Count == parameterCount),
            ];

            Assert.NotEmpty(methods);

            Assert.DoesNotContain(
                methods.SelectMany(static method => method.DescendantNodes())
                    .OfType<ImplicitObjectCreationExpressionSyntax>(),
                static creation =>
                    creation.Ancestors()
                        .OfType<VariableDeclarationSyntax>()
                        .FirstOrDefault()?.Type.ToString() == "SqliteConnection");

            Assert.DoesNotContain(
                methods.SelectMany(static method => method.DescendantNodes())
                    .OfType<ObjectCreationExpressionSyntax>(),
                static creation => creation.Type.ToString().EndsWith(
                    "SqliteConnection",
                    StringComparison.Ordinal));

        }

        foreach (string relativePath in FreshMigrationMembers
            .Select(static member => member.RelativePath)
            .Where(static path => path.Contains("/Data/Covenant/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal))
        {

            AcquisitionSource source = Assert.Single(
                sources,
                candidate => candidate.RelativePath == relativePath);

            Assert.DoesNotContain(
                "ICovenantMaintenanceConnectionFactory",
                source.Text,
                StringComparison.Ordinal);

        }

    }

    [Fact]
    public void Ambient_maintenance_factory_is_confined_to_the_task9_bridge()
    {

        Assert.Equal(
            [
                "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMaintenanceConnectionFactory.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabase.cs",
            ],
            SourcesReferencingAmbientMaintenanceFactory(ProductionSources()));

        Assert.Equal(
            Task9AmbientMaintenanceTestBridge,
            SourcesReferencingAmbientMaintenanceFactory(TestSources()));

    }

    [Fact]
    public void Production_inventory_is_bijective()
    {

        IReadOnlyList<AcquisitionIdentity> discoveries =
            GrimoireConnectionAcquisitionScanner.Discover(ProductionSources());

        IReadOnlyList<GrimoireAcquisitionCatalogEntry> catalog =
            GrimoireConnectionAcquisitionScanner.Catalog();

        Assert.Equal(ExpectedProductionAcquisitionCount, discoveries.Count);

        IReadOnlyList<InventoryFailure> failures = GrimoireConnectionAcquisitionScanner.Validate(
            discoveries,
            catalog);

        Assert.True(
            failures.Count == 0,
            string.Join(System.Environment.NewLine, failures.Select(static failure => failure.ToString())));

        Assert.Equal(discoveries.Count, catalog.Count);

    }

    private static GrimoireAcquisitionCatalogEntry Entry(
        AcquisitionIdentity identity,
        GrimoirePathAuthority pathAuthority = GrimoirePathAuthority.LiveGrimoire,
        GrimoireAcquisitionKind acquisitionKind = GrimoireAcquisitionKind.ServingRawOrdinary,
        ExactNonServingProof? proof = null,
        GrimoireRuntimeAdmissionRoute? runtimeRoute = null) =>
        new(
            identity,
            pathAuthority,
            acquisitionKind,
            runtimeRoute ?? (pathAuthority == GrimoirePathAuthority.LiveGrimoire
                ? GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory
                : GrimoireRuntimeAdmissionRoute.ExactNonServingProof),
            proof);

    private static AcquisitionSource Source(string text) => new("Fixtures/Fixture.cs", text);

    private static IReadOnlyList<GrimoireAcquisitionCatalogEntry> ProductionServingRawDiscoveries()
    {

        HashSet<AcquisitionIdentity> discoveries =
        [
            .. GrimoireConnectionAcquisitionScanner.Discover(ProductionSources()),
        ];

        return
        [
            .. GrimoireConnectionAcquisitionScanner.Catalog()
                .Where(entry =>
                    entry.AcquisitionKind == GrimoireAcquisitionKind.ServingRawOrdinary
                    && discoveries.Contains(entry.Identity)),
        ];

    }

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

    private static IReadOnlyList<AcquisitionSource> TestSources()
    {

        string repositoryRoot = NativeSqlCipherTestPaths.RepositoryRoot();

        string testRoot = Path.Combine(repositoryRoot, "tests", "RetroDownfall.Arcanum.Tests");

        return
        [
            .. Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
                .Where(static path =>
                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(path => new AcquisitionSource(
                    Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
                    File.ReadAllText(path))),
        ];

    }

    private static string[] SourcesReferencingAmbientMaintenanceFactory(
        IReadOnlyList<AcquisitionSource> sources) =>
        [
            .. sources
                .Where(static source => source.Text.Contains(
                    "ICovenantMaintenanceConnectionFactory",
                    StringComparison.Ordinal))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

}
