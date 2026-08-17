using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The layering and single-owner invariants of the Covenant domain and persistence boundary.
/// </summary>
/// <remarks>
/// These assertions are cheap to keep and expensive to lose. A second operation gate, a second
/// search port, or a Core type that learned about SQLite would each be a change that compiles, ships,
/// and quietly removes a guarantee the rest of the tier assumes.
/// </remarks>
public sealed class CovenantArchitectureBoundaryTests
{

    private static readonly Assembly CoreAssembly = typeof(CovenantOperationScope).Assembly;

    private static readonly Assembly InfrastructureAssembly = typeof(CovenantOperationGate).Assembly;

    [Fact]
    public void Core_covenant_types_reference_no_storage_provider_or_transport()
    {

        string[] forbidden =
        [
            "Microsoft.Data.Sqlite",

            "Microsoft.EntityFrameworkCore",

            "Microsoft.AspNetCore",

            "System.Net.Http",

            "Microsoft.Extensions.AI",
        ];

        AssemblyName[] referenced = CoreAssembly.GetReferencedAssemblies();

        foreach (string name in forbidden)
        {

            Assert.DoesNotContain(referenced, reference => reference.Name == name);

        }

    }

    [Fact]
    public void No_covenant_ef_entity_or_migration_exists()
    {

        Assert.DoesNotContain(
            InfrastructureAssembly.GetTypes(),
            static type => type.Namespace?.Contains(".Migrations", StringComparison.Ordinal) == true
                && type.Name.Contains("Covenant", StringComparison.OrdinalIgnoreCase));

        // The canonical tier is declarative SQL, so no DbSet may name it.
        Assert.DoesNotContain(
            typeof(RetroDownfall.Arcanum.Infrastructure.Data.ArcanumDbContext)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => property.Name.Contains("Covenant", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public void One_operation_gate_owns_installation_read_coverage()
    {

        Type[] gates = [.. InfrastructureAssembly.GetTypes()
            .Where(static type => type.IsClass && !type.IsAbstract)
            .Where(static type => typeof(ICovenantOperationGate).IsAssignableFrom(type))];

        Assert.Same(typeof(CovenantOperationGate), Assert.Single(gates));

        Assert.NotNull(typeof(ICovenantOperationGate).GetMethod(nameof(ICovenantOperationGate.AcquireInstallationReadAsync)));

    }

    [Fact]
    public void The_installation_read_lease_is_the_sole_all_scopes_capability()
    {

        Type[] leases = [.. CoreAssembly.GetTypes()
            .Where(static type => type.IsClass && !type.IsAbstract)
            .Where(static type => typeof(ICovenantSnapshotReadLease).IsAssignableFrom(type))];

        // Every snapshot-read lease is either the installation lease or a scoped one. There is no
        // second type that could claim all scopes.
        Assert.Contains(typeof(CovenantInstallationReadLease), leases);

        Assert.All(
            leases,
            lease => Assert.Contains(
                lease,
                (Type[])
                [
                    typeof(CovenantInstallationReadLease),
                    typeof(CovenantReadLease),
                    typeof(CovenantTurnLease),
                    typeof(CovenantProtectedTransferLease),
                ]));

    }

    [Fact]
    public void One_search_index_owns_the_snapshot_read_search_signature()
    {

        Type[] indexes = [.. InfrastructureAssembly.GetTypes()
            .Where(static type => type.IsClass && !type.IsAbstract)
            .Where(static type => typeof(ICovenantSearchIndex).IsAssignableFrom(type))];

        Assert.Same(typeof(CovenantSearchIndex), Assert.Single(indexes));

    }

    [Fact]
    public void One_store_owns_the_canonical_read_port()
    {

        Type[] stores = [.. InfrastructureAssembly.GetTypes()
            .Where(static type => type.IsClass && !type.IsAbstract)
            .Where(static type => typeof(ICovenantStore).IsAssignableFrom(type))];

        Assert.Same(typeof(CovenantStore), Assert.Single(stores));

    }

    [Fact]
    public void Only_the_outbox_worker_and_rebuilder_write_accelerator_state()
    {

        string[] writers = [.. Directory
            .EnumerateFiles(
                Path.Combine(RepositoryRoot(), "src", "RetroDownfall.Arcanum.Infrastructure"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => File.ReadAllText(path)
                .Contains("covenant_search_documents", StringComparison.Ordinal))
            .Select(static path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)];

        Assert.Equal(
            [
                // The one declared exception, and it is not a live writer. This file owns the list of
                // Covenant family content tables for two staged-only callers: the pre-staging inventory,
                // which counts them, and the protected-state purge of §10.19.10, which clears them out
                // of a candidate that has never been published as live. The boundary exists to stop
                // anything but the projection's owners from mutating it while the applied FTS tuple
                // claims it is current — and a purge runs against a database whose applied tuple is
                // null, before it becomes anybody's live installation.
                "BackupRestoreProtectedStateInspector.cs",
                "CovenantIndexRebuilder.cs",
                "CovenantSearchIndex.cs",
                "CovenantSearchOutboxWorker.cs",
                "CovenantSearchSql.cs",
            ],
            writers);

    }

    [Fact]
    public void Every_persistence_component_is_registered_exactly_once()
    {

        ServiceCollection services = [];

        services.AddArcanumInfrastructure(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        AssertSingleRegistration<ICovenantOperationGate>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<CovenantOperationGate>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantCampaignScopeProbe>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantCompiler>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantLinker>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantStore>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<ICovenantSearchIndex>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantMutationKernel>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantQuotaGuard>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantTurnReceiptCompactor>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantCleanupWorker>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantOwnerDeletionReader>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantSearchOutboxWorker>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantIndexRebuilder>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<ICovenantConnectionSource>(services, ServiceLifetime.Scoped);

    }

    [Fact]
    public void Every_covenant_connection_goes_through_the_central_initializer()
    {

        string[] offenders = [.. Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => File.ReadAllText(path).Contains("new SqliteConnection(", StringComparison.Ordinal))
            .Where(static path => !File.ReadAllText(path)
                .Contains("CovenantSqliteConnectionInitializer", StringComparison.Ordinal))
            .Where(static path => File.ReadAllText(path).Contains("covenant_", StringComparison.Ordinal))
            .Select(static path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)];

        // A Covenant suite that opened a raw connection would find its trigger guards missing rather
        // than denying, and would pass for the wrong reason.
        Assert.Empty(offenders);

    }

    private static void AssertSingleRegistration<TService>(
        IServiceCollection services,
        ServiceLifetime expected)
    {

        ServiceDescriptor descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(TService));

        Assert.Equal(expected, descriptor.Lifetime);

    }

    private static string RepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {

            directory = directory.Parent;

        }

        Assert.NotNull(directory);

        return directory!.FullName;

    }

}
