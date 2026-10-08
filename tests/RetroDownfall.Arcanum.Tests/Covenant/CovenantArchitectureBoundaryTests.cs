using System.Reflection;
using System.Runtime.CompilerServices;
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
[Collection(RetroDownfall.Arcanum.Tests.Collections.HostedProducerAnalysisCollection.Name)]
public sealed class CovenantArchitectureBoundaryTests
{
    private static readonly Assembly CoreAssembly = typeof(CovenantOperationScope).Assembly;

    private static readonly Assembly InfrastructureAssembly = typeof(CovenantOperationGate).Assembly;

    [Fact]
    [Trait("Category", "HostedProducerAnalysis")]
    [Trait("Category", "HostedProducerProductionAnalysis")]
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
                "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.AuthenticatedJournalRecoveryOwnerEvidence"
            ],
            issuers.Select(MetadataName));

        Assert.All(issuers, static issuer =>
        {
            Assert.True(issuer.IsSealed);
        });

        Assert.Equal(Accessibility.Private, issuers[0].DeclaredAccessibility);

        Assert.NotNull(issuers[0].ContainingType);

        Assert.Equal(Accessibility.Internal, issuers[1].DeclaredAccessibility);

        Assert.Null(issuers[1].ContainingType);

        Dictionary<string, string> expectedIssuerMembers = new(StringComparer.Ordinal)
        {
            ["RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantOfflineTransitionLaunchGapResumption+AdoptedLaunchOwnerEvidence"] = "RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantOfflineTransitionLaunchGapResumption.Evidence",
            ["RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.AuthenticatedJournalRecoveryOwnerEvidence"] = "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery.PrepareAsync"
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

    // Shared literal expectations connect the source proof to its covered runtime classifications.
    private static RecoveryEffectContract[] RecoveryEffectContracts() =>
        [
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

    [Fact]
    [Trait("Category", "HostedProducerAnalysis")]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    [Trait("Category", "HostedProducerAdditionalAnalysis")]
    public void Every_effectful_recovery_handler_has_the_required_source_effects()
    {
        RecoveryEffectContract[] contracts = RecoveryEffectContracts();

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

        string[] handlerNames = handlers
            .Select(static handler => MetadataName(handler.Symbol))
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] recoveryRootNames = HostedGrimoireProducerInventory.AdditionalProductionRoots
            .Where(static root => root.Member == "RecoverAsync")
            .Select(static root => root.EnclosingType)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(handlerNames, recoveryRootNames);

        HostedProducerDiscovery<HostedProducerSite> discovery =
            HostedGrimoireProducerInventory.AdditionalProductionSiteDiscovery;

        string[] expectedEffectfulHandlers = contracts
            .Select(static contract => contract.HandlerType)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] discoveredEffectfulHandlers = discovery.Items
            .Where(static site => site.Kind is HostedProducerSiteKind.ProviderCall
                or HostedProducerSiteKind.FileSystemEffect)
            .Where(site => handlerNames.Contains(site.RootType, StringComparer.Ordinal))
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
        }

        Assert.True(
            missingEffectSites.Count == 0,
            "Required recovery effect sites were not discovered:\n" + string.Join("\n", missingEffectSites));
    }

    [Fact]
    public void Every_effectful_recovery_handler_has_an_exact_admission_classification()
    {
        foreach (RecoveryEffectContract contract in RecoveryEffectContracts())
        {
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

            // ADO.NET is storage too: a Core contract that names DbTransaction has learned that its
            // implementation is a database, which is the same coupling Microsoft.Data.Sqlite would be.
            "System.Data.Common",
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

    /// <summary>
    /// Installed tables that no production code outside the schema tree reads or writes yet, each with the
    /// reason it is installed anyway.
    /// </summary>
    /// <remarks>
    /// A table with no reader or writer still installs, is guarded by triggers and appears in the UTC
    /// inventory, so a reader of the schema would take it for live. Naming it here is the deliberate
    /// alternative to dropping it from the head manifest. The list cannot rot in either direction: a table
    /// that gains a production reference must leave it, and a table that appears with none must be added
    /// with a reason.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> ReservedInstalledTables =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["session_turn_maintenance_steps"] =
                "Child of session_turn_claims: one checkpoint row per maintenance step (summary, title, Saga, Lexicon). "
                + "Its writer is the turn-maintenance checkpoint work, which needs ISessionTurnClaimCoordinator wired into "
                + "the turn begin path first; that coordinator is installed but unconsumed (DESIGN 10.18).",

            ["owner_deletion_operation_intents"] =
                "The managed owner-deletion journal. The Campaigns delete trigger already reads it, but no production "
                + "path prepares an intent yet. Reserved for the Campaign delete path that names its workspace-marker "
                + "effect before the owner row is removed.",

            ["campaign_path_operation_receipts"] =
                "Replay ledger of the Campaign-path administration service, which DESIGN 10.18 lists as unbuilt.",

            ["session_campaign_binding_resolution_receipts"] =
                "Replay ledger of the Session-binding resolution service, which DESIGN 10.18 lists as unbuilt.",

            ["disclosure_subject_aggregates"] =
                "Target of disclosure receipt compaction. Its own head comment records that no live path writes it and "
                + "that a future fold must not join it into external_disclosure_state again.",
        };

    [Fact]
    public void Every_installed_table_has_a_production_reader_or_writer_or_a_written_reservation()
    {
        IReadOnlyList<ProductionSource> production = [.. ProductionSourceInventory.Sources()
            .Where(static source => !source.RelativePath.Contains("/Data/Schema/", StringComparison.Ordinal))];

        Assert.NotEmpty(production);

        string[] tables = [.. RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.AllObjects
            .Where(static schemaObject => schemaObject.Category == RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCategory.Tables)
            .Select(static schemaObject => schemaObject.Name)
            .Order(StringComparer.Ordinal)];

        Assert.NotEmpty(tables);

        string[] unreferenced = [.. tables.Where(table => !production.Any(source => NamesTable(source, table)))];

        // A table nothing reads or writes must be reserved on purpose, with its reason written down.
        Assert.Empty(unreferenced.Except(ReservedInstalledTables.Keys, StringComparer.Ordinal));

        // And a reservation must not outlive its cause: the moment a table is referenced it leaves the list.
        Assert.Empty(ReservedInstalledTables.Keys.Except(unreferenced, StringComparer.Ordinal));

        Assert.All(ReservedInstalledTables, static reservation => Assert.False(string.IsNullOrWhiteSpace(reservation.Value)));

        Assert.Empty(ReservedInstalledTables.Keys.Except(tables, StringComparer.Ordinal));
    }

    private static bool NamesTable(ProductionSource source, string table)
    {
        int index = source.Text.IndexOf(table, StringComparison.Ordinal);

        while (index >= 0)
        {
            bool startsName = index == 0 || !IsIdentifierCharacter(source.Text[index - 1]);

            int end = index + table.Length;

            bool endsName = end >= source.Text.Length || !IsIdentifierCharacter(source.Text[end]);

            if (startsName && endsName)
            {
                return true;
            }

            index = source.Text.IndexOf(table, end, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character == '_';

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

                    // A compound read-and-exclusive lease minted only by AcquireEntryErasureAsync. It
                    // claims installation coverage only after it has closed and drained the whole
                    // installation, so it is never a second ordinary all-scopes read.
                    typeof(CovenantEntryErasureLease),
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
                // The third declared exception, listed first because the scan orders paths ordinally,
                // and it is not a writer at all. The file names covenant_search_documents only as one
                // entry in the append-only effect-digest table-code registry, which gives each erasure
                // plan target a stable byte. Core has no SQLite access, so it cannot reach the
                // projection, and the name stays a plain literal because hiding it from this scan would
                // hide it from the next reader too.
                "src/RetroDownfall.Arcanum.Core/Memory/MemoryErasureEffectFacts.cs",

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

                // The fourth declared exception. A selective entry erasure deletes the entry's search
                // documents and appends content-free absent deltas in the same transaction, under an
                // entry-erasure closure during which the accelerator lease is refused, so the applied
                // tuple never claims a document this file removed.
                "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantEntryErasurePlan.cs",

                "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantIndexRebuilder.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchIndex.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchOutboxWorker.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchSql.cs",
            ],
            writers);
    }

    /// <summary>
    /// The key's binding epoch is joined in one place, <c>CovenantStoreSql.BindingEpoch</c>, which
    /// every pin and mask read calls, and read into C# in one other, <c>CovenantKeyEpochs.ReadAsync</c>.
    /// </summary>
    /// <remarks>
    /// The rule the expression spells, that a missing key row reads as binding epoch 0, is what keeps a
    /// keyless pin bound when the first head creates the key. A copy that drifted from it, by joining
    /// the moving dependency epoch or dropping the <c>COALESCE</c>, would lapse a pin or a mask on one
    /// path and keep it on another, and every one of those paths answers the same operator question.
    ///
    /// <para>Pinned on the column's name rather than on one spelling of the expression. A copy written
    /// with a table alias, with an aggregate, or as a join with <c>COALESCE(k.IncarnationEpoch, 0)</c>,
    /// which is how the design states the rule, contains no fixed phrase a narrower pin could match.
    /// Comments are stripped, and each remaining code token is resolved to the member that holds it, so
    /// a second statement in the owning file is caught as surely as one in another file.</para>
    ///
    /// <para><c>CovenantKeyEpochs.ReadAsync</c> is the one other site. It reads both epochs of one key
    /// into C#, inside the write transaction, for the curation writers to record under. It is not a
    /// join expression, so it applies the same missing-row rule with an aggregate rather than calling
    /// the shared expression.</para>
    ///
    /// <para>The schema's <c>.sql</c> files are scanned too, and the ones whose code names the column are
    /// listed: the table, its immutability guard, the three head triggers that create a key row at
    /// binding epoch 0, and the version 6 steps that add, backfill, guard and re-create them. Each
    /// defines or writes the column and none reads it into a comparison, so a new schema object that
    /// names it has to be added here deliberately.</para>
    /// </remarks>
    [Fact]
    public void The_binding_epoch_expression_has_one_source()
    {
        const string Owner = "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantStoreSql.cs";

        const string EpochReader = "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantKeyEpochs.cs";

        const string Column = "IncarnationEpoch";

        const string Expression = "SELECT IncarnationEpoch FROM covenant_key_epochs";

        const string InlineRule = "COALESCE((SELECT IncarnationEpoch";

        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.Sources();

        string[] sites = [.. sources
            .Where(static source => source.Names(Column))
            .SelectMany(static source => ColumnSites(source, Column))
            .Order(StringComparer.Ordinal)];

        Assert.Equal(
            [
                $"{EpochReader} CovenantKeyEpochs.ReadAsync",
                $"{Owner} CovenantStoreSql.BindingEpoch",
            ],
            sites);

        string[] files = [.. sources
            .Where(static source => source.Names(Expression))
            .Select(static source => source.RelativePath)
            .Order(StringComparer.Ordinal)];

        Assert.Equal([Owner], files);

        ProductionSource owner = sources.Single(static source => source.IsExactOwner(Owner));

        Assert.Equal(1, owner.Occurrences(Expression));

        Assert.Equal(1, sources.Sum(static source => source.Occurrences(InlineRule)));

        // The one occurrence is the definition itself, not a statement that happens to live beside it.
        MethodDeclarationSyntax definition = Assert.Single(
            CSharpSyntaxTree.ParseText(owner.Text).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "BindingEpoch");

        Assert.Contains(InlineRule, definition.ToString(), StringComparison.Ordinal);

        const string Canonical = "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/";

        Assert.Equal(
            [
                Canonical + "Tables/covenant_key_epochs.sql",
                Canonical + "Transitions/V6/030_covenant_key_epochs_incarnation_epoch.sql",
                Canonical + "Transitions/V6/050_covenant_key_epochs_incarnation_backfill.sql",
                Canonical + "Transitions/V6/070_covenant_key_epochs_guard_incarnation.sql",
                Canonical + "Transitions/V6/220_covenant_heads_key_epoch_insert.sql",
                Canonical + "Transitions/V6/230_covenant_heads_key_epoch_update.sql",
                Canonical + "Transitions/V6/240_covenant_heads_key_epoch_delete.sql",
                Canonical + "Triggers/covenant_heads_key_epoch_delete.sql",
                Canonical + "Triggers/covenant_heads_key_epoch_insert.sql",
                Canonical + "Triggers/covenant_heads_key_epoch_update.sql",
                Canonical + "Triggers/covenant_key_epochs_guard_incarnation.sql",
            ],
            SchemaFilesNaming(Column));
    }

    /// <summary>
    /// Each code token of one source that names <paramref name="column"/>, as the file and the
    /// <c>Type.Member</c> holding it, once per occurrence.
    /// </summary>
    private static IEnumerable<string> ColumnSites(ProductionSource source, string column)
    {
        foreach (SyntaxToken token in CSharpSyntaxTree.ParseText(source.Text).GetRoot().DescendantTokens())
        {
            int occurrences = 0;

            for (int index = token.Text.IndexOf(column, StringComparison.Ordinal);
                index >= 0;
                index = token.Text.IndexOf(column, index + column.Length, StringComparison.Ordinal))
            {
                occurrences++;
            }

            if (occurrences == 0)
            {
                continue;
            }

            string type = token.Parent?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
                ?? "<no type>";

            string member = token.Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault() switch
            {
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                FieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(static variable => variable.Identifier.ValueText)),
                ConstructorDeclarationSyntax => ".ctor",
                BaseTypeDeclarationSyntax declared => declared.Identifier.ValueText,
                { } other => other.Kind().ToString(),
                null => "<no member>",
            };

            for (int found = 0; found < occurrences; found++)
            {
                yield return $"{source.RelativePath} {type}.{member}";
            }
        }
    }

    /// <summary>
    /// The authored <c>.sql</c> files under <c>src</c> whose code, with <c>--</c> comment lines removed,
    /// names <paramref name="column"/>, repository-relative and ordered.
    /// </summary>
    private static string[] SchemaFilesNaming(string column)
    {
        string root = RetroDownfall.Arcanum.Tests.NativeSqlCipher.NativeSqlCipherTestPaths.RepositoryRoot();

        string separator = Path.DirectorySeparatorChar.ToString();

        return [.. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.sql", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                && !file.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
            .Where(file => File.ReadLines(file)
                .Where(static line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal))
                .Any(line => line.Contains(column, StringComparison.Ordinal)))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];
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

        AssertSingleOwnerBoundRecoveryHandler<CovenantLaunchGapMutationRecoveryHandler>(services);

        AssertSingleOwnerBoundRecoveryHandler<CovenantLaunchGapFactoryResetRecoveryHandler>(services);

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

        AssertSingleOwnerBoundRecoveryHandler<DataRetentionMutationRecoveryHandler>(builder.Services);

        AssertSingleOwnerBoundRecoveryHandler<DataRetentionFactoryResetRecoveryHandler>(builder.Services);

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

    private static void AssertSingleOwnerBoundRecoveryHandler<THandler>(
        IServiceCollection services)
    {
        ServiceDescriptor concreteDescriptor = Assert.Single(
            services,
            static candidate => candidate.ServiceType == typeof(THandler));

        Assert.Equal(ServiceLifetime.Scoped, concreteDescriptor.Lifetime);

        Assert.Equal(typeof(THandler), concreteDescriptor.ImplementationType);

        object concrete = RuntimeHelpers.GetUninitializedObject(typeof(THandler));

        OwnerBoundAliasProbeProvider probe = new(typeof(THandler), concrete);

        object normal = Assert.Single(InvokeOwnerBoundAliases(
            services,
            typeof(ILongRunningOperationRecoveryHandler),
            probe));

        object authenticated = Assert.Single(InvokeOwnerBoundAliases(
            services,
            typeof(IAuthenticatedCovenantErasureRecoveryHandler),
            probe));

        Assert.Same(concrete, normal);

        Assert.Same(concrete, authenticated);
    }

    private static object[] InvokeOwnerBoundAliases(
        IServiceCollection services,
        Type serviceType,
        IServiceProvider probe) =>
        services
            .Where(descriptor => descriptor.ServiceType == serviceType
                && descriptor.Lifetime == ServiceLifetime.Scoped
                && descriptor.ImplementationFactory is not null)
            .Select(descriptor =>
            {
                try
                {
                    return descriptor.ImplementationFactory!(probe);
                }
                catch (Exception)
                {
                    return null;
                }
            })
            .OfType<object>()
            .ToArray();

    private sealed class OwnerBoundAliasProbeProvider(Type handlerType, object handler)
        : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == handlerType ? handler : null;
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
