using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal enum GrimoirePathAuthority : byte
{

    LiveGrimoire = 1,

    StoppedHostGrimoire = 2,

    PreReadinessGrimoire = 3,

    ShutdownGrimoire = 4,

    ArchiveOrSnapshot = 5,

    RestoreOrCompactionStaging = 6,

    DesignTimeScratch = 7,

    NativeRuntimeValidation = 8,

    NotGrimoire = 9,

}

internal enum GrimoireAcquisitionKind : byte
{

    ServingEfOrdinary = 1,

    ServingRawOrdinary = 2,

    JournalMaintenance = 3,

    LegacyV3Maintenance = 4,

    BootstrapOrShutdown = 5,

    StoppedHostRecovery = 6,

    StagingOrArchive = 7,

    DesignTimeOrNativeValidation = 8,

    NonGrimoireCandidate = 9,

}

internal enum AcquisitionConstructKind : byte
{

    UseSqlite = 1,

    AddDbContext = 2,

    ProviderOpen = 3,

    ProviderObjectCreation = 4,

    MarkedRouteDeclaration = 5,

    MarkedRouteInvocation = 6,

}

internal readonly record struct AcquisitionIdentity(
    string RelativePath,
    string EnclosingType,
    string EnclosingMember,
    AcquisitionConstructKind ConstructKind,
    string CalleeOrConstructedType,
    int Arity,
    string Fingerprint);

internal sealed record ExactNonServingProof(
    ExactNonServingProofKind Kind,
    string EvidenceMember,
    int RemovalIssue = 0);

internal sealed record GrimoireAcquisitionCatalogEntry(
    AcquisitionIdentity Identity,
    GrimoirePathAuthority PathAuthority,
    GrimoireAcquisitionKind AcquisitionKind,
    GrimoireRuntimeAdmissionRoute RuntimeRoute,
    ExactNonServingProof? NonServingProof);

internal enum GrimoireRuntimeAdmissionRoute : byte
{

    SharedEfInterceptor = 1,

    OrdinaryConnectionFactory = 2,

    MaintenanceConnectionFactory = 3,

    StoppedHostConnectionFactory = 4,

    ExactNonServingProof = 5,

}

internal enum ExactNonServingProofKind : byte
{

    StoppedHostAuthority = 1,

    PreReadinessHeldLock = 2,

    ShutdownHeldLock = 3,

    TypedStagingOrSnapshot = 4,

    DesignTimeScratch = 5,

    NativeRuntimeValidation = 6,

    NegativeNonDatabaseProof = 7,

    LegacyV3ExclusiveLease = 8,

}

internal enum InventoryFailureCode : byte
{

    UncataloguedDiscovery = 1,

    StaleCatalogEntry = 2,

    DuplicateCatalogEntry = 3,

    DuplicateDiscovery = 4,

    MissingRequiredRouteMarker = 5,

    DuplicateMarkedRouteName = 6,

    InvalidClassification = 7,

    MissingNonServingProof = 8,

}

internal readonly record struct AcquisitionSource(string RelativePath, string Text);

internal sealed record InventoryFailure(
    InventoryFailureCode Code,
    AcquisitionIdentity? Identity,
    string Detail);

internal static class GrimoireConnectionAcquisitionScanner
{

    private static readonly HashSet<string> ProviderOpenNames =
    [
        "Open",
        "OpenAsync",
        "OpenConnection",
        "OpenConnectionAsync",
    ];

    private static readonly HashSet<string> OpaqueAcquisitionRouteReturnNames =
    [
        "IGrimoireOrdinaryConnectionLease",
        "IGrimoireMaintenanceConnectionLease",
        "ICovenantV3MaintenanceConnectionLease",
        "IStoppedHostGrimoireConnectionLease",
        "HostToolsMarkerPairResetDatabaseSession",
    ];

    private static readonly HashSet<string> RecursiveReturnWrappers =
    [
        "Task",
        "ValueTask",
        "Result",
    ];

    internal static IReadOnlyList<AcquisitionIdentity> Discover(IEnumerable<AcquisitionSource> sources)
    {

        List<(AcquisitionSource Source, CompilationUnitSyntax Root)> parsed = Parse(sources);

        List<AcquisitionIdentity> identities = [];

        Dictionary<string, int> markedRouteArities = [];

        foreach ((AcquisitionSource source, CompilationUnitSyntax root) in parsed)
        {

            foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {

                if (IsConcrete(method) && IsMarked(method))
                {

                    markedRouteArities[method.Identifier.ValueText] = method.ParameterList.Parameters.Count;

                    identities.Add(MarkedRouteIdentity(
                        source.RelativePath,
                        method,
                        method.Identifier.ValueText,
                        method.ParameterList.Parameters.Count));

                }

            }

            foreach (LocalFunctionStatementSyntax localFunction in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            {

                if (IsConcrete(localFunction) && IsMarked(localFunction))
                {

                    markedRouteArities[localFunction.Identifier.ValueText] = localFunction.ParameterList.Parameters.Count;

                    identities.Add(MarkedRouteIdentity(
                        source.RelativePath,
                        localFunction,
                        localFunction.Identifier.ValueText,
                        localFunction.ParameterList.Parameters.Count));

                }

            }

        }

        foreach ((AcquisitionSource source, CompilationUnitSyntax root) in parsed)
        {

            foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {

                string terminalName = TerminalName(invocation.Expression);

                int arity = invocation.ArgumentList.Arguments.Count;

                if (terminalName == "UseSqlite")
                {

                    identities.Add(Identity(
                        source.RelativePath,
                        invocation,
                        AcquisitionConstructKind.UseSqlite,
                        Tokens(invocation.Expression),
                        arity));

                }

                if (terminalName.StartsWith("AddDbContext", StringComparison.Ordinal))
                {

                    identities.Add(Identity(
                        source.RelativePath,
                        invocation,
                        AcquisitionConstructKind.AddDbContext,
                        Tokens(invocation.Expression),
                        arity));

                }

                if (ProviderOpenNames.Contains(terminalName))
                {

                    identities.Add(Identity(
                        source.RelativePath,
                        invocation,
                        AcquisitionConstructKind.ProviderOpen,
                        Tokens(invocation.Expression),
                        arity));

                }

                if (markedRouteArities.TryGetValue(terminalName, out int markedArity)
                    && markedArity == arity)
                {

                    identities.Add(Identity(
                        source.RelativePath,
                        invocation,
                        AcquisitionConstructKind.MarkedRouteInvocation,
                        terminalName,
                        arity));

                }

            }

            foreach (ObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {

                string typeName = TerminalName(creation.Type);

                if (typeName is "DbConnection" or "SqliteConnection")
                {

                    identities.Add(Identity(
                        source.RelativePath,
                        creation,
                        AcquisitionConstructKind.ProviderObjectCreation,
                        Tokens(creation.Type),
                        creation.ArgumentList?.Arguments.Count ?? 0));

                }

            }

        }

        return identities;

    }

    internal static IReadOnlyList<InventoryFailure> Validate(
        IReadOnlyList<AcquisitionIdentity> discoveries,
        IReadOnlyList<GrimoireAcquisitionCatalogEntry> catalog)
    {

        List<InventoryFailure> failures = [];

        foreach (IGrouping<AcquisitionIdentity, AcquisitionIdentity> duplicate in discoveries.GroupBy(static identity => identity))
        {

            if (duplicate.Count() > 1)
            {

                failures.Add(new(
                    InventoryFailureCode.DuplicateDiscovery,
                    duplicate.Key,
                    "The syntax scanner resolved this construct more than once."));

            }

        }

        foreach (IGrouping<AcquisitionIdentity, GrimoireAcquisitionCatalogEntry> duplicate in catalog.GroupBy(
                     static entry => entry.Identity))
        {

            if (duplicate.Count() > 1)
            {

                failures.Add(new(
                    InventoryFailureCode.DuplicateCatalogEntry,
                    duplicate.Key,
                    "The catalog contains this construct more than once."));

            }

        }

        HashSet<AcquisitionIdentity> discovered = [.. discoveries];

        HashSet<AcquisitionIdentity> catalogued = [.. catalog.Select(static entry => entry.Identity)];

        foreach (AcquisitionIdentity discovery in discovered.Except(catalogued))
        {

            failures.Add(new(
                InventoryFailureCode.UncataloguedDiscovery,
                discovery,
                "The syntax scanner found a construct with no exact catalog entry."));

        }

        foreach (AcquisitionIdentity entry in catalogued.Except(discovered))
        {

            failures.Add(new(
                InventoryFailureCode.StaleCatalogEntry,
                entry,
                "The catalog names a construct the syntax scanner no longer finds."));

        }

        foreach (GrimoireAcquisitionCatalogEntry entry in catalog)
        {

            if (HasBroadIdentity(entry.Identity))
            {

                failures.Add(new(
                    InventoryFailureCode.InvalidClassification,
                    entry.Identity,
                    "A catalog identity must name one exact authored construct, not a wildcard."));

            }

            if (entry.NonServingProof is not null && !HasExactProofEvidence(entry))
            {

                failures.Add(new(
                    InventoryFailureCode.InvalidClassification,
                    entry.Identity,
                    "A non-serving proof must derive from one exact catalog identity."));

            }

            bool canonicalLivePath = entry.Identity.Fingerprint.Contains(
                "ArcanumPaths.GrimoireDatabaseFile",
                StringComparison.Ordinal);

            if ((canonicalLivePath && entry.PathAuthority != GrimoirePathAuthority.LiveGrimoire)
                || !IsValidClassification(entry))
            {

                failures.Add(new(
                    InventoryFailureCode.InvalidClassification,
                    entry.Identity,
                    "A live Grimoire acquisition must use a live authority and serving classification."));

            }

            if (entry.PathAuthority != GrimoirePathAuthority.LiveGrimoire && entry.NonServingProof is null)
            {

                failures.Add(new(
                    InventoryFailureCode.MissingNonServingProof,
                    entry.Identity,
                    "Every non-live or exact-negative candidate requires an exact proof."));

            }

        }

        foreach (IGrouping<string, GrimoireAcquisitionCatalogEntry> duplicate in catalog
            .Where(static entry => entry.NonServingProof is not null)
            .GroupBy(static entry => entry.NonServingProof!.EvidenceMember, StringComparer.Ordinal))
        {

            if (duplicate.Count() > 1)
            {

                foreach (GrimoireAcquisitionCatalogEntry entry in duplicate)
                {

                    failures.Add(new(
                        InventoryFailureCode.InvalidClassification,
                        entry.Identity,
                        "A non-serving proof cannot be shared by multiple catalog identities."));

                }

            }

        }

        return failures;

    }

    internal static IReadOnlyList<InventoryFailure> ValidateMarkerCoverage(IEnumerable<AcquisitionSource> sources)
    {

        List<InventoryFailure> failures = [];

        List<(AcquisitionSource Source, CompilationUnitSyntax Root)> parsed = Parse(sources);

        List<(string Name, AcquisitionIdentity Identity)> markedRoutes = [];

        foreach ((AcquisitionSource source, CompilationUnitSyntax root) in parsed)
        {

            foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {

                ValidateMethodMarker(source, method, failures, markedRoutes);

            }

            foreach (LocalFunctionStatementSyntax localFunction in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            {

                ValidateLocalFunctionMarker(source, localFunction, failures, markedRoutes);

            }

        }

        foreach (IGrouping<string, (string Name, AcquisitionIdentity Identity)> duplicate in markedRoutes.GroupBy(
                     static route => route.Name,
                     StringComparer.Ordinal))
        {

            if (duplicate.Count() > 1)
            {

                failures.Add(new(
                    InventoryFailureCode.DuplicateMarkedRouteName,
                    duplicate.First().Identity,
                    "Marked route names must be repository-unique."));

            }

        }

        return failures;

    }

    internal static IReadOnlyList<GrimoireAcquisitionCatalogEntry> Catalog() =>
        BindProofEvidence(
        [
        new(
            new("src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Configuration.cs", "CliCommandTree", "BuildConfig(1)", AcquisitionConstructKind.ProviderOpen, "handler.Open", 0, "handler.Open()"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "CliCommandTree.BuildConfig(1)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs", "MemoryEndpoints", "HandleSearchAsync(6)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 3, "OpenConnectionAsync(db,connections,context.RequestAborted)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs", "MemoryEndpoints", "BuildStatusAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 3, "OpenConnectionAsync(db,connections,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs", "MemoryEndpoints", "OpenConnectionAsync(3)", AcquisitionConstructKind.MarkedRouteInvocation, "AcquireScopedAsync", 3, "connections.AcquireScopedAsync(scopedConnection,CovenantSqliteConnectionMode.ReadOnly,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs", "MemoryEndpoints", "OpenConnectionAsync(3)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenConnectionAsync", 3, "OpenConnectionAsync(3)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs", "MemoryEndpoints", "HandleSearchAsync(6)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenConnectionAsync", 3, "OpenConnectionAsync(db,connections,context.RequestAborted)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs", "MemoryEndpoints", "BuildStatusAsync(7)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenConnectionAsync", 3, "OpenConnectionAsync(db,connections,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Api/Tower/SessionDivinationEndpoints.cs", "SessionDivinationEndpoints", "JoinSessionMetadataAsync(7)", AcquisitionConstructKind.MarkedRouteInvocation, "AcquireScopedAsync", 3, "connections.AcquireScopedAsync(scopedConnection,CovenantSqliteConnectionMode.ReadOnly,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs", "WizardIntelligenceProvider", "JoinWorkspaceChunkMetadataAsync(5)", AcquisitionConstructKind.MarkedRouteInvocation, "AcquireScopedAsync", 3, "connections.AcquireScopedAsync(scopedConnection,CovenantSqliteConnectionMode.ReadOnly,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Api/Health/GrimoireLivenessProbe.cs", "GrimoireLivenessProbe", "ExecuteProbeAsync(1)", AcquisitionConstructKind.MarkedRouteInvocation, "AcquireScopedAsync", 3, "connections.AcquireScopedAsync(scopedConnection,CovenantSqliteConnectionMode.ReadOnly,timeoutCts.Token)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Api/Workspaces/WorkspaceDivinationEndpoints.cs", "WorkspaceDivinationEndpoints", "JoinWorkspaceChunksAsync(6)", AcquisitionConstructKind.MarkedRouteInvocation, "AcquireScopedAsync", 3, "connections.AcquireScopedAsync(scopedConnection,CovenantSqliteConnectionMode.ReadOnly,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/EmbeddingsResetService.cs", "EmbeddingsResetService", "PurgeLabeledKindAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "AcquireScopedAsync", 3, "_connections.AcquireScopedAsync(scopedConnection,CovenantSqliteConnectionMode.ReadOnly,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/EmbeddingsResetService.cs", "EmbeddingsResetService", "ResetAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/EmbeddingsResetService.cs", "EmbeddingsResetService", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/DivinationService.cs", "DivinationService", "SearchAsync(7)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/DivinationService.cs", "DivinationService", "SearchScopedAsync(11)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/DivinationService.cs", "DivinationService", "SearchCampaignScopedAsync(8)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/WorkspaceIndexInspectorService.cs", "WorkspaceIndexInspectorService", "GetStatusAsync(5)", AcquisitionConstructKind.ProviderOpen, "_db.Database.OpenConnectionAsync", 1, "_db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/WorkspaceIndexInspectorService.cs", "WorkspaceIndexInspectorService", "GetChunksAsync(5)", AcquisitionConstructKind.ProviderOpen, "_db.Database.OpenConnectionAsync", 1, "_db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "DiscoverScopesAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "PruneRemovedScopesAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "EnumerateLeafSourcesAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "GetCurrentGenerationAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "BeginGenerationAsync(9)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "AppendNodesAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "SetParentAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "PublishGenerationAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "AbandonGenerationAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "ReconcileGenerationsAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "GetLayerNodesAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "GetNodeEmbeddingsAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "TryGetReusableSummaryAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "HydrateRetrievedNodesAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "GetTerminalLayerAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "GetScopeStatusesAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "CountPublishedNodesAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs", "TapestryStore", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs", "SessionAttachmentIndexRepository", "ReconcileAndFindPendingAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs", "SessionAttachmentIndexRepository", "SetPendingAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs", "SessionAttachmentIndexRepository", "MarkWithoutIndexAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs", "SessionAttachmentIndexRepository", "BeginReplaceAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs", "SessionAttachmentIndexRepository", "AppendReplaceBatchAsync(8)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs", "SessionAttachmentIndexRepository", "CompleteReplaceAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs", "SessionAttachmentIndexRepository", "GetStateAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs", "SessionAttachmentIndexRepository", "GetStatusesAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs", "SessionAttachmentIndexRepository", "GetChunksForAttachmentAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs", "SessionAttachmentIndexRepository", "GetRetrievedChunksAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs", "SessionAttachmentIndexRepository", "DeleteForSessionInAmbientTransactionAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs", "SessionAttachmentIndexRepository", "UpsertStateAsync(8)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs", "SessionAttachmentIndexRepository", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/A2A/A2AExternalSpendLedger.cs", "A2AExternalSpendLedger", "GetTodayAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantCampaignScopeProbe.cs", "CovenantCampaignScopeProbe", "HasDeletionEventAsync(4)", AcquisitionConstructKind.MarkedRouteInvocation, "AcquireScopedAsync", 3, "connections.AcquireScopedAsync(scopedConnection,CovenantSqliteConnectionMode.ReadOnly,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.RestartRootProof.cs", "CampaignPathMarkerLifecycle", "ReopenAndProveRootAsync(3)", AcquisitionConstructKind.ProviderOpen, "CampaignPathMarkerRootAuthority.Instance.OpenAsync", 6, "CampaignPathMarkerRootAuthority.Instance.OpenAsync(_rootOpener,row.CampaignId,row.PriorRevision,reopenedIdentity,recordedDisplayPath,cancellationToken)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "CampaignPathMarkerLifecycle.ReopenAndProveRootAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerRootAuthority.cs", "CampaignPathMarkerRootAuthority.Factory", "OpenAsync(6)", AcquisitionConstructKind.ProviderOpen, "OpenAsync", 7, "OpenAsync(opener,campaignId,pathRevision,expectedPhysicalIdentityDigest,canonicalDisplayPath,requireExistingMarkerDirectory:false,cancellationToken)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "CampaignPathMarkerRootAuthority.Factory.OpenAsync(6)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerRootAuthority.cs", "CampaignPathMarkerRootAuthority.Factory", "OpenExistingAsync(6)", AcquisitionConstructKind.ProviderOpen, "OpenAsync", 7, "OpenAsync(opener,campaignId,pathRevision,expectedPhysicalIdentityDigest,canonicalDisplayPath,requireExistingMarkerDirectory:true,cancellationToken)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "CampaignPathMarkerRootAuthority.Factory.OpenExistingAsync(6)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.RestoreInventory.cs", "CampaignPathMarkerLifecycle", "ObserveRegisteredRootAsync(2)", AcquisitionConstructKind.ProviderOpen, "CampaignPathMarkerRootAuthority.Instance.OpenAsync", 6, "CampaignPathMarkerRootAuthority.Instance.OpenAsync(_rootOpener,root.CampaignId,root.Revision,identity,root.DisplayPath,cancellationToken)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "CampaignPathMarkerLifecycle.ObserveRegisteredRootAsync(2)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceHostedService.cs", "CovenantMaintenanceHostedService", "RunSweepAsync(4)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.TurnCommit.cs", "GrimoireRepository", "CommitWithinImmediateTransactionAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "AcquireScopedAsync", 3, "_connections.AcquireScopedAsync(connection,CovenantSqliteConnectionMode.ReadWrite,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionEntryPersistence.cs", "SessionEntryPersistence", "ReadProbeOnFreshConnectionAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenFreshAsync", 2, "_connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionEntryPersistence.cs", "SessionEntryPersistence", "ReadReceiptOnFreshConnectionAsync(3)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenFreshAsync", 2, "_connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Repositories/CampaignRepository.cs", "CampaignRepository", "AddAsync(2)", AcquisitionConstructKind.ProviderOpen, "_db.Database.OpenConnectionAsync", 1, "_db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs", "GrimoireRepository", "SearchArchivesAsync(3)", AcquisitionConstructKind.ProviderOpen, "_db.Database.OpenConnectionAsync", 1, "_db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs", "GrimoireRepository", "GetTodaySpendAsync(1)", AcquisitionConstructKind.ProviderOpen, "_db.Database.OpenConnectionAsync", 1, "_db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),

        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostInstallationResetPlanReadAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenStoppedHostInstallationResetPlanReadAsync", 2, "OpenStoppedHostInstallationResetPlanReadAsync(2)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostInstallationResetPlanReadAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostInstallationResetWorkspaceResolutionAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenStoppedHostInstallationResetWorkspaceResolutionAsync", 2, "OpenStoppedHostInstallationResetWorkspaceResolutionAsync(2)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostInstallationResetWorkspaceResolutionAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostInstallationResetIdentityReadAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenStoppedHostInstallationResetIdentityReadAsync", 2, "OpenStoppedHostInstallationResetIdentityReadAsync(2)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostInstallationResetIdentityReadAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync", 2, "OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync(2)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostInstallationResetApplyAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenStoppedHostInstallationResetApplyAsync", 2, "OpenStoppedHostInstallationResetApplyAsync(2)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostInstallationResetApplyAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostMarkerPairResetAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenStoppedHostMarkerPairResetAsync", 2, "OpenStoppedHostMarkerPairResetAsync(2)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostMarkerPairResetAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostLeaseAsync(4)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenStoppedHostLeaseAsync", 4, "OpenStoppedHostLeaseAsync(4)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostLeaseAsync(4)"),

        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs", "InstallationResetExistingGrimoire", "PlanUnderStoppedHostAuthorityAsync(3)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenStoppedHostInstallationResetPlanReadAsync", 2, "factory.OpenStoppedHostInstallationResetPlanReadAsync(authority,token)"), "InstallationResetExistingGrimoire.PlanUnderStoppedHostAuthorityAsync(3)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs", "InstallationResetExistingGrimoire", "ApplyUnderStoppedHostAuthorityAsync(3)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenStoppedHostInstallationResetApplyAsync", 2, "factory.OpenStoppedHostInstallationResetApplyAsync(authority,token)"), "InstallationResetExistingGrimoire.ApplyUnderStoppedHostAuthorityAsync(3)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs", "InstallationResetExistingGrimoire", "ResolveWorkspaceUnderStoppedHostAuthorityAsync(3)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenStoppedHostInstallationResetWorkspaceResolutionAsync", 2, "factory.OpenStoppedHostInstallationResetWorkspaceResolutionAsync(authority,token)"), "InstallationResetExistingGrimoire.ResolveWorkspaceUnderStoppedHostAuthorityAsync(3)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs", "InstallationResetExistingGrimoire", "ReadIdentityUnderStoppedHostAuthorityAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenStoppedHostInstallationResetIdentityReadAsync", 2, "factory.OpenStoppedHostInstallationResetIdentityReadAsync(authority,token)"), "InstallationResetExistingGrimoire.ReadIdentityUnderStoppedHostAuthorityAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs", "InstallationResetExistingGrimoire", "ReadHostToolsEvidenceUnderStoppedHostAuthorityAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync", 2, "factory.OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync(authority,token)"), "InstallationResetExistingGrimoire.ReadHostToolsEvidenceUnderStoppedHostAuthorityAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs", "InstallationResetExistingGrimoire", "ExecuteUnderStoppedHostAuthorityAsync(5)", AcquisitionConstructKind.UseSqlite, "newDbContextOptionsBuilder<ArcanumDbContext>().UseSqlite", 2, "newDbContextOptionsBuilder<ArcanumDbContext>().UseSqlite(lease.Connection,contextOwnsConnection:false)"), "InstallationResetExistingGrimoire.ExecuteUnderStoppedHostAuthorityAsync(5)"),

        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabase.cs", "HostToolsMarkerPairResetDatabase", "OpenHostToolsMarkerPairResetDatabaseSessionAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenStoppedHostMarkerPairResetAsync", 2, "_connections.OpenStoppedHostMarkerPairResetAsync(authority,cancellationToken)"), "HostToolsMarkerPairResetDatabase.OpenHostToolsMarkerPairResetDatabaseSessionAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabase.cs", "HostToolsMarkerPairResetDatabase", "OpenHostToolsMarkerPairResetDatabaseSessionAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenHostToolsMarkerPairResetDatabaseSessionAsync", 2, "OpenHostToolsMarkerPairResetDatabaseSessionAsync(2)"), "HostToolsMarkerPairResetDatabase.OpenHostToolsMarkerPairResetDatabaseSessionAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabase.cs", "HostToolsMarkerPairResetDatabase", "CreateSession(1)", AcquisitionConstructKind.MarkedRouteDeclaration, "CreateSession", 1, "CreateSession(1)"), "HostToolsMarkerPairResetDatabase.CreateSession(1)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabase.cs", "HostToolsMarkerPairResetDatabase", "OpenHostToolsMarkerPairResetDatabaseSessionAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "CreateSession", 1, "CreateSession(lease)"), "HostToolsMarkerPairResetDatabase.OpenHostToolsMarkerPairResetDatabaseSessionAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs", "HostToolsMarkerPairResetCoordinator", "OpenDatabaseAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenDatabaseAsync", 2, "OpenDatabaseAsync(2)"), "HostToolsMarkerPairResetCoordinator.OpenDatabaseAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs", "HostToolsMarkerPairResetCoordinator", "BeginCoreAsync(5)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenDatabaseAsync", 2, "OpenDatabaseAsync(heldInstallationLock,cancellationToken)"), "HostToolsMarkerPairResetCoordinator.BeginCoreAsync(5)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs", "HostToolsMarkerPairResetCoordinator", "ResumeAsync(3)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenDatabaseAsync", 2, "OpenDatabaseAsync(heldInstallationLock,recoveryCheckpoint.Token)"), "HostToolsMarkerPairResetCoordinator.ResumeAsync(3)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs", "HostToolsMarkerPairResetCoordinator", "ResumeFromDatabaseDeletedOsAbsentAsync(4)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenDatabaseAsync", 2, "OpenDatabaseAsync(heldInstallationLock,cancellationToken)"), "HostToolsMarkerPairResetCoordinator.ResumeFromDatabaseDeletedOsAbsentAsync(4)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs", "HostToolsMarkerPairResetCoordinator", "ResumeFromAbsentPairStateAsync(4)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenDatabaseAsync", 2, "OpenDatabaseAsync(heldInstallationLock,cancellationToken)"), "HostToolsMarkerPairResetCoordinator.ResumeFromAbsentPairStateAsync(4)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs", "HostToolsMarkerPairResetCoordinator", "OpenDatabaseAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenHostToolsMarkerPairResetDatabaseSessionAsync", 2, "_database.OpenHostToolsMarkerPairResetDatabaseSessionAsync(authority,cancellationToken)"), "HostToolsMarkerPairResetCoordinator.OpenDatabaseAsync(2)"),

        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostInstallationResetPlanReadAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenStoppedHostLeaseAsync", 4, "OpenStoppedHostLeaseAsync(authority,StoppedHostGrimoireOperation.InstallationResetPlanRead,CovenantSqliteConnectionMode.ReadOnly,cancellationToken)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostInstallationResetPlanReadAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostInstallationResetWorkspaceResolutionAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenStoppedHostLeaseAsync", 4, "OpenStoppedHostLeaseAsync(authority,StoppedHostGrimoireOperation.InstallationResetWorkspaceResolution,CovenantSqliteConnectionMode.ReadOnly,cancellationToken)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostInstallationResetWorkspaceResolutionAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostInstallationResetIdentityReadAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenStoppedHostLeaseAsync", 4, "OpenStoppedHostLeaseAsync(authority,StoppedHostGrimoireOperation.InstallationResetIdentityRead,CovenantSqliteConnectionMode.ReadOnly,cancellationToken)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostInstallationResetIdentityReadAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenStoppedHostLeaseAsync", 4, "OpenStoppedHostLeaseAsync(authority,StoppedHostGrimoireOperation.InstallationResetHostToolsEvidenceRead,CovenantSqliteConnectionMode.ReadOnly,cancellationToken)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostInstallationResetApplyAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenStoppedHostLeaseAsync", 4, "OpenStoppedHostLeaseAsync(authority,StoppedHostGrimoireOperation.InstallationResetApply,CovenantSqliteConnectionMode.ReadWrite,cancellationToken)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostInstallationResetApplyAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostMarkerPairResetAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenStoppedHostLeaseAsync", 4, "OpenStoppedHostLeaseAsync(authority,StoppedHostGrimoireOperation.MarkerPairReset,CovenantSqliteConnectionMode.ReadWrite,cancellationToken)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostMarkerPairResetAsync(2)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostLeaseAsync(4)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostLeaseAsync(4)"),
        Stopped(new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs", "StoppedHostGrimoireConnectionFactory", "OpenStoppedHostLeaseAsync(4)", AcquisitionConstructKind.ProviderObjectCreation, "SqliteConnection", 1, "newSqliteConnection(newSqliteConnectionStringBuilder{DataSource=canonicalDatabasePath,Password=_passphrase.Passphrase,Pooling=false,Mode=modeisCovenantSqliteConnectionMode.ReadOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWrite,Cache=SqliteCacheMode.Private,}.ToString())"), "StoppedHostGrimoireConnectionFactory.OpenStoppedHostLeaseAsync(4)"),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveStore.cs", "InstallationResetActiveStore", "OpenEnvelope(4)", AcquisitionConstructKind.ProviderOpen, "InstallationResetActiveRecordAuthenticator.Open", 4, "InstallationResetActiveRecordAuthenticator.Open(key.Value,location,installationId,envelope)"),
            GrimoirePathAuthority.StoppedHostGrimoire,
            GrimoireAcquisitionKind.StoppedHostRecovery,
            GrimoireRuntimeAdmissionRoute.StoppedHostConnectionFactory,
            new(ExactNonServingProofKind.StoppedHostAuthority, "InstallationResetActiveStore.OpenEnvelope(4)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Diagnostics/GrimoireDiagnostics.cs", "GrimoireProbe", "OpenReadOnlyAsync(2)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs", "WorkspaceIndexingService", "DeleteOrphanedChunksAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 2, "OpenConnectionAsync(db,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs", "WorkspaceIndexingService", "LoadExistingChunkIdsAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 2, "OpenConnectionAsync(db,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs", "WorkspaceIndexingService", "UpdateChunkMetadataAsync(9)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 2, "OpenConnectionAsync(db,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs", "WorkspaceIndexingService", "DeleteChunkByIdAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 2, "OpenConnectionAsync(db,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs", "WorkspaceIndexingService", "LoadExistingFileLastWriteTimesAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 2, "OpenConnectionAsync(db,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs", "WorkspaceIndexingService", "DeleteExistingChunksAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 2, "OpenConnectionAsync(db,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs", "WorkspaceIndexingService", "InsertChunkAsync(14)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 2, "OpenConnectionAsync(db,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs", "WorkspaceIndexingService", "OpenConnectionAsync(2)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs", "GrimoireDatabaseBootstrapper", "CheckpointOnShutdownAsync(3)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.ShutdownGrimoire,
            GrimoireAcquisitionKind.BootstrapOrShutdown,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.ShutdownHeldLock, "GrimoireDatabaseBootstrapper.CheckpointOnShutdownAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs", "GrimoireDatabaseBootstrapper", "EnsureInitializedAsync(9)", AcquisitionConstructKind.ProviderOpen, "probe.OpenAsync", 1, "probe.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.PreReadinessGrimoire,
            GrimoireAcquisitionKind.BootstrapOrShutdown,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.PreReadinessHeldLock, "GrimoireDatabaseBootstrapper.EnsureInitializedAsync(9)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs", "GrimoireDatabaseBootstrapper", "EnsureInitializedAsync(9)", AcquisitionConstructKind.ProviderOpen, "installConnection.OpenAsync", 1, "installConnection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.PreReadinessGrimoire,
            GrimoireAcquisitionKind.BootstrapOrShutdown,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.PreReadinessHeldLock, "GrimoireDatabaseBootstrapper.EnsureInitializedAsync(9)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs", "GrimoireDatabaseBootstrapper", "RekeyToPbkdf2Async(5)", AcquisitionConstructKind.ProviderOpen, "rekeyConnection.OpenAsync", 1, "rekeyConnection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.PreReadinessGrimoire,
            GrimoireAcquisitionKind.BootstrapOrShutdown,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.PreReadinessHeldLock, "GrimoireDatabaseBootstrapper.RekeyToPbkdf2Async(5)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs", "GrimoireDatabaseBootstrapper", "CanOpenDatabaseAsync(3)", AcquisitionConstructKind.ProviderOpen, "probe.OpenAsync", 1, "probe.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.PreReadinessGrimoire,
            GrimoireAcquisitionKind.BootstrapOrShutdown,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.PreReadinessHeldLock, "GrimoireDatabaseBootstrapper.CanOpenDatabaseAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/EntryWeavingService.cs", "EntryWeavingService", "FetchUnembeddedEntriesAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 2, "OpenConnectionAsync(db,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/EntryWeavingService.cs", "EntryWeavingService", "UpsertEmbeddingAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 2, "OpenConnectionAsync(db,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Hosting/EntryWeavingService.cs", "EntryWeavingService", "OpenConnectionAsync(2)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs", "LexiconService", "UpsertCoreAsync(6)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs", "LexiconService", "DeleteByNameAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs", "LexiconService", "MatchEntitiesAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs", "LexiconService", "GetByNameAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs", "LexiconService", "GetByNameInScopeAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs", "LexiconService", "ListAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs", "LexiconService", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupDatabaseSnapshotter.cs", "BackupDatabaseSnapshotter", "CreateAsync(4)", AcquisitionConstructKind.ProviderOpen, "source.OpenAsync", 1, "source.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupDatabaseSnapshotter.cs", "BackupDatabaseSnapshotter", "CreateAsync(4)", AcquisitionConstructKind.ProviderOpen, "destination.OpenAsync", 1, "destination.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.ArchiveOrSnapshot,
            GrimoireAcquisitionKind.StagingOrArchive,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.TypedStagingOrSnapshot, "BackupDatabaseSnapshotter.CreateAsync(4)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupDatabaseSnapshotter.cs", "BackupDatabaseSnapshotter", "VerifySnapshotAsync(3)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.ArchiveOrSnapshot,
            GrimoireAcquisitionKind.StagingOrArchive,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.TypedStagingOrSnapshot, "BackupDatabaseSnapshotter.VerifySnapshotAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupArchiveCodec.cs", "BackupArchiveCodec", "VerifyDatabaseAsync(3)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.ArchiveOrSnapshot,
            GrimoireAcquisitionKind.StagingOrArchive,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.TypedStagingOrSnapshot, "BackupArchiveCodec.VerifyDatabaseAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupService.cs", "BackupService", "ReadSchemaVersionAsync(3)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupService.cs", "BackupService", "RemoveOperationFromSnapshotAsync(4)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.ArchiveOrSnapshot,
            GrimoireAcquisitionKind.StagingOrArchive,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.TypedStagingOrSnapshot, "BackupService.RemoveOperationFromSnapshotAsync(4)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalAnchorStore.cs", "BackupRestoreJournalAnchorStore", "OpenEnvelope(3)", AcquisitionConstructKind.ProviderOpen, "BackupRestoreJournalAuthenticator.Open", 4, "BackupRestoreJournalAuthenticator.Open(key.Value,profileNamespace.Digest,installationId,envelope)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "BackupRestoreJournalAnchorStore.OpenEnvelope(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs", "BackupRestoreService", "PrepareStagedGenerationAsync(12)", AcquisitionConstructKind.ProviderOpen, "BackupRestoreDatabaseWorker.OpenAsync", 4, "BackupRestoreDatabaseWorker.OpenAsync(stagedDatabase,grimoireSecret,readOnly:false,cancellationToken)"),
            GrimoirePathAuthority.RestoreOrCompactionStaging,
            GrimoireAcquisitionKind.StagingOrArchive,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.TypedStagingOrSnapshot, "BackupRestoreService.PrepareStagedGenerationAsync(12)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs", "BackupRestoreService", "ReconcileAsync(6)", AcquisitionConstructKind.ProviderOpen, "BackupRestoreDatabaseWorker.OpenAsync", 4, "BackupRestoreDatabaseWorker.OpenAsync(databasePath,grimoireSecret,readOnly:true,cancellationToken)"),
            GrimoirePathAuthority.RestoreOrCompactionStaging,
            GrimoireAcquisitionKind.StagingOrArchive,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.TypedStagingOrSnapshot, "BackupRestoreService.ReconcileAsync(6)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs", "BackupRestoreService", "ReadDestinationSchemaAsync(1)", AcquisitionConstructKind.ProviderOpen, "BackupRestoreDatabaseWorker.OpenAsync", 4, "BackupRestoreDatabaseWorker.OpenAsync(_paths.DatabasePath,secret.Value,readOnly:true,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs", "BackupRestoreService", "ReadDestinationCampaignIdsAsync(1)", AcquisitionConstructKind.ProviderOpen, "BackupRestoreDatabaseWorker.OpenAsync", 4, "BackupRestoreDatabaseWorker.OpenAsync(_paths.DatabasePath,secret.Value,readOnly:true,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs", "BackupRestoreService", "ReadDestinationCovenantStateAsync(1)", AcquisitionConstructKind.ProviderOpen, "BackupRestoreDatabaseWorker.OpenAsync", 4, "BackupRestoreDatabaseWorker.OpenAsync(_paths.DatabasePath,secret.Value,readOnly:true,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupInventoryPlanner.cs", "BackupInventoryPlanner", "AddDatabaseBackedFilesAsync(9)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreDatabaseWorker.cs", "BackupRestoreDatabaseWorker", "OpenAsync(4)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreProtectedStateInspector.cs", "BackupRestoreProtectedStateInspector", "InspectExtractedArchiveAsync(2)", AcquisitionConstructKind.ProviderOpen, "BackupRestoreDatabaseWorker.OpenAsync", 4, "BackupRestoreDatabaseWorker.OpenAsync(database,secret,readOnly:true,cancellationToken)"),
            GrimoirePathAuthority.ArchiveOrSnapshot,
            GrimoireAcquisitionKind.StagingOrArchive,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.TypedStagingOrSnapshot, "BackupRestoreProtectedStateInspector.InspectExtractedArchiveAsync(2)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs", "BackupSessionImporter", "ImportProtectedAsync(10)", AcquisitionConstructKind.ProviderOpen, "BackupRestoreDatabaseWorker.OpenAsync", 4, "BackupRestoreDatabaseWorker.OpenAsync(sourceDatabasePath,sourceGrimoireSecret,readOnly:true,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs", "BackupSessionImporter", "ImportProtectedAsync(10)", AcquisitionConstructKind.ProviderOpen, "BackupRestoreDatabaseWorker.OpenAsync", 4, "BackupRestoreDatabaseWorker.OpenAsync(destinationDatabasePath,destinationGrimoireSecret,readOnly:false,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs", "BackupSessionImporter", "ImportOneProtectedAsync(9)", AcquisitionConstructKind.ProviderOpen, "BackupRestoreDatabaseWorker.OpenAsync", 4, "BackupRestoreDatabaseWorker.OpenAsync(sourceDatabasePath,sourceGrimoireSecret,readOnly:true,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs", "BackupSessionImporter", "ImportAsync(9)", AcquisitionConstructKind.ProviderOpen, "BackupRestoreDatabaseWorker.OpenAsync", 4, "BackupRestoreDatabaseWorker.OpenAsync(sourceDatabasePath,sourceGrimoireSecret,readOnly:true,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs", "BackupSessionImporter", "ImportAsync(9)", AcquisitionConstructKind.ProviderOpen, "BackupRestoreDatabaseWorker.OpenAsync", 4, "BackupRestoreDatabaseWorker.OpenAsync(destinationDatabasePath,destinationGrimoireSecret,readOnly:false,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs", "SagaMemoryStore", "InsertCoreAsync(9)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs", "SagaMemoryStore", "CountAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs", "SagaMemoryStore", "CountBySessionAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs", "SagaMemoryStore", "ListAsync(6)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs", "SagaMemoryStore", "GetByIdsAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs", "SagaMemoryStore", "DeleteAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs", "SagaMemoryStore", "DeleteAllAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs", "SagaMemoryStore", "GetStatsAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs", "SagaMemoryStore", "GetWatermarkAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs", "SagaMemoryStore", "SetWatermarkAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs", "SagaMemoryStore", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContextOptionsConfigurator.cs", "ArcanumDbContextOptionsConfigurator", "ConfigureProvider(2)", AcquisitionConstructKind.UseSqlite, "optionsBuilder.UseSqlite", 1, "optionsBuilder.UseSqlite(connectionString)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs", "DataRetentionService", "ApplyFactoryResetAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SanctumBreachRepository.cs", "SanctumBreachRepository", "RecordAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(ct)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SanctumBreachRepository.cs", "SanctumBreachRepository", "QueryAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(ct)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SanctumBreachRepository.cs", "SanctumBreachRepository", "GetCountAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(ct)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SanctumBreachRepository.cs", "SanctumBreachRepository", "DeleteOldestAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(ct)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SanctumBreachRepository.cs", "SanctumBreachRepository", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs", "BudgetReservationService", "ReserveAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs", "BudgetReservationService", "AdjustAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs", "BudgetReservationService", "ReconcileAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs", "BudgetReservationService", "ReleaseAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs", "BudgetReservationService", "GetTodayCommittedSpendAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs", "BudgetReservationService", "GetTodayOutstandingReservationsAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs", "BudgetReservationService", "SweepExpiredAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs", "BudgetReservationService", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs", "IdempotencyClaimStore", "TryGetAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs", "IdempotencyClaimStore", "GetByIdAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs", "IdempotencyClaimStore", "TryCreateAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs", "IdempotencyClaimStore", "HeartbeatAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs", "IdempotencyClaimStore", "CompleteAsync(8)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs", "IdempotencyClaimStore", "TryReclaimAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs", "IdempotencyClaimStore", "LinkRunAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs", "IdempotencyClaimStore", "DeleteExpiredAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs", "IdempotencyClaimStore", "MarkTerminalAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs", "IdempotencyClaimStore", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "DeleteSessionAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "DeleteAttachmentAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "ApplyMemoryResetAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "ReadSessionSnapshotAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "ReadAttachmentSnapshotAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "PopulateAttachmentDerivedCountsAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "ReadContextPinBlockersAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "ReadSessionConflictsAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "ReadGlobalConflictsAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "ReadUploadedFilesByReferenceEligibilityAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "ReadUploadedFileAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "ReadBlockingBatchReferencesAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "ReadSessionIdsBeforeAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "ReadEligibleSessionIdsBeforeAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "CountTableAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "SumColumnAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "CountJoinedEntryEmbeddingsAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "CountJoinedEntryVectorEmbeddingsAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "CountAttachmentEmbeddingsAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "CountAttachmentVectorEmbeddingsAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "TableExistsAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "ExecuteStandaloneAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "MutationTargetExistsAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs", "SessionAttachmentStore", "DeleteRowsForSessionInAmbientTransactionAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs", "SessionAttachmentStore", "ClearEntryIdsInAmbientTransactionAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs", "SessionAttachmentStore", "ReadBoundForForkPageAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs", "SessionAttachmentStore", "SweepMissingSessionRowsAndDirectoriesAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs", "SessionAttachmentStore", "DeleteSweptRowAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs", "SessionAttachmentStore", "ListDistinctBoundSessionIdsAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs", "SessionAttachmentStore", "ListAllRowsAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContextFactory.cs", "ArcanumDbContextFactory", "CreateDbContext(1)", AcquisitionConstructKind.UseSqlite, "optionsBuilder.UseSqlite", 1, "optionsBuilder.UseSqlite(connectionString)"),
            GrimoirePathAuthority.DesignTimeScratch,
            GrimoireAcquisitionKind.DesignTimeOrNativeValidation,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.DesignTimeScratch, "ArcanumDbContextFactory.CreateDbContext(1)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetAlertRepository.cs", "BudgetAlertRepository", "RecordAlertAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetAlertRepository.cs", "BudgetAlertRepository", "HasAlertedTodayAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetAlertRepository.cs", "BudgetAlertRepository", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BlobEncryptionMetadataStore.cs", "BlobEncryptionMetadataStore", "ListAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BlobEncryptionMetadataStore.cs", "BlobEncryptionMetadataStore", "UpdateEncryptionMetadataAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BlobEncryptionMetadataStore.cs", "BlobEncryptionMetadataStore", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/UnseenServantWatermarkStore.cs", "UnseenServantWatermarkStore", "GetAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/UnseenServantWatermarkStore.cs", "UnseenServantWatermarkStore", "SaveAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/UnseenServantWatermarkStore.cs", "UnseenServantWatermarkStore", "GetAllAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/UnseenServantWatermarkStore.cs", "UnseenServantWatermarkStore", "DeleteAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/UnseenServantWatermarkStore.cs", "UnseenServantWatermarkStore", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionContextPinStore.cs", "SessionContextPinStore", "EnsureOpenAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.CovenantInventory.cs", "DataRetentionService", "ReadNonrevocableDisclosureExposureAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "PromotePendingAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "GetByIdAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "GetByLogicalAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "ReadBoundPageAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "ListLatestBoundAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "ReadLatestBoundByLogicalKeyPageAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "ReadIndexLatestAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "ReadIndexVersionPageAsync(6)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "DeleteStalePendingAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "InsertRowAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "UpdateSourceAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "FindLatestAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "SumByteLengthAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "ListPendingByTurnAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "ListStalePendingAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "ListStalePendingForTurnAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "RelativePathIsClaimedAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "ListAllRelativePathsAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs", "SessionAttachmentStore", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "_db.Database.OpenConnectionAsync", 1, "_db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyStore.cs", "IdempotencyStore", "TryGetAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyStore.cs", "IdempotencyStore", "SaveAsync(6)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyStore.cs", "IdempotencyStore", "DeleteExpiredAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyStore.cs", "IdempotencyStore", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/UploadedFileRepository.cs", "UploadedFileRepository", "CreateAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/UploadedFileRepository.cs", "UploadedFileRepository", "CreateForOwnedFileOnceAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/UploadedFileRepository.cs", "UploadedFileRepository", "GetByIdAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/UploadedFileRepository.cs", "UploadedFileRepository", "ListAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/UploadedFileRepository.cs", "UploadedFileRepository", "DeleteAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/UploadedFileRepository.cs", "UploadedFileRepository", "TryDeleteUnreferencedOnceAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/UploadedFileRepository.cs", "UploadedFileRepository", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "_db.Database.OpenConnectionAsync", 1, "_db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.WorkspaceReset.cs", "DataRetentionService", "BuildWorkspaceResetPlanAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.WorkspaceReset.cs", "DataRetentionService", "ApplyWorkspaceResetAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryScopeResolver.cs", "MemoryScopeResolver", "ReadBoundCampaignAsync(2)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "CreateAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "GetByIdAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "ListAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "ListPageAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "ListPendingPageAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "ListByStatusAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "UpdateStatusAsync(6)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "TryCompareAndSetStatusAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "ListLineCheckpointsAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "ListLineCheckpointsAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "TryBeginLineAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "TryRecordTerminalLineAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "CompleteLineAsync(6)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "DeleteLineCheckpointsAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs", "BatchRepository", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/AttachmentMemoryProvenanceStore.cs", "AttachmentMemoryProvenanceStore", "RecordConsultationsAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/AttachmentMemoryProvenanceStore.cs", "AttachmentMemoryProvenanceStore", "ListConsultationsAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/AttachmentMemoryProvenanceStore.cs", "AttachmentMemoryProvenanceStore", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs", "SqliteNativeRuntimeValidator", "ValidateAsync(2)", AcquisitionConstructKind.ProviderOpen, "Open", 2, "Open(scratch.Path_,key)"),
            GrimoirePathAuthority.NativeRuntimeValidation,
            GrimoireAcquisitionKind.DesignTimeOrNativeValidation,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NativeRuntimeValidation, "SqliteNativeRuntimeValidator.ValidateAsync(2)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs", "SqliteNativeRuntimeValidator", "ValidateAsync(2)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.NativeRuntimeValidation,
            GrimoireAcquisitionKind.DesignTimeOrNativeValidation,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NativeRuntimeValidation, "SqliteNativeRuntimeValidator.ValidateAsync(2)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs", "SqliteNativeRuntimeValidator", "CodecRoundTripsAsync(3)", AcquisitionConstructKind.ProviderOpen, "Open", 2, "Open(path,key)"),
            GrimoirePathAuthority.NativeRuntimeValidation,
            GrimoireAcquisitionKind.DesignTimeOrNativeValidation,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NativeRuntimeValidation, "SqliteNativeRuntimeValidator.CodecRoundTripsAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs", "SqliteNativeRuntimeValidator", "CodecRoundTripsAsync(3)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.NativeRuntimeValidation,
            GrimoireAcquisitionKind.DesignTimeOrNativeValidation,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NativeRuntimeValidation, "SqliteNativeRuntimeValidator.CodecRoundTripsAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs", "SqliteNativeRuntimeValidator", "CipherIntegrityPassesAsync(3)", AcquisitionConstructKind.ProviderOpen, "Open", 2, "Open(path,key)"),
            GrimoirePathAuthority.NativeRuntimeValidation,
            GrimoireAcquisitionKind.DesignTimeOrNativeValidation,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NativeRuntimeValidation, "SqliteNativeRuntimeValidator.CipherIntegrityPassesAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs", "SqliteNativeRuntimeValidator", "CipherIntegrityPassesAsync(3)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.NativeRuntimeValidation,
            GrimoireAcquisitionKind.DesignTimeOrNativeValidation,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NativeRuntimeValidation, "SqliteNativeRuntimeValidator.CipherIntegrityPassesAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs", "SqliteNativeRuntimeValidator", "WrongKeyIsRejectedAsync(3)", AcquisitionConstructKind.ProviderOpen, "Open", 2, "Open(path,wrongKey)"),
            GrimoirePathAuthority.NativeRuntimeValidation,
            GrimoireAcquisitionKind.DesignTimeOrNativeValidation,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NativeRuntimeValidation, "SqliteNativeRuntimeValidator.WrongKeyIsRejectedAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs", "SqliteNativeRuntimeValidator", "WrongKeyIsRejectedAsync(3)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.NativeRuntimeValidation,
            GrimoireAcquisitionKind.DesignTimeOrNativeValidation,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NativeRuntimeValidation, "SqliteNativeRuntimeValidator.WrongKeyIsRejectedAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs", "SqliteNativeRuntimeValidator", "FtsSecureDeletePassesAsync(3)", AcquisitionConstructKind.ProviderOpen, "Open", 2, "Open(path,key)"),
            GrimoirePathAuthority.NativeRuntimeValidation,
            GrimoireAcquisitionKind.DesignTimeOrNativeValidation,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NativeRuntimeValidation, "SqliteNativeRuntimeValidator.FtsSecureDeletePassesAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs", "SqliteNativeRuntimeValidator", "FtsSecureDeletePassesAsync(3)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.NativeRuntimeValidation,
            GrimoireAcquisitionKind.DesignTimeOrNativeValidation,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NativeRuntimeValidation, "SqliteNativeRuntimeValidator.FtsSecureDeletePassesAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs", "SqliteNativeRuntimeValidator", "LoadExtensionIsBlockedAsync(3)", AcquisitionConstructKind.ProviderOpen, "Open", 2, "Open(path,key)"),
            GrimoirePathAuthority.NativeRuntimeValidation,
            GrimoireAcquisitionKind.DesignTimeOrNativeValidation,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NativeRuntimeValidation, "SqliteNativeRuntimeValidator.LoadExtensionIsBlockedAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs", "SqliteNativeRuntimeValidator", "LoadExtensionIsBlockedAsync(3)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.NativeRuntimeValidation,
            GrimoireAcquisitionKind.DesignTimeOrNativeValidation,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NativeRuntimeValidation, "SqliteNativeRuntimeValidator.LoadExtensionIsBlockedAsync(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs", "SagaMemoryStore", "ReadCurationRowAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs", "SagaMemoryStore", "RetireAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs", "SagaMemoryStore", "ReinstateAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs", "SagaMemoryStore", "CorrectAsync(6)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs", "SagaMemoryStore", "SetPinAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/TurnRunWriter.cs", "TurnRunWriter", "StartRunAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/TurnRunWriter.cs", "TurnRunWriter", "CompleteRunAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/TurnRunWriter.cs", "TurnRunWriter", "TryAbandonRunAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/TurnRunWriter.cs", "TurnRunWriter", "RecordBillableOperationAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/TurnRunWriter.cs", "TurnRunWriter", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "CreateAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "ResolveOrCreateAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "TryStartSingleFlightAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "GetAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "FindRequestIdentityAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "FindByRequestedOperationIdAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "ListAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "FindExpiredAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "TryAcquireLeaseAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "RenewLeaseAsync(5)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenFreshAsync", 2, "_connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.IsolatedHeartbeat,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "GetCountsAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "ExecuteUpdateAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs", "LongRunningOperationStore", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "AddBatchFileStatusAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "ReadConflictsAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "AddCompletedBatchCandidatesAsync(8)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "AddEntryProtectionDiagnosticsAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "AddAttachmentProtectionDiagnosticsAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "AddEntryEmbeddingProtectionDiagnosticsAsync(6)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "AddBoundedSessionConflictDiagnosticsAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "AddEntryCandidatesAsync(8)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "AddAttachmentCandidatesAsync(8)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "AddEntryEmbeddingCandidatesAsync(8)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "AddWorkspaceCandidatesAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "AddIdempotencyCandidatesAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "AddAccountingCandidatesAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "ReadStringIdsAsync(7)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "ReadCandidateAgeBoundaryAsync(4)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "CandidateStillMeetsFrozenAgeAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "IsSqlCandidateOldEnoughAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "DeleteBatchCandidateAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "DeleteUploadedFileCandidateAsync(5)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "DeleteEntryCandidateAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "DeleteEntryEmbeddingCandidateAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "DeleteWorkspaceCandidateAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "DeleteSagaCandidateAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "DeleteLexiconCandidateAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DataRetentionService", "DeleteAccountingCandidateAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs", "ServiceCollectionExtensions", "AddArcanumGrimoireForCli(1)", AcquisitionConstructKind.AddDbContext, "services.AddDbContext<ArcanumDbContext>", 1, "services.AddDbContext<ArcanumDbContext>((sp,options)=>ArcanumDbContextOptionsConfigurator.Configure(options,sp.GetRequiredService<IGrimoireDbPassphraseSource>(),sp.GetRequiredService<IGrimoireOrdinaryConnectionLifecycle>(),sp.GetRequiredService<ICovenantConnectionDrain>(),sp.GetRequiredService<ICovenantSqliteConnectionInitializer>()))"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingEfOrdinary,
            GrimoireRuntimeAdmissionRoute.SharedEfInterceptor,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs", "ServiceCollectionExtensions", "AddArcanumInfrastructure(2)", AcquisitionConstructKind.AddDbContext, "services.AddDbContextPool<ArcanumDbContext>", 2, "services.AddDbContextPool<ArcanumDbContext>((sp,options)=>ArcanumDbContextOptionsConfigurator.Configure(options,sp.GetRequiredService<IGrimoireDbPassphraseSource>(),sp.GetRequiredService<IGrimoireOrdinaryConnectionLifecycle>(),sp.GetRequiredService<ICovenantConnectionDrain>(),sp.GetRequiredService<ICovenantSqliteConnectionInitializer>()),poolSize:32)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingEfOrdinary,
            GrimoireRuntimeAdmissionRoute.SharedEfInterceptor,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalStore.cs", "GrimoireOfflineTransitionJournalStore", "AdvanceAsync(4)", AcquisitionConstructKind.ProviderOpen, "Open", 2, "Open(current.Location,current.Envelope)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalStore.AdvanceAsync(4)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalStore.cs", "GrimoireOfflineTransitionJournalStore", "AuthenticateEvidence(3)", AcquisitionConstructKind.ProviderOpen, "Open", 2, "Open(location,decoded.Value)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalStore.AuthenticateEvidence(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalStore.cs", "GrimoireOfflineTransitionJournalStore", "IsExactPredecessor(3)", AcquisitionConstructKind.ProviderOpen, "Open", 2, "Open(location,decoded.Value)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalStore.IsExactPredecessor(3)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalStore.cs", "GrimoireOfflineTransitionJournalStore", "ResumeExactCurrentAsync(7)", AcquisitionConstructKind.ProviderOpen, "Open", 2, "Open(location,envelope)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalStore.ResumeExactCurrentAsync(7)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalStore.cs", "GrimoireOfflineTransitionJournalStore", "AuthenticatePublishedAsync(6)", AcquisitionConstructKind.ProviderOpen, "Open", 2, "Open(location,decoded.Value)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalStore.AuthenticatePublishedAsync(6)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalStore.cs", "GrimoireOfflineTransitionJournalStore", "Open(2)", AcquisitionConstructKind.ProviderOpen, "GrimoireOfflineTransitionJournalAuthenticator.Open", 5, "GrimoireOfflineTransitionJournalAuthenticator.Open(opening,location.ProfileNamespace.Digest,envelope.InstallationId,location.JournalLocationDigest,envelope)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalStore.Open(2)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs", "GrimoireOfflineTransitionJournalFileStore", "ResolveLocation(1)", AcquisitionConstructKind.ProviderOpen, "GrimoireOfflineTransitionJournalFilePrimitives.Open", 1, "GrimoireOfflineTransitionJournalFilePrimitives.Open(parent)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalFileStore.ResolveLocation(1)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs", "GrimoireOfflineTransitionJournalFileStore", "InspectEvidenceAsync(2)", AcquisitionConstructKind.ProviderOpen, "Open", 1, "Open(location)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalFileStore.InspectEvidenceAsync(2)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs", "GrimoireOfflineTransitionJournalFileStore", "ReadIfPresentAsync(2)", AcquisitionConstructKind.ProviderOpen, "Open", 1, "Open(location)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalFileStore.ReadIfPresentAsync(2)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs", "GrimoireOfflineTransitionJournalFileStore", "ReplaceDurablyAsync(5)", AcquisitionConstructKind.ProviderOpen, "Open", 1, "Open(location)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalFileStore.ReplaceDurablyAsync(5)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs", "GrimoireOfflineTransitionJournalFileStore", "DeleteDurably(4)", AcquisitionConstructKind.ProviderOpen, "Open", 1, "Open(location)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalFileStore.DeleteDurably(4)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs", "GrimoireOfflineTransitionJournalFileStore", "ProveAbsentDurably(2)", AcquisitionConstructKind.ProviderOpen, "Open", 1, "Open(location)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalFileStore.ProveAbsentDurably(2)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs", "GrimoireOfflineTransitionJournalFileStore", "NormalizeWorkingPredecessorAsync(7)", AcquisitionConstructKind.ProviderOpen, "Open", 1, "Open(location)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalFileStore.NormalizeWorkingPredecessorAsync(7)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs", "GrimoireOfflineTransitionJournalFileStore", "CompleteRetirementAsync(7)", AcquisitionConstructKind.ProviderOpen, "Open", 1, "Open(location)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalFileStore.CompleteRetirementAsync(7)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs", "GrimoireOfflineTransitionJournalFileStore", "ResumeWorkingPublicationAsync(7)", AcquisitionConstructKind.ProviderOpen, "Open", 1, "Open(location)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalFileStore.ResumeWorkingPublicationAsync(7)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs", "GrimoireOfflineTransitionJournalFileStore", "OpenProductionPrimitives(1)", AcquisitionConstructKind.ProviderOpen, "GrimoireOfflineTransitionJournalFilePrimitives.Open", 2, "GrimoireOfflineTransitionJournalFilePrimitives.Open(parent,location.GuardedParentPhysicalIdentityDigest)"),
            GrimoirePathAuthority.NotGrimoire,
            GrimoireAcquisitionKind.NonGrimoireCandidate,
            GrimoireRuntimeAdmissionRoute.ExactNonServingProof,
            new(ExactNonServingProofKind.NegativeNonDatabaseProof, "GrimoireOfflineTransitionJournalFileStore.OpenProductionPrimitives(1)", 0)),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsStore.cs", "AnnalsStore", "GetClaimAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsStore.cs", "AnnalsStore", "GetVersionsAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsStore.cs", "AnnalsStore", "GetDependenciesAsync(2)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsStore.cs", "AnnalsStore", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireOrdinaryConnectionFactory.cs", "GrimoireOrdinaryConnectionFactory", "AcquireScopedAsync(3)", AcquisitionConstructKind.MarkedRouteDeclaration, "AcquireScopedAsync", 3, "AcquireScopedAsync(3)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireOrdinaryConnectionFactory.cs", "GrimoireOrdinaryConnectionFactory", "AcquireScopedAsync(3)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireOrdinaryConnectionFactory.cs", "GrimoireOrdinaryConnectionFactory", "OpenFreshAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenFreshAsync", 2, "OpenFreshAsync(2)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireOrdinaryConnectionFactory.cs", "GrimoireOrdinaryConnectionFactory", "OpenFreshAsync(2)", AcquisitionConstructKind.ProviderObjectCreation, "SqliteConnection", 1, "newSqliteConnection(builder.ToString())"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireOrdinaryConnectionFactory.cs", "GrimoireOrdinaryConnectionFactory", "OpenFreshAsync(2)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireMaintenanceConnectionFactory.cs", "GrimoireMaintenanceConnectionFactory", "OpenJournalCanonicalErasureAsync(3)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenJournalCanonicalErasureAsync", 3, "OpenJournalCanonicalErasureAsync(3)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.JournalMaintenance,
            GrimoireRuntimeAdmissionRoute.MaintenanceConnectionFactory,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireMaintenanceConnectionFactory.cs", "GrimoireMaintenanceConnectionFactory", "OpenJournalCanonicalErasureAsync(3)", AcquisitionConstructKind.ProviderObjectCreation, "SqliteConnection", 1, "newSqliteConnection(builder.ToString())"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.JournalMaintenance,
            GrimoireRuntimeAdmissionRoute.MaintenanceConnectionFactory,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireMaintenanceConnectionFactory.cs", "GrimoireMaintenanceConnectionFactory", "OpenJournalCanonicalErasureAsync(3)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.JournalMaintenance,
            GrimoireRuntimeAdmissionRoute.MaintenanceConnectionFactory,
            null),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureWriter.cs", "CovenantDisclosureWriter", "OpenVerifiedAsync(3)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenFreshAsync", 2, "_connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureInventorySource.cs", "CovenantErasureInventorySource", "WithOwnedSnapshotAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenFreshAsync", 2, "_connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantHealthyCatalogErasureGuard.cs", "CovenantHealthyCatalogErasureGuard", "RequireHealthyAsync(1)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenFreshAsync", 2, "_connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        V3(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCanonicalErasureTransaction.cs", "CovenantCanonicalErasureTransaction", "ApplyAsync(3)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3CanonicalErasureAsync", 2, "_connections.OpenV3CanonicalErasureAsync(capability,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            "CovenantCanonicalErasureTransaction.ApplyAsync(3)"),
        
        new(
            new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ICovenantConnectionSource.cs", "CovenantConnectionSource", "GetOpenCoreConnectionAsync(1)", AcquisitionConstructKind.MarkedRouteInvocation, "AcquireScopedAsync", 3, "_connections.AcquireScopedAsync(connection,CovenantSqliteConnectionMode.ReadWrite,cancellationToken)"),
            GrimoirePathAuthority.LiveGrimoire,
            GrimoireAcquisitionKind.ServingRawOrdinary,
            GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
            null),
        
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3CanonicalErasureAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenV3CanonicalErasureAsync", 2, "OpenV3CanonicalErasureAsync(2)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3CanonicalErasureAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3WalTruncationAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenV3WalTruncationAsync", 2, "OpenV3WalTruncationAsync(2)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3WalTruncationAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3VacuumAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenV3VacuumAsync", 2, "OpenV3VacuumAsync(2)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3VacuumAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3ExportSourceAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenV3ExportSourceAsync", 2, "OpenV3ExportSourceAsync(2)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3ExportSourceAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3ExportVerificationAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenV3ExportVerificationAsync", 2, "OpenV3ExportVerificationAsync(2)"), GrimoirePathAuthority.RestoreOrCompactionStaging, "CovenantV3MaintenanceConnectionFactory.OpenV3ExportVerificationAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3PostReplaceJournalRestoreAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenV3PostReplaceJournalRestoreAsync", 2, "OpenV3PostReplaceJournalRestoreAsync(2)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3PostReplaceJournalRestoreAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3AcceleratorInitializationAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenV3AcceleratorInitializationAsync", 2, "OpenV3AcceleratorInitializationAsync(2)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3AcceleratorInitializationAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3CandidateReopenVerificationAsync(2)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenV3CandidateReopenVerificationAsync", 2, "OpenV3CandidateReopenVerificationAsync(2)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3CandidateReopenVerificationAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3LeaseAsync(5)", AcquisitionConstructKind.MarkedRouteDeclaration, "OpenV3LeaseAsync", 5, "OpenV3LeaseAsync(5)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3LeaseAsync(5)"),

        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3CanonicalErasureAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3LeaseAsync", 5, "OpenV3LeaseAsync(capability,CovenantV3MaintenancePurpose.CanonicalErasure,DatabaseBuilder,CovenantSqliteConnectionMode.ExclusiveMaintenance,cancellationToken)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3CanonicalErasureAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3WalTruncationAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3LeaseAsync", 5, "OpenV3LeaseAsync(capability,CovenantV3MaintenancePurpose.WalTruncation,DatabaseBuilder,CovenantSqliteConnectionMode.ExclusiveMaintenance,cancellationToken)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3WalTruncationAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3VacuumAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3LeaseAsync", 5, "OpenV3LeaseAsync(capability,CovenantV3MaintenancePurpose.CompactionVacuum,DatabaseBuilder,CovenantSqliteConnectionMode.ExclusiveMaintenance,cancellationToken)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3VacuumAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3ExportSourceAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3LeaseAsync", 5, "OpenV3LeaseAsync(capability,CovenantV3MaintenancePurpose.CompactionExport,DatabaseBuilder,CovenantSqliteConnectionMode.ExclusiveMaintenance,cancellationToken)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3ExportSourceAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3ExportVerificationAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3LeaseAsync", 5, "OpenV3LeaseAsync(capability,CovenantV3MaintenancePurpose.CompactionExportVerification,StagingBuilder,CovenantSqliteConnectionMode.ReadOnly,cancellationToken)"), GrimoirePathAuthority.RestoreOrCompactionStaging, "CovenantV3MaintenanceConnectionFactory.OpenV3ExportVerificationAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3PostReplaceJournalRestoreAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3LeaseAsync", 5, "OpenV3LeaseAsync(capability,CovenantV3MaintenancePurpose.CompactionPostReplaceJournalRestore,DatabaseBuilder,CovenantSqliteConnectionMode.ReadWrite,cancellationToken)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3PostReplaceJournalRestoreAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3AcceleratorInitializationAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3LeaseAsync", 5, "OpenV3LeaseAsync(capability,CovenantV3MaintenancePurpose.AcceleratorInitialization,DatabaseBuilder,CovenantSqliteConnectionMode.ExclusiveMaintenance,cancellationToken)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3AcceleratorInitializationAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3CandidateReopenVerificationAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3LeaseAsync", 5, "OpenV3LeaseAsync(capability,CovenantV3MaintenancePurpose.CandidateReopenVerification,ImmutableReadOnlyBuilder,CovenantSqliteConnectionMode.ReadOnly,cancellationToken)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3CandidateReopenVerificationAsync(2)"),

        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3LeaseAsync(5)", AcquisitionConstructKind.ProviderOpen, "connection.OpenAsync", 1, "connection.OpenAsync(cancellationToken)"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3LeaseAsync(5)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs", "CovenantV3MaintenanceConnectionFactory", "OpenV3LeaseAsync(5)", AcquisitionConstructKind.ProviderObjectCreation, "SqliteConnection", 1, "newSqliteConnection(builder().ToString())"), GrimoirePathAuthority.LiveGrimoire, "CovenantV3MaintenanceConnectionFactory.OpenV3LeaseAsync(5)"),

        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs", "CovenantLocalErasureStorageHealth", "ExportAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3ExportSourceAsync", 2, "_connections.OpenV3ExportSourceAsync(capability,cancellationToken)"), GrimoirePathAuthority.LiveGrimoire, "CovenantLocalErasureStorageHealth.ExportAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs", "CovenantLocalErasureStorageHealth", "VerifyExportAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3ExportVerificationAsync", 2, "_connections.OpenV3ExportVerificationAsync(capability,cancellationToken)"), GrimoirePathAuthority.RestoreOrCompactionStaging, "CovenantLocalErasureStorageHealth.VerifyExportAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs", "CovenantLocalErasureStorageHealth", "ReadAndVerifyCandidateAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3CandidateReopenVerificationAsync", 2, "_connections.OpenV3CandidateReopenVerificationAsync(capability,cancellationToken)"), GrimoirePathAuthority.LiveGrimoire, "CovenantLocalErasureStorageHealth.ReadAndVerifyCandidateAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs", "CovenantLocalErasureStorageHealth", "TruncateWalAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3WalTruncationAsync", 2, "_connections.OpenV3WalTruncationAsync(proof,token)"), GrimoirePathAuthority.LiveGrimoire, "CovenantLocalErasureStorageHealth.TruncateWalAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs", "CovenantLocalErasureStorageHealth", "InitializeAcceleratorAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3AcceleratorInitializationAsync", 2, "_connections.OpenV3AcceleratorInitializationAsync(proof,token)"), GrimoirePathAuthority.LiveGrimoire, "CovenantLocalErasureStorageHealth.InitializeAcceleratorAsync(2)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs", "CovenantLocalErasureStorageHealth", "ReplaceAsync(3)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3PostReplaceJournalRestoreAsync", 2, "_connections.OpenV3PostReplaceJournalRestoreAsync(proof,token)"), GrimoirePathAuthority.LiveGrimoire, "CovenantLocalErasureStorageHealth.ReplaceAsync(3)"),
        V3(new("src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs", "CovenantLocalErasureStorageHealth", "VacuumAsync(2)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenV3VacuumAsync", 2, "_connections.OpenV3VacuumAsync(proof,token)"), GrimoirePathAuthority.LiveGrimoire, "CovenantLocalErasureStorageHealth.VacuumAsync(2)")
        ]);

    private static IReadOnlyList<GrimoireAcquisitionCatalogEntry> BindProofEvidence(
        IReadOnlyList<GrimoireAcquisitionCatalogEntry> catalog) =>
        [
            .. catalog.Select(static entry => entry.NonServingProof is { } proof
                ? entry with
                {
                    NonServingProof = proof with
                    {
                        EvidenceMember = ExactProofEvidenceMember(entry.Identity),
                    },
                }
                : entry),
        ];

    private static GrimoireAcquisitionCatalogEntry Stopped(
        AcquisitionIdentity identity,
        string _) =>
        new(
            identity,
            GrimoirePathAuthority.StoppedHostGrimoire,
            GrimoireAcquisitionKind.StoppedHostRecovery,
            GrimoireRuntimeAdmissionRoute.StoppedHostConnectionFactory,
            new(ExactNonServingProofKind.StoppedHostAuthority, ExactProofEvidenceMember(identity)));

    private static GrimoireAcquisitionCatalogEntry V3(
        AcquisitionIdentity identity,
        GrimoirePathAuthority pathAuthority,
        string _) =>
        new(
            identity,
            pathAuthority,
            GrimoireAcquisitionKind.LegacyV3Maintenance,
            GrimoireRuntimeAdmissionRoute.MaintenanceConnectionFactory,
            new(ExactNonServingProofKind.LegacyV3ExclusiveLease, ExactProofEvidenceMember(identity), 248));

    private static void ValidateMethodMarker(
        AcquisitionSource source,
        MethodDeclarationSyntax method,
        ICollection<InventoryFailure> failures,
        ICollection<(string Name, AcquisitionIdentity Identity)> markedRoutes)
    {

        if (!IsConcrete(method))
        {

            return;

        }

        AcquisitionIdentity identity = MarkedRouteIdentity(
            source.RelativePath,
            method,
            method.Identifier.ValueText,
            method.ParameterList.Parameters.Count);

        if (IsMarked(method))
        {

            markedRoutes.Add((method.Identifier.ValueText, identity));

        }

        if (ReturnsOpaqueAcquisitionRouteType(method.ReturnType)
            && !IsFailureOnlyHelper(method)
            && !IsMarked(method))
        {

            failures.Add(new(
                InventoryFailureCode.MissingRequiredRouteMarker,
                identity,
                "A concrete opaque acquisition route requires GrimoireConnectionAcquisitionRoute."));

        }

    }

    private static void ValidateLocalFunctionMarker(
        AcquisitionSource source,
        LocalFunctionStatementSyntax localFunction,
        ICollection<InventoryFailure> failures,
        ICollection<(string Name, AcquisitionIdentity Identity)> markedRoutes)
    {

        if (!IsConcrete(localFunction))
        {

            return;

        }

        AcquisitionIdentity identity = MarkedRouteIdentity(
            source.RelativePath,
            localFunction,
            localFunction.Identifier.ValueText,
            localFunction.ParameterList.Parameters.Count);

        if (IsMarked(localFunction))
        {

            markedRoutes.Add((localFunction.Identifier.ValueText, identity));

        }

        if (ReturnsOpaqueAcquisitionRouteType(localFunction.ReturnType)
            && !IsFailureOnlyHelper(localFunction)
            && !IsMarked(localFunction))
        {

            failures.Add(new(
                InventoryFailureCode.MissingRequiredRouteMarker,
                identity,
                "A concrete opaque acquisition route requires GrimoireConnectionAcquisitionRoute."));

        }

    }

    private static bool ReturnsOpaqueAcquisitionRouteType(TypeSyntax type)
    {

        string terminalName = TerminalName(type);

        if (OpaqueAcquisitionRouteReturnNames.Contains(terminalName))
        {

            return true;

        }

        if (type is NullableTypeSyntax nullable)
        {

            return ReturnsOpaqueAcquisitionRouteType(nullable.ElementType);

        }

        if (type is GenericNameSyntax generic
            && RecursiveReturnWrappers.Contains(generic.Identifier.ValueText))
        {

            return generic.TypeArgumentList.Arguments.Any(ReturnsOpaqueAcquisitionRouteType);

        }

        if (type is QualifiedNameSyntax qualified)
        {

            return ReturnsOpaqueAcquisitionRouteType(qualified.Right);

        }

        if (type is AliasQualifiedNameSyntax aliasQualified)
        {

            return ReturnsOpaqueAcquisitionRouteType(aliasQualified.Name);

        }

        return false;

    }

    private static bool IsFailureOnlyHelper(MethodDeclarationSyntax method) =>
        IsFailureOnlyHelper(
            method.Body,
            method.ExpressionBody,
            method.Parent as TypeDeclarationSyntax);

    private static bool IsFailureOnlyHelper(LocalFunctionStatementSyntax localFunction) =>
        IsFailureOnlyHelper(
            localFunction.Body,
            localFunction.ExpressionBody,
            localFunction.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault());

    private static bool IsFailureOnlyHelper(
        BlockSyntax? body,
        ArrowExpressionClauseSyntax? expressionBody,
        TypeDeclarationSyntax? containingType)
    {

        if (expressionBody is not null)
        {

            return IsFailureOnlyResult(expressionBody.Expression, containingType);

        }

        if (body is null)
        {

            return false;

        }

        IReadOnlyList<ReturnStatementSyntax> returns =
            body.DescendantNodes().OfType<ReturnStatementSyntax>().ToArray();

        return returns.Count != 0
            && returns.All(statement => statement.Expression is { } expression
                && IsFailureOnlyResult(expression, containingType));

    }

    private static bool IsFailureOnlyResult(
        ExpressionSyntax expression,
        TypeDeclarationSyntax? containingType)
    {

        if (ContainsFailureResult(expression))
        {

            return true;

        }

        if (containingType is null || expression is not InvocationExpressionSyntax invocation)
        {

            return false;

        }

        string name = TerminalName(invocation.Expression);

        int arity = invocation.ArgumentList.Arguments.Count;

        return containingType.Members
            .OfType<MethodDeclarationSyntax>()
            .Any(candidate => candidate.Identifier.ValueText == name
                && candidate.ParameterList.Parameters.Count == arity
                && IsConcrete(candidate)
                && ContainsOnlyDirectFailureResult(candidate));

    }

    private static bool ContainsOnlyDirectFailureResult(MethodDeclarationSyntax method)
    {

        if (method.ExpressionBody is not null)
        {

            return ContainsFailureResult(method.ExpressionBody.Expression);

        }

        IReadOnlyList<ReturnStatementSyntax> returns =
            method.Body?.DescendantNodes().OfType<ReturnStatementSyntax>().ToArray() ?? [];

        return returns.Count != 0
            && returns.All(static statement => statement.Expression is { } expression
                && ContainsFailureResult(expression));

    }

    private static bool ContainsFailureResult(ExpressionSyntax expression) =>
        expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(invocation =>
            TerminalName(invocation.Expression) == "Failure");

    private static bool IsConcrete(MethodDeclarationSyntax method) =>
        method.Body is not null || method.ExpressionBody is not null;

    private static bool IsConcrete(LocalFunctionStatementSyntax localFunction) =>
        localFunction.Body is not null || localFunction.ExpressionBody is not null;

    private static bool IsMarked(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(static list => list.Attributes).Any(IsRouteAttribute);

    private static bool IsMarked(LocalFunctionStatementSyntax localFunction) =>
        localFunction.AttributeLists.SelectMany(static list => list.Attributes).Any(IsRouteAttribute);

    private static bool HasBroadIdentity(AcquisitionIdentity identity) =>
        identity.RelativePath.Contains('*', StringComparison.Ordinal)
        || identity.EnclosingType.Contains('*', StringComparison.Ordinal)
        || identity.EnclosingMember.Contains('*', StringComparison.Ordinal)
        || identity.CalleeOrConstructedType.Contains('*', StringComparison.Ordinal)
        || identity.Fingerprint.Contains('*', StringComparison.Ordinal);

    private static bool HasExactProofEvidence(GrimoireAcquisitionCatalogEntry entry) =>
        entry.NonServingProof?.EvidenceMember == ExactProofEvidenceMember(entry.Identity);

    private static string ExactProofEvidenceMember(AcquisitionIdentity identity) =>
        string.Join(
            '|',
            identity.RelativePath,
            identity.EnclosingType,
            identity.EnclosingMember,
            identity.ConstructKind.ToString(),
            identity.CalleeOrConstructedType,
            identity.Arity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            identity.Fingerprint);

    private static bool IsRouteAttribute(AttributeSyntax attribute) =>
        TerminalName(attribute.Name) == "GrimoireConnectionAcquisitionRoute";

    private static bool IsValidClassification(GrimoireAcquisitionCatalogEntry entry) =>
        entry.PathAuthority switch
        {
            GrimoirePathAuthority.LiveGrimoire => entry.AcquisitionKind switch
            {
                GrimoireAcquisitionKind.ServingEfOrdinary =>
                    entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.SharedEfInterceptor
                    && entry.NonServingProof is null,

                GrimoireAcquisitionKind.ServingRawOrdinary =>
                    entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory
                    && entry.NonServingProof is null,

                GrimoireAcquisitionKind.JournalMaintenance =>
                    entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.MaintenanceConnectionFactory
                    && entry.NonServingProof is null,

                GrimoireAcquisitionKind.LegacyV3Maintenance =>
                    entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.MaintenanceConnectionFactory
                    && HasProof(entry, ExactNonServingProofKind.LegacyV3ExclusiveLease, 248),

                _ => false,
            },

            GrimoirePathAuthority.StoppedHostGrimoire =>
                entry.AcquisitionKind == GrimoireAcquisitionKind.StoppedHostRecovery
                && entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.StoppedHostConnectionFactory
                && HasProof(entry, ExactNonServingProofKind.StoppedHostAuthority),

            GrimoirePathAuthority.PreReadinessGrimoire =>
                entry.AcquisitionKind == GrimoireAcquisitionKind.BootstrapOrShutdown
                && entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.ExactNonServingProof
                && HasProof(entry, ExactNonServingProofKind.PreReadinessHeldLock),

            GrimoirePathAuthority.ShutdownGrimoire =>
                entry.AcquisitionKind == GrimoireAcquisitionKind.BootstrapOrShutdown
                && entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.ExactNonServingProof
                && HasProof(entry, ExactNonServingProofKind.ShutdownHeldLock),

            GrimoirePathAuthority.ArchiveOrSnapshot =>
                entry.AcquisitionKind == GrimoireAcquisitionKind.StagingOrArchive
                && entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.ExactNonServingProof
                && HasProof(entry, ExactNonServingProofKind.TypedStagingOrSnapshot),

            GrimoirePathAuthority.RestoreOrCompactionStaging =>
                (entry.AcquisitionKind == GrimoireAcquisitionKind.StagingOrArchive
                    && entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.ExactNonServingProof
                    && HasProof(entry, ExactNonServingProofKind.TypedStagingOrSnapshot))
                || (entry.AcquisitionKind == GrimoireAcquisitionKind.LegacyV3Maintenance
                    && entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.MaintenanceConnectionFactory
                    && HasProof(entry, ExactNonServingProofKind.LegacyV3ExclusiveLease, 248)),

            GrimoirePathAuthority.DesignTimeScratch =>
                entry.AcquisitionKind == GrimoireAcquisitionKind.DesignTimeOrNativeValidation
                && entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.ExactNonServingProof
                && HasProof(entry, ExactNonServingProofKind.DesignTimeScratch),

            GrimoirePathAuthority.NativeRuntimeValidation =>
                entry.AcquisitionKind == GrimoireAcquisitionKind.DesignTimeOrNativeValidation
                && entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.ExactNonServingProof
                && HasProof(entry, ExactNonServingProofKind.NativeRuntimeValidation),

            GrimoirePathAuthority.NotGrimoire =>
                entry.AcquisitionKind == GrimoireAcquisitionKind.NonGrimoireCandidate
                && entry.RuntimeRoute == GrimoireRuntimeAdmissionRoute.ExactNonServingProof
                && HasProof(entry, ExactNonServingProofKind.NegativeNonDatabaseProof),

            _ => false,
        };

    private static bool HasProof(
        GrimoireAcquisitionCatalogEntry entry,
        ExactNonServingProofKind kind,
        int removalIssue = 0) =>
        entry.NonServingProof is { } proof
        && proof.Kind == kind
        && proof.RemovalIssue == removalIssue;

    private static AcquisitionIdentity Identity(
        string relativePath,
        SyntaxNode node,
        AcquisitionConstructKind constructKind,
        string calleeOrConstructedType,
        int arity) =>
        new(
            relativePath.Replace('\\', '/'),
            EnclosingType(node),
            EnclosingMember(node),
            constructKind,
            calleeOrConstructedType,
            arity,
            Tokens(node));

    private static AcquisitionIdentity MarkedRouteIdentity(
        string relativePath,
        SyntaxNode node,
        string methodName,
        int arity) =>
        new(
            relativePath.Replace('\\', '/'),
            EnclosingType(node),
            EnclosingMember(node),
            AcquisitionConstructKind.MarkedRouteDeclaration,
            methodName,
            arity,
            $"{methodName}({arity})");

    private static string EnclosingType(SyntaxNode node)
    {

        string[] types =
        [
            .. node.Ancestors().OfType<TypeDeclarationSyntax>()
                .Reverse()
                .Select(static type => type.Identifier.ValueText),
        ];

        return types.Length == 0 ? "<global>" : string.Join('.', types);

    }

    private static string EnclosingMember(SyntaxNode node)
    {

        LocalFunctionStatementSyntax? localFunction = node.AncestorsAndSelf()
            .OfType<LocalFunctionStatementSyntax>()
            .FirstOrDefault();

        if (localFunction is not null)
        {

            return localFunction.Identifier.ValueText + "(" + localFunction.ParameterList.Parameters.Count + ")";

        }

        MethodDeclarationSyntax? method = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        if (method is not null)
        {

            return method.Identifier.ValueText + "(" + method.ParameterList.Parameters.Count + ")";

        }

        return "<global>";

    }

    private static string TerminalName(SyntaxNode node) => node switch
    {
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,

        MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.ValueText,

        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,

        GenericNameSyntax generic => generic.Identifier.ValueText,

        QualifiedNameSyntax qualified => TerminalName(qualified.Right),

        AliasQualifiedNameSyntax aliasQualified => TerminalName(aliasQualified.Name),

        PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,

        _ => node.GetLastToken().ValueText,
    };

    private static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(
        static token => token.Text));

    private static List<(AcquisitionSource Source, CompilationUnitSyntax Root)> Parse(
        IEnumerable<AcquisitionSource> sources) =>
    [
        .. sources.Select(static source => (
            source,
            CSharpSyntaxTree.ParseText(source.Text, new CSharpParseOptions(LanguageVersion.Preview)).GetCompilationUnitRoot())),
    ];

}
