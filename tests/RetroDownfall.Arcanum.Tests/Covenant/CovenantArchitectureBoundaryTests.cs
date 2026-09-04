using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

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
    public void Resume_or_acquire_is_a_required_gate_operation()
    {

        MethodInfo method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(ICovenantOperationGate).GetMethod(
                nameof(ICovenantOperationGate.ResumeOrAcquireExclusiveAsync)));

        Assert.True(method.IsAbstract);

        Assert.Null(method.GetMethodBody());

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

    /// <summary>
    /// Scans every authored source file in the repository, not one project. A writer added under
    /// <c>Api</c> or <c>Cli</c> can reach this table today: <c>ICovenantConnectionSource</c> and
    /// <c>CovenantSearchSql</c> are <c>internal</c>, and Infrastructure grants both projects
    /// <c>InternalsVisibleTo</c>, so a project-scoped scan cannot see a writer added there. Core
    /// cannot reach it - the dependency direction is Cli -&gt; Api -&gt; Infrastructure -&gt; Core -
    /// but the scan covers it anyway rather than special-casing the two that can.
    /// </summary>
    [Fact]
    public void Only_the_outbox_worker_and_rebuilder_write_accelerator_state()
    {

        string[] writers = [.. ProductionSourceInventory.Sources()
            .Where(static source => source.Names("covenant_search_documents"))
            .Select(static source => source.RelativePath)
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
                "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreProtectedStateInspector.cs",

                // The second declared exception, and it is not a live writer either. A canonical
                // erasure empties the projection in the same transaction that deletes the heads it
                // projected and stamps a new dataset generation, so there is no moment at which the
                // applied FTS tuple claims a projection this file removed: the tuple is set to null by
                // the same statement (§10.20.5). It runs on its own exclusive maintenance connection
                // with the family's admission already closed, which is the one condition under which
                // clearing the projection is not a race against the workers that own it.
                "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCanonicalErasureTransaction.cs",

                "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantIndexRebuilder.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchIndex.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchOutboxWorker.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchSql.cs",
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

        AssertSingleRegistration<CovenantRuntimeGenerationProvider>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantRuntimeGenerationProvider>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<CovenantAvailability>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantAvailability>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<CovenantAuthoritySnapshotProvider>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantAuthoritySnapshotProvider>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantConnectionDrain>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<StoppedHostGrimoireConnectionFactory>(
            services,
            ServiceLifetime.Singleton);

        AssertSingleRegistration<IStoppedHostGrimoireConnectionFactory>(
            services,
            ServiceLifetime.Singleton);

        AssertSingleRegistration<CovenantV3MaintenanceConnectionFactory>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantV3MaintenanceConnectionFactory>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantV3MaintenancePathAuthority>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<CovenantHealthyCatalogErasureGuard>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<CovenantManagedFileErasureRequestReader>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<CovenantDisclosureExposureReader>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<CovenantErasureStartupRecoveryOwnerAdopter>(services, ServiceLifetime.Singleton);

        // The journal's slot is one per profile, so its store is a singleton; the authority above it
        // reads this installation's identity, which is scoped.
        AssertSingleRegistration<IGrimoireOfflineTransitionJournalStore>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<GrimoireOfflineTransitionHandlerRegistry>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<GrimoireOfflineTransitionLifecycleStore>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<IGrimoireOfflineTransitionPhaseAuthority>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<ICovenantCanonicalErasure>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantLocalErasureStorageHealth>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<CovenantDisclosureWriter>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantDisclosureJournal>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantDisclosureWriterLifecycle>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantAuthorityTransitionPublisher>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantCommittedTransitionPublisher>(services, ServiceLifetime.Singleton);

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

        AssertSingleRegistration<CovenantOwnerCleanupCoordinator>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantSearchOutboxCoordinator>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantTurnReceiptCompactionCoordinator>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantIndexRebuilder>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<ICovenantConnectionSource>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantErasureInventorySource>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<ICovenantErasureInventorySource>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantErasureTransition>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<ICovenantErasureTransition>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantErasureCoordinator>(services, ServiceLifetime.Scoped);

    }

    [Fact]
    public async Task Cli_composition_validates_the_complete_covenant_graph()
    {

        ServiceCollection services = [];

        services.AddLogging();

        services.AddArcanumCliClientStack();

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(CovenantResetCheckpointInitiator));

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(ICovenantErasureEffectDigestCalculator));

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(DataRetentionService));

        await AssertCompleteCovenantGraphAsync(services, isHost: false);

    }

    [Fact]
    public async Task Full_host_composition_validates_the_complete_covenant_graph()
    {

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<IWeaveService>(static _ => null!);

        builder.Services.AddSingleton<IArcanumIntelligenceProvider>(static _ => null!);

        builder.Services.AddSingleton<IHumanPromptRegistry>(static _ => null!);

        builder.Services.AddSingleton<IModelTokenEstimator>(static _ => null!);

        builder.Services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        AssertSingleRegistration<CovenantResetCheckpointInitiator>(
            builder.Services,
            ServiceLifetime.Scoped);

        AssertSingleRegistration<ICovenantErasureEffectDigestCalculator>(
            builder.Services,
            ServiceLifetime.Singleton);

        AssertSingleRegistration<DataRetentionService>(
            builder.Services,
            ServiceLifetime.Scoped);

        AssertSingleRegistration<IDataRetentionService>(
            builder.Services,
            ServiceLifetime.Scoped);

        AssertSingleRecoveryHandler<DataRetentionRecoveryHandler>(builder.Services);

        AssertSingleRecoveryHandler<DataRetentionMutationRecoveryHandler>(builder.Services);

        AssertSingleRecoveryHandler<DataRetentionFactoryResetRecoveryHandler>(builder.Services);

        await AssertCompleteCovenantGraphAsync(builder.Services, isHost: true);

    }

    [Fact]
    public void Covenant_runtime_facades_expose_no_independent_live_state_mutator()
    {

        Type[] facades =
        [
            typeof(ICovenantEnvelopeMasterKeyProvider),
            typeof(ICovenantAuthoritySnapshotProvider),
            typeof(ICovenantAvailability),
        ];

        foreach (Type facade in facades)
        {

            Assert.All(
                facade.GetProperties(),
                static property => Assert.Null(property.SetMethod));

            Assert.DoesNotContain(
                facade.GetMethods(),
                static method => !method.IsSpecialName
                    && (method.Name.StartsWith("Initialize", StringComparison.Ordinal)
                        || method.Name.StartsWith("Publish", StringComparison.Ordinal)
                        || method.Name.StartsWith("Replace", StringComparison.Ordinal)
                        || method.Name.StartsWith("Retire", StringComparison.Ordinal)
                        || method.Name.StartsWith("Set", StringComparison.Ordinal)));

        }

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

    private static void AssertSingleRecoveryHandler<THandler>(IServiceCollection services)
    {

        ServiceDescriptor descriptor = Assert.Single(
            services,
            static candidate => candidate.ServiceType == typeof(ILongRunningOperationRecoveryHandler)
                && candidate.ImplementationType == typeof(THandler));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

    }

    private static async Task AssertCompleteCovenantGraphAsync(
        IServiceCollection services,
        bool isHost)
    {

        SqliteNativeRuntime.Instance.Initialize();

        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        IGrimoireDbPassphraseSource passphrase = provider
            .GetRequiredService<IGrimoireDbPassphraseSource>();

        Assert.IsType<GrimoireDbPassphraseSource>(passphrase)
            .SetPassphrase("task-8-composition-validation");

        await using AsyncServiceScope firstScope = provider.CreateAsyncScope();

        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();

        CovenantErasureCoordinator firstCoordinator = firstScope.ServiceProvider
            .GetRequiredService<CovenantErasureCoordinator>();

        CovenantErasureCoordinator secondCoordinator = secondScope.ServiceProvider
            .GetRequiredService<CovenantErasureCoordinator>();

        Assert.NotSame(firstCoordinator, secondCoordinator);

        CovenantErasureInventorySource firstInventory = firstScope.ServiceProvider
            .GetRequiredService<CovenantErasureInventorySource>();

        Assert.Same(
            firstInventory,
            firstScope.ServiceProvider.GetRequiredService<ICovenantErasureInventorySource>());

        Assert.NotSame(
            firstInventory,
            secondScope.ServiceProvider.GetRequiredService<CovenantErasureInventorySource>());

        CovenantErasureTransition firstTransition = firstScope.ServiceProvider
            .GetRequiredService<CovenantErasureTransition>();

        Assert.Same(
            firstTransition,
            firstScope.ServiceProvider.GetRequiredService<ICovenantErasureTransition>());

        Assert.NotSame(
            firstTransition,
            secondScope.ServiceProvider.GetRequiredService<CovenantErasureTransition>());

        Assert.Same(
            provider.GetRequiredService<ICovenantDisclosureJournal>(),
            provider.GetRequiredService<ICovenantDisclosureWriterLifecycle>());

        CovenantRuntimeGenerationProvider runtime = provider
            .GetRequiredService<CovenantRuntimeGenerationProvider>();

        Assert.Same(
            runtime,
            provider.GetRequiredService<ICovenantRuntimeGenerationProvider>());

        Assert.Same(
            runtime,
            RuntimeHolder(provider.GetRequiredService<CovenantEnvelopeMasterKeyProvider>()));

        Assert.Same(
            runtime,
            RuntimeHolder(provider.GetRequiredService<CovenantAuthoritySnapshotProvider>()));

        Assert.Same(
            runtime,
            RuntimeHolder(provider.GetRequiredService<CovenantAvailability>()));

        Assert.Same(
            runtime,
            RuntimeHolder(provider.GetRequiredService<CovenantOperationGate>()));

        Assert.Same(
            provider.GetRequiredService<ICovenantConnectionDrain>(),
            firstScope.ServiceProvider.GetRequiredService<ICovenantConnectionDrain>());

        Assert.Same(
            provider.GetRequiredService<IStoppedHostGrimoireConnectionFactory>(),
            firstScope.ServiceProvider
                .GetRequiredService<IStoppedHostGrimoireConnectionFactory>());

        Assert.Same(
            provider.GetRequiredService<CovenantV3MaintenanceConnectionFactory>(),
            provider.GetRequiredService<ICovenantV3MaintenanceConnectionFactory>());

        Assert.Same(
            provider.GetRequiredService<ICovenantV3MaintenanceConnectionFactory>(),
            firstScope.ServiceProvider.GetRequiredService<ICovenantV3MaintenanceConnectionFactory>());

        Assert.Same(
            provider.GetRequiredService<ICovenantCanonicalErasure>(),
            firstScope.ServiceProvider.GetRequiredService<ICovenantCanonicalErasure>());

        Assert.Same(
            provider.GetRequiredService<ICovenantLocalErasureStorageHealth>(),
            firstScope.ServiceProvider.GetRequiredService<ICovenantLocalErasureStorageHealth>());

        Assert.Same(
            provider.GetRequiredService<CovenantErasureStartupRecoveryOwnerAdopter>(),
            firstScope.ServiceProvider.GetRequiredService<CovenantErasureStartupRecoveryOwnerAdopter>());

        Assert.Same(
            provider.GetRequiredService<CovenantManagedFileErasureRequestReader>(),
            firstScope.ServiceProvider.GetRequiredService<CovenantManagedFileErasureRequestReader>());

        Assert.Same(
            provider.GetRequiredService<CovenantDisclosureExposureReader>(),
            firstScope.ServiceProvider.GetRequiredService<CovenantDisclosureExposureReader>());

        Assert.Same(
            provider.GetRequiredService<ICovenantAuthorityTransitionPublisher>(),
            provider.GetRequiredService<ICovenantCommittedTransitionPublisher>());

        if (isHost)
        {

            _ = firstScope.ServiceProvider.GetRequiredService<CovenantResetCheckpointInitiator>();

            _ = provider.GetRequiredService<ICovenantErasureEffectDigestCalculator>();

        }
        else
        {

            Assert.Null(firstScope.ServiceProvider.GetService<CovenantResetCheckpointInitiator>());

            Assert.Null(provider.GetService<ICovenantErasureEffectDigestCalculator>());

        }

    }

    private static CovenantRuntimeGenerationProvider RuntimeHolder(object facade)
    {

        FieldInfo field = Assert.Single(
            facade.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            static candidate => candidate.FieldType == typeof(CovenantRuntimeGenerationProvider));

        return Assert.IsType<CovenantRuntimeGenerationProvider>(field.GetValue(facade));

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
