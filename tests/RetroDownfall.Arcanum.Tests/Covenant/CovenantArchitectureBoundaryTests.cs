using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Operations;
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
    public void Owner_evidence_has_exactly_two_path_specific_issuers()
    {
        CSharpCompilation compilation = Assert.Single(
            HostedGrimoireProducerInventory.ProductionCompilations,
            static candidate => candidate.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        INamedTypeSymbol ownerEvidence = RequiredSourceType(
            compilation,
            "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningRecoveryOwnerEvidence");

        INamedTypeSymbol[] allTypes = SourceTypes(compilation).ToArray();

        INamedTypeSymbol[] issuers = allTypes
            .Where(type => SymbolEqualityComparer.Default.Equals(type.BaseType, ownerEvidence))
            .OrderBy(MetadataName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantOfflineTransitionLaunchGapResumption+AdoptedLaunchOwnerEvidence",
                "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery+AuthenticatedJournalOwnerEvidence"
            ],
            issuers.Select(MetadataName));

        Assert.All(issuers, static issuer =>
        {
            Assert.NotNull(issuer.ContainingType);
            Assert.Equal(Accessibility.Private, issuer.DeclaredAccessibility);
            Assert.True(issuer.IsSealed);
        });

        Dictionary<string, string> expectedIssuerMembers = new(StringComparer.Ordinal)
        {
            ["RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantOfflineTransitionLaunchGapResumption+AdoptedLaunchOwnerEvidence"] = "RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantOfflineTransitionLaunchGapResumption.ResumeBeforeReadinessAsync",
            ["RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery+AuthenticatedJournalOwnerEvidence"] = "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery.PrepareAsync"
        };

        IMethodSymbol[] issuerCreationMembers = issuers
            .SelectMany(issuer => ObjectCreationMembers(compilation, issuer))
            .ToArray();

        Assert.Equal(expectedIssuerMembers.Count, issuerCreationMembers.Length);

        foreach (INamedTypeSymbol issuer in issuers)
        {
            Assert.Equal(
                expectedIssuerMembers[MetadataName(issuer)],
                MethodName(Assert.Single(ObjectCreationMembers(compilation, issuer))));
        }

        INamedTypeSymbol adoptedOwner = RequiredSourceType(
            compilation,
            "RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantErasureStartupRecoveryOwnerAdopter+AdoptedOwner");

        Assert.Equal(
            "RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantErasureStartupRecoveryOwnerAdopter",
            MetadataName(adoptedOwner.ContainingType!));
        Assert.True(adoptedOwner.IsSealed);
        Assert.False(adoptedOwner.IsAbstract);

        Assert.Equal(
            Accessibility.Private,
            Assert.Single(adoptedOwner.InstanceConstructors).DeclaredAccessibility);

        IMethodSymbol adoptedOwnerCreationMember = Assert.Single(
            ObjectCreationMembers(compilation, adoptedOwner));

        Assert.Equal(
            "RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantErasureStartupRecoveryOwnerAdopter+AdoptedOwner.CompleteAuthenticatedAdoptionAsync",
            MethodName(adoptedOwnerCreationMember));

        IMethodSymbol authenticatedCreationMember = Assert.Single(
            adoptedOwner.GetMembers("CompleteAuthenticatedAdoptionAsync").OfType<IMethodSymbol>());

        Assert.Equal(
            ["RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantErasureStartupRecoveryOwnerAdopter.AdoptBeforeReadinessAsync"],
            InvocationCallers(compilation, authenticatedCreationMember).Select(MethodName));

        INamedTypeSymbol handoff = RequiredSourceType(
            compilation,
            "RetroDownfall.Arcanum.Infrastructure.Security.ICovenantClosedRecoveryHandoff");

        INamedTypeSymbol handoffImplementation = Assert.Single(
            allTypes,
            type => type is { TypeKind: TypeKind.Class, IsAbstract: false }
                && type.AllInterfaces.Any(
                    contract => SymbolEqualityComparer.Default.Equals(contract, handoff)));

        Assert.Equal(
            "RetroDownfall.Arcanum.Infrastructure.Security.CovenantClosedRecoveryHandoff",
            MetadataName(handoffImplementation));

        INamedTypeSymbol lockAccessor = RequiredSourceType(
            compilation,
            "RetroDownfall.Arcanum.Infrastructure.InstallationReset.IInstallationResetMaintenanceLockAccessor");

        IMethodSymbol borrowHeldLock = Assert.Single(
            lockAccessor.GetMembers("BorrowHeldLock").OfType<IMethodSymbol>());

        IMethodSymbol[] prohibitedCreationMembers =
        [
            .. issuerCreationMembers,
            adoptedOwnerCreationMember
        ];

        string[] reachableCreationMembers = ReachableMembers(
                compilation,
                InvocationCallers(compilation, borrowHeldLock))
            .Where(member => prohibitedCreationMembers.Any(
                creator => SymbolEqualityComparer.Default.Equals(
                    creator.OriginalDefinition,
                    member.OriginalDefinition)))
            .Select(MethodName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(reachableCreationMembers);
    }

    [Fact]
    public void Every_effectful_recovery_handler_has_an_exact_admission_classification()
    {
        // Required callees are concrete external effects. Typed recovery handoffs are graph edges,
        // not effects; HostedGrimoireProducerInventoryTests proves those closed graphs separately.
        RecoveryEffectContract[] contracts =
        [
            new(
                "RetroDownfall.Arcanum.Api.Intelligence.BatchOperationRecoveryHandler",
                LongRunningOperationKinds.Batch,
                [0],
                [
                    "System.IO.File.Delete"
                ]),
            new(
                "RetroDownfall.Arcanum.Infrastructure.Operations.AttachmentPromotionRecoveryHandler",
                LongRunningOperationKinds.AttachmentPromotion,
                [0],
                ["RetroDownfall.Arcanum.Core.Storage.ISessionAttachmentStore.ReconcileAsync"]),
            new(
                "RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionMigrationRecoveryHandler",
                LongRunningOperationKinds.BlobEncryptionMigration,
                [0, 1],
                ["RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionFileProcessor.MigrateAsync"]),
            new(
                "RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionKeyRotationRecoveryHandler",
                LongRunningOperationKinds.BlobEncryptionKeyRotation,
                [0, 1],
                [
                    "RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionFileProcessor.ReencryptAsync",
                    "RetroDownfall.Arcanum.Core.Storage.IFileEncryptionKeyRing.RetireAsync"
                ]),
            new(
                "RetroDownfall.Arcanum.Infrastructure.Backup.BackupCreateRecoveryHandler",
                LongRunningOperationKinds.BackupCreate,
                [2],
                ["RetroDownfall.Arcanum.Infrastructure.Backup.OwnedTemporaryDirectory.TryDelete"]),
            new(
                "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionRecoveryHandler",
                LongRunningOperationKinds.DataRetentionPrune,
                [0, 2],
                [
                    "RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryDeleteQuarantined"
                ]),
            new(
                "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionMutationRecoveryHandler",
                LongRunningOperationKinds.DataRetentionMutation,
                [2],
                [
                    "RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryDeleteQuarantined"
                ],
                [4]),
            new(
                "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionFactoryResetRecoveryHandler",
                LongRunningOperationKinds.DataRetentionFactoryReset,
                [0],
                [
                    "RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryQuarantine"
                ],
                [2]),
            new(
                "RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapMutationRecoveryHandler",
                LongRunningOperationKinds.DataRetentionMutation,
                [],
                [
                    "RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryDeleteQuarantined"
                ],
                [4]),
            new(
                "RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapFactoryResetRecoveryHandler",
                LongRunningOperationKinds.DataRetentionFactoryReset,
                [],
                [
                    "RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryQuarantine"
                ],
                [2]),
            new(
                "RetroDownfall.Arcanum.Infrastructure.A2A.A2AOutboundSendingRecoveryHandler",
                LongRunningOperationKinds.A2AOutboundSending,
                [1],
                ["RetroDownfall.Arcanum.Infrastructure.A2A.IA2AClientService.CancelRemoteTaskAsync"])
        ];

        IReadOnlyList<CSharpCompilation> compilations =
            HostedGrimoireProducerInventory.ProductionCompilations;

        SourceType[] handlers = compilations
            .SelectMany(compilation => SourceTypes(compilation)
                .Select(symbol => new SourceType(compilation, symbol)))
            .Where(static candidate => candidate.Symbol is { TypeKind: TypeKind.Class, IsAbstract: false })
            .Where(static candidate => candidate.Symbol.AllInterfaces.Any(
                contract => MetadataName(contract)
                    == "RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler"))
            .OrderBy(static candidate => MetadataName(candidate.Symbol), StringComparer.Ordinal)
            .ToArray();

        NonHostedProducerChainEntry[] roots = handlers
            .Select(RecoveryHandlerRoot)
            .ToArray();

        HostedProducerDiscovery<HostedProducerSite> discovery =
            HostedGrimoireProducerInventory.DiscoverProducerSites(
                compilations,
                new HostedProducerDiscovery<string>([], []),
                [],
                roots);

        string[] expectedEffectfulHandlers = contracts
            .Select(static contract => contract.HandlerType)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] discoveredEffectfulHandlers = discovery.Items
            .Where(static site => site.Kind is HostedProducerSiteKind.ProviderCall
                or HostedProducerSiteKind.FileSystemEffect)
            .Select(static site => site.RootType)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedEffectfulHandlers, discoveredEffectfulHandlers);

        Dictionary<string, SourceType> handlersByName = handlers.ToDictionary(
            static handler => MetadataName(handler.Symbol),
            StringComparer.Ordinal);

        List<string> missingEffectSites = [];

        foreach (RecoveryEffectContract contract in contracts)
        {
            SourceType handler = handlersByName[contract.HandlerType];

            Assert.Equal(contract.Kind, ConstantStringProperty(handler, "Kind"));

            HostedProducerSite[] sites = discovery.Items
                .Where(site => site.RootType == contract.HandlerType)
                .Where(static site => site.Kind is HostedProducerSiteKind.ProviderCall
                    or HostedProducerSiteKind.FileSystemEffect)
                .ToArray();

            foreach (string callee in contract.RequiredCallees)
            {
                if (!sites.Any(site => site.Callee == callee))
                {
                    missingEffectSites.Add(contract.HandlerType + " -> " + callee);
                }
            }

            foreach (int checkpointVersion in contract.ExternalCheckpointVersions)
            {
                LongRunningRecoveryAdmissionDecision decision =
                    LongRunningOperationRecoveryAdmission.Classify(
                        RecoveryOperation(contract.Kind, checkpointVersion),
                        ownerEvidence: null);

                Assert.True(
                    decision.Kind is LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect,
                    $"{contract.HandlerType} checkpoint V{checkpointVersion} reaches an external effect but was classified {decision.Kind}.");
            }

            foreach (int checkpointVersion in contract.OwnerBoundCheckpointVersions ?? [])
            {
                LongRunningRecoveryAdmissionDecision decision =
                    LongRunningOperationRecoveryAdmission.Classify(
                        RecoveryOperation(contract.Kind, checkpointVersion),
                        ownerEvidence: null);

                Assert.Equal(
                    LongRunningRecoveryAdmissionKind.OwnerBoundAwaitingExactOwner,
                    decision.Kind);
            }
        }

        Assert.True(
            missingEffectSites.Count == 0,
            "Required recovery effect sites were not discovered:\n" + string.Join("\n", missingEffectSites));

        Assert.Equal(
            LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            LongRunningOperationRecoveryAdmission.Classify(
                RecoveryOperation(LongRunningOperationKinds.BackupCreate, 0),
                ownerEvidence: null).Kind);

        Assert.Equal(
            LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            LongRunningOperationRecoveryAdmission.Classify(
                RecoveryOperation(LongRunningOperationKinds.A2AOutboundSending, 0),
                ownerEvidence: null).Kind);
    }

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

        AssertSingleRegistration<IGrimoireMaintenanceConnectionFactory>(services, ServiceLifetime.Singleton);

        // Where every closed-period path comes from. A singleton because the installation's canonical
        // and staging files do not change while the process runs, and because two of these would be
        // two answers to "which file is the Grimoire" — the question the gate exists to settle once.
        AssertSingleRegistration<IGrimoireMaintenancePathAuthority>(services, ServiceLifetime.Singleton);

        // Scoped, unlike everything around it, and deliberately so. This names the exact connection
        // object the operation store issues its statements on, and that object belongs to the scoped
        // database context — a singleton here would hand the coordinator a connection some other
        // scope owns, which is the one thing the scoped permit it feeds cannot tolerate.
        AssertSingleRegistration<ICovenantClosedPeriodLedgerConnection>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<CovenantHealthyCatalogErasureGuard>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<CovenantManagedFileErasureRequestReader>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<CovenantDisclosureExposureReader>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<CovenantErasureStartupRecoveryOwnerAdopter>(services, ServiceLifetime.Singleton);

        // The journal's slot is one per profile, so its store is a singleton; the authority above it
        // reads this installation's identity, which is scoped.
        AssertSingleRegistration<IGrimoireOfflineTransitionJournalStore>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<GrimoireOfflineTransitionHandlerRegistry>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<GrimoireOfflineTransitionLifecycleStore>(services, ServiceLifetime.Singleton);

        // Scoped, unlike the payload table above it. Decoding a journal depends on nothing but the
        // bytes; deciding what a transition of that kind owes is reached through the same scope its
        // session and operation store are, and a singleton would let a handler capture one it outlives.
        AssertSingleRegistration<GrimoireOfflineTransitionEffectHandlerRegistry>(
            services,
            ServiceLifetime.Scoped);

        AssertSingleRegistration<IGrimoireOfflineTransitionPhaseAuthority>(services, ServiceLifetime.Scoped);

        // The pre-bootstrap recovery chain is composed as singletons because it runs before any
        // request scope exists. Its one scoped collaborator - the operation store, reached through the
        // lease adoption below - is resolved from a scope the dispatch opens per resumed operation.
        AssertSingleRegistration<IGrimoireRecoveryOnlyUnlock>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ICovenantRecoveryAuthorityBootstrapper>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<IGrimoireOfflineTransitionHandlerDispatch>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<IGrimoireOfflineTransitionStartupRecovery>(services, ServiceLifetime.Singleton);

        AssertSingleRegistration<ILongRunningOperationMaintenanceLeaseAdoption>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<ILongRunningOperationSameOwnerLeaseResumption>(services, ServiceLifetime.Scoped);

        Assert.False(typeof(ILongRunningOperationSameOwnerLeaseResumption).IsVisible);

        Assert.False(
            typeof(ILongRunningOperationSameOwnerLeaseResumption)
                .IsAssignableFrom(typeof(FakeLongRunningOperationStore)));

        AssertSingleRegistration<ILongRunningOperationGenericRecoveryDiscovery>(services, ServiceLifetime.Scoped);

        AssertSingleRegistration<ILongRunningOperationClassifiedRecoveryLeaseAcquisition>(
            services,
            ServiceLifetime.Scoped);

        AssertSingleRegistration<GrimoireOfflineTransitionDatabaseReconciler>(services, ServiceLifetime.Scoped);

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
    public async Task Cli_composition_resolves_the_exact_launch_gap_recovery_graph_without_host_services()
    {
        ServiceCollection services = [];

        services.AddLogging();

        services.AddArcanumCliClientStack();

        AssertSingleRegistration<IGrimoireOfflineTransitionHandlerDispatch>(
            services,
            ServiceLifetime.Singleton);

        AssertSingleRegistration<ILongRunningOperationMaintenanceLeaseAdoption>(
            services,
            ServiceLifetime.Scoped);

        AssertSingleRegistration<LongRunningOperationReconciler>(
            services,
            ServiceLifetime.Scoped);

        AssertSingleRecoveryHandler<CovenantLaunchGapMutationRecoveryHandler>(services);

        AssertSingleRecoveryHandler<CovenantLaunchGapFactoryResetRecoveryHandler>(services);

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType
                == typeof(ILongRunningOperationGenericRecoveryDiscovery));

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType
                == typeof(ILongRunningOperationClassifiedRecoveryLeaseAcquisition));

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(DataRetentionService));

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(IDataRetentionService));

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(IDataRetentionHostedSweep));

        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        Assert.NotNull(
            provider.GetRequiredService<IGrimoireOfflineTransitionHandlerDispatch>());

        Assert.IsType<GrimoireDbPassphraseSource>(
                provider.GetRequiredService<IGrimoireDbPassphraseSource>())
            .SetPassphrase("launch-gap-composition-validation");

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        LongRunningOperationReconciler reconciler = scope.ServiceProvider
            .GetRequiredService<LongRunningOperationReconciler>();

        InvalidOperationException genericRecovery = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reconciler.ReconcileNowAsync(
                ownerId: "cli-must-not-run-a-generic-recovery-pass",
                cancellationToken: CancellationToken.None));

        Assert.Contains("generic-discovery", genericRecovery.Message, StringComparison.Ordinal);

        Assert.Single(
            scope.ServiceProvider.GetServices<ILongRunningOperationRecoveryHandler>(),
            static handler => string.Equals(
                handler.Kind,
                LongRunningOperationKinds.DataRetentionMutation,
                StringComparison.Ordinal));

        Assert.Single(
            scope.ServiceProvider.GetServices<ILongRunningOperationRecoveryHandler>(),
            static handler => string.Equals(
                handler.Kind,
                LongRunningOperationKinds.DataRetentionFactoryReset,
                StringComparison.Ordinal));
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

        AssertSingleRegistration<IDataRetentionHostedSweep>(
            builder.Services,
            ServiceLifetime.Scoped);

        Assert.False(typeof(IDataRetentionHostedSweep).IsVisible);

        Assert.False(typeof(DataRetentionHostedSweepContinuation).IsVisible);

        Assert.False(typeof(DataRetentionHostedSweepOutcome).IsVisible);

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

        if (isHost)
        {
            LongRunningOperationStore operationStore = firstScope.ServiceProvider
                .GetRequiredService<LongRunningOperationStore>();

            Assert.Same(
                operationStore,
                firstScope.ServiceProvider.GetRequiredService<ILongRunningOperationGenericRecoveryDiscovery>());

            Assert.Same(
                operationStore,
                firstScope.ServiceProvider
                    .GetRequiredService<ILongRunningOperationClassifiedRecoveryLeaseAcquisition>());
        }

        Assert.Same(
            provider.GetRequiredService<IStoppedHostGrimoireConnectionFactory>(),
            firstScope.ServiceProvider
                .GetRequiredService<IStoppedHostGrimoireConnectionFactory>());

        Assert.Same(
            provider.GetRequiredService<IGrimoireMaintenanceConnectionFactory>(),
            firstScope.ServiceProvider.GetRequiredService<IGrimoireMaintenanceConnectionFactory>());

        Assert.Same(
            provider.GetRequiredService<IGrimoireMaintenancePathAuthority>(),
            firstScope.ServiceProvider.GetRequiredService<IGrimoireMaintenancePathAuthority>());

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
            LongRunningOperationStore firstOperationStore = firstScope.ServiceProvider
                .GetRequiredService<LongRunningOperationStore>();

            Assert.Same(
                firstOperationStore,
                firstScope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>());

            Assert.Same(
                firstOperationStore,
                firstScope.ServiceProvider.GetRequiredService<ILongRunningOperationSameOwnerLeaseResumption>());

            Assert.NotSame(
                firstOperationStore,
                secondScope.ServiceProvider.GetRequiredService<LongRunningOperationStore>());

            _ = firstScope.ServiceProvider.GetRequiredService<CovenantResetCheckpointInitiator>();

            _ = provider.GetRequiredService<ICovenantErasureEffectDigestCalculator>();
        }
        else
        {
            Assert.Null(firstScope.ServiceProvider.GetService<CovenantResetCheckpointInitiator>());

            Assert.Null(provider.GetService<ICovenantErasureEffectDigestCalculator>());
        }
    }

    private static INamedTypeSymbol RequiredSourceType(
        CSharpCompilation compilation,
        string metadataName)
    {
        INamedTypeSymbol? symbol = compilation.GetTypeByMetadataName(metadataName);

        Assert.NotNull(symbol);

        return symbol!;
    }

    private static IEnumerable<INamedTypeSymbol> SourceTypes(CSharpCompilation compilation) =>
        TypesInNamespace(compilation.Assembly.GlobalNamespace);

    private static IEnumerable<INamedTypeSymbol> TypesInNamespace(INamespaceSymbol @namespace)
    {
        foreach (INamedTypeSymbol type in @namespace.GetTypeMembers())
        {
            foreach (INamedTypeSymbol candidate in TypeAndNestedTypes(type))
            {
                yield return candidate;
            }
        }

        foreach (INamespaceSymbol child in @namespace.GetNamespaceMembers())
        {
            foreach (INamedTypeSymbol candidate in TypesInNamespace(child))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> TypeAndNestedTypes(INamedTypeSymbol type)
    {
        yield return type;

        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        {
            foreach (INamedTypeSymbol candidate in TypeAndNestedTypes(nested))
            {
                yield return candidate;
            }
        }
    }

    private static string MetadataName(INamedTypeSymbol type)
    {
        if (type.ContainingType is { } containingType)
        {
            return MetadataName(containingType) + "+" + type.MetadataName;
        }

        string containingNamespace = type.ContainingNamespace.ToDisplayString();

        return string.IsNullOrEmpty(containingNamespace)
            ? type.MetadataName
            : containingNamespace + "." + type.MetadataName;
    }

    private static string MethodName(IMethodSymbol method) =>
        MetadataName(method.ContainingType) + "." + method.Name;

    private static IReadOnlyList<IMethodSymbol> ObjectCreationMembers(
        CSharpCompilation compilation,
        INamedTypeSymbol createdType)
    {
        List<IMethodSymbol> members = [];

        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);

            foreach (BaseObjectCreationExpressionSyntax creation in tree
                .GetRoot()
                .DescendantNodes()
                .OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(creation).Symbol is not IMethodSymbol constructor
                    || !SymbolEqualityComparer.Default.Equals(
                        constructor.ContainingType,
                        createdType)
                    || model.GetEnclosingSymbol(creation.SpanStart) is not IMethodSymbol member)
                {
                    continue;
                }

                members.Add(member);
            }
        }

        return members;
    }

    private static IReadOnlyList<IMethodSymbol> InvocationCallers(
        CSharpCompilation compilation,
        IMethodSymbol calledMethod)
    {
        List<IMethodSymbol> callers = [];

        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);

            foreach (InvocationExpressionSyntax invocation in tree
                .GetRoot()
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol target
                    || !CanDispatchTo(target, calledMethod)
                    || model.GetEnclosingSymbol(invocation.SpanStart) is not IMethodSymbol caller)
                {
                    continue;
                }

                callers.Add(caller);
            }
        }

        return callers;
    }

    private static IReadOnlyList<IMethodSymbol> ReachableMembers(
        CSharpCompilation compilation,
        IEnumerable<IMethodSymbol> roots)
    {
        SourceMethod[] sourceMethods = SourceMethods(compilation).ToArray();

        Dictionary<string, SourceMethod[]> sourceMethodsByName = sourceMethods
            .GroupBy(static member => member.Symbol.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);

        Queue<IMethodSymbol> pending = new(roots);

        HashSet<IMethodSymbol> visited = new(SymbolEqualityComparer.Default);

        while (pending.TryDequeue(out IMethodSymbol? method))
        {
            IMethodSymbol definition = method.OriginalDefinition;

            if (!visited.Add(definition))
            {
                continue;
            }

            foreach (SourceMethod source in sourceMethods.Where(
                candidate => SymbolEqualityComparer.Default.Equals(
                    candidate.Symbol.OriginalDefinition,
                    definition)))
            {
                void EnqueueTargets(IMethodSymbol target)
                {
                    if (!sourceMethodsByName.TryGetValue(
                        target.Name,
                        out SourceMethod[]? candidates))
                    {
                        return;
                    }

                    foreach (SourceMethod candidate in candidates)
                    {
                        if (CanDispatchTo(candidate.Symbol, target))
                        {
                            pending.Enqueue(candidate.Symbol);
                        }
                    }
                }

                foreach (SyntaxNode call in source.Syntax
                    .DescendantNodes()
                    .Where(static node => node is InvocationExpressionSyntax
                        or BaseObjectCreationExpressionSyntax))
                {
                    if (source.Model.GetSymbolInfo(call).Symbol is not IMethodSymbol target)
                    {
                        continue;
                    }

                    EnqueueTargets(target);
                }

                foreach (ExpressionSyntax propertyReference in source.Syntax
                    .DescendantNodes()
                    .OfType<ExpressionSyntax>())
                {
                    if (source.Model.GetSymbolInfo(propertyReference).Symbol
                        is not IPropertySymbol property)
                    {
                        continue;
                    }

                    if (property.GetMethod is { } getter)
                    {
                        EnqueueTargets(getter);
                    }

                    if (property.SetMethod is { } setter)
                    {
                        EnqueueTargets(setter);
                    }
                }
            }
        }

        return visited.ToArray();
    }

    private static IEnumerable<SourceMethod> SourceMethods(CSharpCompilation compilation)
    {
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);

            foreach (SyntaxNode syntax in tree.GetRoot().DescendantNodes())
            {
                IMethodSymbol? symbol = syntax switch
                {
                    BaseMethodDeclarationSyntax declaration =>
                        model.GetDeclaredSymbol(declaration),
                    AccessorDeclarationSyntax accessor =>
                        model.GetDeclaredSymbol(accessor),
                    LocalFunctionStatementSyntax localFunction =>
                        model.GetDeclaredSymbol(localFunction),
                    _ => null
                };

                if (symbol is not null)
                {
                    yield return new SourceMethod(symbol, syntax, model);
                }
            }
        }
    }

    private static bool CanDispatchTo(
        IMethodSymbol candidate,
        IMethodSymbol target)
    {
        if (SymbolEqualityComparer.Default.Equals(
            candidate.OriginalDefinition,
            target.OriginalDefinition))
        {
            return true;
        }

        if (target.ContainingType.TypeKind == TypeKind.Interface)
        {
            foreach (INamedTypeSymbol contract in candidate.ContainingType.AllInterfaces)
            {
                foreach (IMethodSymbol slot in contract
                    .GetMembers(target.Name)
                    .OfType<IMethodSymbol>())
                {
                    if (!SymbolEqualityComparer.Default.Equals(
                            slot.OriginalDefinition,
                            target.OriginalDefinition)
                        || candidate.ContainingType.FindImplementationForInterfaceMember(slot)
                            is not IMethodSymbol implementation)
                    {
                        continue;
                    }

                    if (SymbolEqualityComparer.Default.Equals(
                        implementation.OriginalDefinition,
                        candidate.OriginalDefinition))
                    {
                        return true;
                    }
                }
            }
        }

        for (IMethodSymbol? overridden = candidate.OverriddenMethod;
            overridden is not null;
            overridden = overridden.OverriddenMethod)
        {
            if (SymbolEqualityComparer.Default.Equals(
                overridden.OriginalDefinition,
                target.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private static NonHostedProducerChainEntry RecoveryHandlerRoot(SourceType handler)
    {
        IMethodSymbol recovery = Assert.Single(
            handler.Symbol.GetMembers("RecoverAsync").OfType<IMethodSymbol>());

        SyntaxReference declaration = Assert.Single(recovery.DeclaringSyntaxReferences);

        string typeName = MetadataName(handler.Symbol);

        return new NonHostedProducerChainEntry(
            typeName + ".RecoverAsync",
            declaration.SyntaxTree.FilePath,
            typeName,
            "RecoverAsync",
            HostedProducerAuthorityKind.FiniteRequest,
            typeName + ".RecoverAsync: recovery effect classification root",
            []);
    }

    private static string ConstantStringProperty(
        SourceType sourceType,
        string propertyName)
    {
        IPropertySymbol property = Assert.Single(
            sourceType.Symbol.GetMembers(propertyName).OfType<IPropertySymbol>());

        PropertyDeclarationSyntax declaration = Assert.IsType<PropertyDeclarationSyntax>(
            Assert.Single(property.DeclaringSyntaxReferences).GetSyntax());

        ExpressionSyntax? expression = declaration.ExpressionBody?.Expression;

        if (expression is null)
        {
            AccessorDeclarationSyntax getter = Assert.Single(
                declaration.AccessorList!.Accessors,
                static accessor => accessor.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration));

            expression = getter.ExpressionBody?.Expression
                ?? Assert.Single(getter.Body!.Statements.OfType<ReturnStatementSyntax>()).Expression;
        }

        Assert.NotNull(expression);

        Optional<object?> constant = sourceType.Compilation
            .GetSemanticModel(declaration.SyntaxTree)
            .GetConstantValue(expression!);

        Assert.True(constant.HasValue);

        return Assert.IsType<string>(constant.Value);
    }

    private static LongRunningOperation RecoveryOperation(
        string kind,
        int checkpointVersion) =>
        new(
            Guid.NewGuid(),
            kind,
            LongRunningOperationState.Running,
            LongRunningOperationRecoveryRegistry.Find(kind)?.Policy
                ?? LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            RootOperationId: null,
            ParentOperationId: null,
            SessionId: null,
            RunId: null,
            InferenceRunId: null,
            BudgetReservationId: null,
            IdempotencyClaimId: null,
            DateTimeOffset.UnixEpoch,
            StartedAt: DateTimeOffset.UnixEpoch,
            HeartbeatAt: DateTimeOffset.UnixEpoch,
            CompletedAt: null,
            LeaseOwner: "architecture-test",
            LeaseExpiresAt: DateTimeOffset.UnixEpoch,
            AttemptCount: 1,
            CheckpointVersion: checkpointVersion,
            CheckpointPayload: null,
            CheckpointReference: null,
            PublicSummary: "architecture-test",
            TerminalErrorCode: null,
            Revision: 1);

    private sealed record SourceMethod(
        IMethodSymbol Symbol,
        SyntaxNode Syntax,
        SemanticModel Model);

    private sealed record SourceType(
        CSharpCompilation Compilation,
        INamedTypeSymbol Symbol);

    private sealed record RecoveryEffectContract(
        string HandlerType,
        string Kind,
        IReadOnlyList<int> ExternalCheckpointVersions,
        IReadOnlyList<string> RequiredCallees,
        IReadOnlyList<int>? OwnerBoundCheckpointVersions = null);

    private static CovenantRuntimeGenerationProvider RuntimeHolder(object facade)
    {
        FieldInfo field = Assert.Single(
            facade.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            static candidate => candidate.FieldType == typeof(CovenantRuntimeGenerationProvider));

        return Assert.IsType<CovenantRuntimeGenerationProvider>(field.GetValue(facade));
    }

    private static string RepositoryRoot() =>
        global::RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot();
}
