using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string ActualLongRestHolderType = "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission";

    private const string ActualLongRestCoordinatorType = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator";

    private const string ActualLongRestSweepPort = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedSweep";

    private const string ActualLongRestMaintenanceType = "RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance";

    private const string ActualLongRestGateType = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate";

    private const string ActualLongRestLeaseType = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease";

    private const string ActualLongRestWorkerType = "RetroDownfall.Arcanum.Infrastructure.Hosting.LongRestSweepHostedService";

    private readonly record struct ActualLongRestMember(SemanticModel Model, IMethodSymbol Symbol, MethodDeclarationSyntax Syntax);

    /// <summary>The real members that carry the one Long Rest lease from its mint to the physical send, with their key calls.</summary>
    private sealed record ActualLongRestSpine(
        ActualLongRestMember Worker, InvocationExpressionSyntax WorkerAcquisition,
        ActualLongRestMember Recover, InvocationExpressionSyntax RecoverAcquisition,
        ActualLongRestMember Paid, InvocationExpressionSyntax GroupCall,
        ActualLongRestMember Chat, InvocationExpressionSyntax ChatSend,
        ActualLongRestMember Embedding, InvocationExpressionSyntax EmbeddingSend);

    [Fact]
    public void ActualLongRestSpineCarriesOneLeaseByValueFromMintToExternalEffectGroup() => ReadActualLongRestSpine();

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void ActualLongRestReadinessAndOrdinaryRootsRetainPhysicalDispatchCapsules()
    {
        IReadOnlyList<Microsoft.CodeAnalysis.CSharp.CSharpCompilation> compilations =
            HostedGrimoireProducerInventory.ProductionCompilations;

        string[] roots = ["LongRunningOperationStartupHostedService", "LongRestSweepHostedService"];

        HostedProducerServiceEntry[] selected = HostedGrimoireProducerInventory.Catalog
            .Where(entry => roots.Contains(entry.ServiceType, StringComparer.Ordinal)).ToArray();

        Assert.Equal(roots.Order(StringComparer.Ordinal), selected.Select(static entry => entry.ServiceType).Order(StringComparer.Ordinal));

        Assert.All(selected, entry => Assert.Same(
            HostedGrimoireProducerInventory.Catalog.Single(original => original.ServiceType == entry.ServiceType), entry));

        HostedProducerDiscovery<string> registrations = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices(compilations);

        Assert.Empty(registrations.Diagnostics);

        foreach (string root in roots)
        {
            Assert.Contains(registrations.Items, registration => registration == root
                || registration.EndsWith("." + root, StringComparison.Ordinal));
        }

        ActualLongRestSpine spine = ReadActualLongRestSpine();

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        HostedProducerAnalysisMetrics metrics = Assert.IsType<HostedProducerAnalysisMetrics>(discovery.AnalysisMetrics);

        foreach (string line in RenderRootTraversalMetrics(metrics.RootTraversals)) output.WriteLine(line);

        foreach (var diagnosticGroup in discovery.Diagnostics.GroupBy(static diagnostic => diagnostic.Code).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"ACTUAL_LONG_REST_SUBSET diagnostic={diagnosticGroup.Key} count={diagnosticGroup.Count()}");
        }

        // A populated capsule list can coexist with an incomplete traversal.
        // Match the existing production manifest contract for every diagnostic,
        // including refusals whose identity is only a raw source location.
        Assert.True(discovery.Diagnostics.Count == 0, string.Join(global::System.Environment.NewLine,
            discovery.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Identity}: {diagnostic.Detail}")));

        foreach (string root in roots)
        {
            Assert.Contains(metrics.RootTraversals, metric => metric.RootType == root && metric.AnalyzedStates > 0);
        }

        HostedProducerOperationEntry startup = Assert.Single(selected.Single(static entry => entry.ServiceType == "LongRunningOperationStartupHostedService").Operations,
            static operation => operation.Authority == HostedProducerAuthorityKind.PreReadinessStartup);

        HostedProducerOperationEntry sweep = Assert.Single(selected.Single(static entry => entry.ServiceType == "LongRestSweepHostedService").Operations,
            static operation => operation.Authority == HostedProducerAuthorityKind.OrdinaryHostedWork);

        AssertRoute("LongRunningOperationStartupHostedService", startup, spine.Recover, spine.RecoverAcquisition);
        AssertRoute("LongRestSweepHostedService", sweep, spine.Worker, spine.WorkerAcquisition);

        output.WriteLine("ACTUAL_LONG_REST_SUBSET originalCatalogRoots=2 assertedAuthorities=2 physicalBranchesPerAuthority=2 compilerOwnershipShells=2 fullAggregateQualification=false");

        void AssertRoute(string root, HostedProducerOperationEntry authority,
            ActualLongRestMember workMember, InvocationExpressionSyntax workCall)
        {
            HostedProducerSite[] work = Sites(workMember, workCall, HostedProducerSiteKind.EffectFrontier);

            string workCapsule = Assert.Single(work.Select(static site => site.CapsuleId).Distinct(StringComparer.Ordinal));

            Assert.All(work, site =>
            {
                Assert.Equal(GrimoireWorkKind.LongRestSweep, site.AdmittedWorkKind);
                Assert.Equal(workCapsule, site.WorkFrontierCapsuleId);
            });

            HostedProducerSite[] groups = Sites(spine.Paid, spine.GroupCall, HostedProducerSiteKind.EffectFrontier);

            string groupCapsule = Assert.Single(groups.Select(static site => site.CapsuleId).Distinct(StringComparer.Ordinal));

            Assert.All(groups, site =>
            {
                Assert.Equal(workCapsule, site.WorkFrontierCapsuleId);
                Assert.Equal(groupCapsule, site.EffectFrontierCapsuleId);
            });

            foreach (HostedProducerSite send in Sites(spine.Chat, spine.ChatSend, HostedProducerSiteKind.ProviderCall)
                .Concat(Sites(spine.Embedding, spine.EmbeddingSend, HostedProducerSiteKind.ProviderCall)))
            {
                Assert.Equal(workCapsule, send.WorkFrontierCapsuleId);
                Assert.Equal(groupCapsule, send.EffectFrontierCapsuleId);
                output.WriteLine($"ACTUAL_LONG_REST_SUBSET authority={authority.OperationId} source={send.SourcePath} member={send.MemberIdentity} capsule={send.CapsuleId} work={workCapsule} group={groupCapsule}");
            }

            HostedProducerInventoryDiagnostic[] failures = discovery.Diagnostics.Where(diagnostic =>
                diagnostic.Identity.StartsWith(authority.OperationId, StringComparison.Ordinal)
                && (diagnostic.Code.StartsWith("HOSTED_RECOVERY_", StringComparison.Ordinal)
                    || diagnostic.Code is "HOSTED_ROOT_UNRESOLVED" or "HOSTED_SITE_UNCLASSIFIED"
                        or "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                        or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                        or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED" or "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED"))
                .ToArray();

            Assert.True(failures.Length == 0, string.Join(global::System.Environment.NewLine,
                failures.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Identity}: {diagnostic.Detail}")));

            HostedProducerSite[] Sites(ActualLongRestMember member,
                InvocationExpressionSyntax call, HostedProducerSiteKind kind)
            {
                string identity = Assert.IsType<string>(member.Symbol.GetDocumentationCommentId());

                string fingerprint = HostedGrimoireProducerInventory.Fingerprint(call);

                HostedProducerSite[] matches = discovery.Items.Where(site => site.RootType == root
                    && site.AuthorityOperationId == authority.OperationId && site.SourcePath == call.SyntaxTree.FilePath
                    && site.MemberIdentity == identity && site.Kind == kind && site.SyntaxFingerprint == fingerprint).ToArray();

                Assert.True(matches.Length > 0, $"Missing actual source site: {root}; {authority.OperationId}; {identity}; {call}");

                IMethodSymbol called = Assert.IsAssignableFrom<IMethodSymbol>(member.Model.GetSymbolInfo(call).Symbol);

                Assert.All(matches, site => Assert.True(site.Callee.EndsWith("." + called.Name, StringComparison.Ordinal), site.Callee));

                return matches;
            }
        }

    }

    // The lease is minted twice in production and never stored: by the hosted worker, and inline by the recovery
    // entry that still runs a clean sweep. Both hand the acquired local by value to an admitted helper, the Session
    // shell does the same for its own lease, and every later use takes the lease as a parameter.
    private static ActualLongRestSpine ReadActualLongRestSpine()
    {
        IReadOnlyList<CSharpCompilation> compilations = HostedGrimoireProducerInventory.ProductionCompilations;

        CSharpCompilation core = Assert.Single(compilations, static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Core");

        CSharpCompilation infrastructure = Assert.Single(compilations, static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        CSharpCompilation api = Assert.Single(compilations, static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Api");

        Assert.Equal(17, (int)GrimoireWorkKind.LongRestSweep);

        // The holder and its public Core process member are gone.
        Assert.Null(infrastructure.GetTypeByMetadataName(ActualLongRestHolderType));

        INamedTypeSymbol corePort = Assert.IsAssignableFrom<INamedTypeSymbol>(core.GetTypeByMetadataName("RetroDownfall.Arcanum.Core.LongRest.ILongRestMaintenance"));

        Assert.Empty(corePort.GetMembers("ProcessAsync"));

        // The hosted worker: acquire, retain with await using, hand the retained local to the port. Nothing is bound.
        ActualLongRestMember worker = ActualLongRestMemberOf(infrastructure, ActualLongRestWorkerType, "RunOneAsync");

        InvocationExpressionSyntax workerAcquisition = ActualLongRestCallOf(worker, ActualLongRestGateType, "TryAcquireWorkLease");

        AssertLongRestSweepKind(worker, workerAcquisition);

        ILocalSymbol retained = AssertRetainedLease(worker, workerAcquisition);

        InvocationExpressionSyntax handoff = ActualLongRestCallOf(worker, ActualLongRestSweepPort, "ResumeAdmittedAsync");

        Assert.True(SymbolEqualityComparer.Default.Equals(retained, worker.Model.GetSymbolInfo(handoff.ArgumentList.Arguments[1].Expression).Symbol));

        Assert.Contains(handoff.Ancestors(), static ancestor => ancestor is AwaitExpressionSyntax);

        Assert.DoesNotContain(worker.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>(), static name => name.Identifier.ValueText is "Bind" or "LongRestWorkAdmission");

        // The two self-admitting shells mint their own lease inline and join the helper under it.
        ActualLongRestMember resume = ActualLongRestMemberOf(infrastructure, ActualLongRestCoordinatorType, "ResumeAsync");

        AssertSelfAdmittingShell(resume, "ResumeCoreAsync");

        ActualLongRestMember recover = ActualLongRestMemberOf(infrastructure, ActualLongRestCoordinatorType, "RecoverAsync");

        InvocationExpressionSyntax recoverAcquisition = AssertSelfAdmittingShell(recover, "RecoverAdmittedAsync");

        // The hosted port is a guarded forwarder to the same core, with no capability.
        ActualLongRestMember port = ActualLongRestMemberOf(infrastructure, ActualLongRestCoordinatorType, "ResumeAdmittedAsync");

        AssertAdmissionKindGuard(port);

        IParameterSymbol portLease = LeaseParameter(port);

        InvocationExpressionSyntax forwarded = Assert.Single(port.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => port.Model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "ResumeCoreAsync" });

        Assert.Equal("null", forwarded.ArgumentList.Arguments[1].Expression.ToString());

        Assert.True(SymbolEqualityComparer.Default.Equals(portLease, port.Model.GetSymbolInfo(forwarded.ArgumentList.Arguments[2].Expression).Symbol));

        // The owned run takes the lease formal and passes only that formal to every lease-bearing call.
        ActualLongRestMember owned = ActualLongRestMemberOf(infrastructure, ActualLongRestCoordinatorType, "RunOwnedAsync");

        AssertLeaseOnlyFlowsFromTheFormal(owned, expectedCalls: 5, callees: ["AttestCleanAdmission", "AttestCleanAdmission",
            "ReconcileCompletedCheckpointAsync", "ReadmitCleanAsync", "ProcessAdmittedAsync"]);

        // The admitted maintenance does the same and begins each group on its own parameter.
        ActualLongRestMember process = ActualLongRestMemberOf(api, ActualLongRestMaintenanceType, "ProcessAdmittedAsync");

        AssertAdmissionKindGuard(process);

        AssertLeaseOnlyFlowsFromTheFormal(process, expectedCalls: 2, callees: ["FoldAsync", "AdjudicateAsync"]);

        AssertLeaseOnlyFlowsFromTheFormal(ActualLongRestMemberOf(api, ActualLongRestMaintenanceType, "FoldAsync"), expectedCalls: 2,
            callees: ["ExecutePaidAsync", "ExecutePaidAsync"]);

        AssertLeaseOnlyFlowsFromTheFormal(ActualLongRestMemberOf(api, ActualLongRestMaintenanceType, "AdjudicateAsync"), expectedCalls: 1,
            callees: ["ExecutePaidAsync"]);

        ActualLongRestMember paid = ActualLongRestMemberOf(api, ActualLongRestMaintenanceType, "ExecutePaidAsync");

        InvocationExpressionSyntax groupCall = ActualLongRestCallOf(paid, ActualLongRestLeaseType, "TryBeginExternalEffectGroup");

        IParameterSymbol paidLease = LeaseParameter(paid);

        Assert.True(SymbolEqualityComparer.Default.Equals(paidLease, paid.Model.GetSymbolInfo(
            Assert.IsType<MemberAccessExpressionSyntax>(groupCall.Expression).Expression).Symbol));

        // The local function that runs before each send and every other closure leave the lease alone.
        AssertNoClosureCapturesLease(paid, paidLease);

        SingleVariableDesignationSyntax groupVariable = Assert.Single(groupCall.ArgumentList.Arguments[0].DescendantNodes().OfType<SingleVariableDesignationSyntax>());

        ISymbol groupSymbol = paid.Model.GetDeclaredSymbol(groupVariable)!;

        LocalDeclarationStatementSyntax retainedGroup = Assert.Single(paid.Syntax.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(), declaration =>
            declaration.AwaitKeyword.RawKind != 0 && declaration.UsingKeyword.RawKind != 0
            && declaration.Declaration.Variables.Any(variable => variable.Initializer is { } initializer
                && SymbolEqualityComparer.Default.Equals(paid.Model.GetSymbolInfo(initializer.Value).Symbol, groupSymbol)));

        Assert.Same(paid.Syntax.Body, retainedGroup.Parent);
        Assert.True(groupCall.SpanStart < retainedGroup.SpanStart);

        var chat = ActualLongRestMemberOf(api, "RetroDownfall.Arcanum.Api.Intelligence.ModelCallExecutor", "ExecuteLongRestChatAsync");

        InvocationExpressionSyntax chatSend = ActualLongRestCallOf(chat, "Microsoft.Extensions.AI.IChatClient", "GetResponseAsync");

        var embedding = ActualLongRestMemberOf(api, "RetroDownfall.Arcanum.Api.Intelligence.ModelCallExecutor", "ExecuteLongRestEmbeddingAsync");

        InvocationExpressionSyntax embeddingSend = ActualLongRestCallOf(embedding, "Microsoft.Extensions.AI.IEmbeddingGenerator`2", "GenerateAsync");

        return new(worker, workerAcquisition, recover, recoverAcquisition, paid, groupCall, chat, chatSend, embedding, embeddingSend);
    }

    private static ActualLongRestMember ActualLongRestMemberOf(Compilation compilation, string typeName, string methodName)
    {
        INamedTypeSymbol type = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName(typeName));

        IMethodSymbol method = Assert.Single(type.GetMembers(methodName).OfType<IMethodSymbol>());

        MethodDeclarationSyntax syntax = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(method.DeclaringSyntaxReferences).GetSyntax());

        return new(compilation.GetSemanticModel(syntax.SyntaxTree), method, syntax);
    }

    private static InvocationExpressionSyntax ActualLongRestCallOf(ActualLongRestMember member, string typeName, string methodName)
    {
        INamedTypeSymbol type = Assert.IsAssignableFrom<INamedTypeSymbol>(member.Model.Compilation.GetTypeByMetadataName(typeName));

        return Assert.Single(member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol called && called.Name == methodName
            && SymbolEqualityComparer.Default.Equals(called.ContainingType.OriginalDefinition, type));
    }

    private static IParameterSymbol LeaseParameter(ActualLongRestMember member) => Assert.Single(member.Symbol.Parameters,
        parameter => parameter.Type.ToDisplayString() == ActualLongRestLeaseType && parameter.Name == "admission");

    private static void AssertLongRestSweepKind(ActualLongRestMember member, InvocationExpressionSyntax acquisition) =>
        Assert.Equal(GrimoireWorkKind.LongRestSweep, (GrimoireWorkKind)Convert.ToInt32(
            Assert.IsAssignableFrom<IFieldSymbol>(member.Model.GetSymbolInfo(acquisition.ArgumentList.Arguments[0].Expression).Symbol).ConstantValue));

    // `await using IGrimoireWorkLease x = <the acquisition's out value>`, directly in the member body, after the acquisition.
    private static ILocalSymbol AssertRetainedLease(ActualLongRestMember member, InvocationExpressionSyntax acquisition)
    {
        SingleVariableDesignationSyntax admittedVariable = Assert.Single(acquisition.ArgumentList.Arguments[1].DescendantNodes().OfType<SingleVariableDesignationSyntax>());

        ISymbol admitted = member.Model.GetDeclaredSymbol(admittedVariable)!;

        LocalDeclarationStatementSyntax declaration = Assert.Single(member.Syntax.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(), candidate =>
            candidate.AwaitKeyword.RawKind != 0 && candidate.UsingKeyword.RawKind != 0
            && candidate.Declaration.Variables.Single().Initializer!.Value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(name => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(name).Symbol, admitted)));

        Assert.True(acquisition.SpanStart < declaration.SpanStart);

        return Assert.IsAssignableFrom<ILocalSymbol>(member.Model.GetDeclaredSymbol(declaration.Declaration.Variables.Single()));
    }

    private static InvocationExpressionSyntax AssertSelfAdmittingShell(ActualLongRestMember entry, string helperName)
    {
        InvocationExpressionSyntax acquisition = ActualLongRestCallOf(entry, ActualLongRestGateType, "TryAcquireWorkLease");

        AssertLongRestSweepKind(entry, acquisition);

        // The acquisition is the condition of a statement of the entry's own body, so the gate's ambient lifetime is this context's.
        IfStatementSyntax gate = Assert.IsType<IfStatementSyntax>(acquisition.Ancestors().OfType<StatementSyntax>().First());

        Assert.Same(entry.Syntax.Body, gate.Parent);

        ILocalSymbol acquired = AssertRetainedLease(entry, acquisition);

        var helper = ActualLongRestMemberOf(entry.Model.Compilation, ActualLongRestCoordinatorType, helperName);

        IParameterSymbol lease = Assert.Single(helper.Symbol.Parameters, parameter => parameter.Type.ToDisplayString() == ActualLongRestLeaseType);

        InvocationExpressionSyntax ownedJoin = Assert.Single(entry.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            SymbolEqualityComparer.Default.Equals(entry.Model.GetSymbolInfo(call).Symbol, helper.Symbol)
            && SymbolEqualityComparer.Default.Equals(entry.Model.GetSymbolInfo(call.ArgumentList.Arguments[lease.Ordinal].Expression).Symbol, acquired));

        Assert.Contains(ownedJoin.Ancestors(), static ancestor => ancestor is AwaitExpressionSyntax);
        Assert.Contains(ownedJoin.Ancestors(), static ancestor => ancestor is ReturnStatementSyntax);
        Assert.DoesNotContain(entry.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>(), static name => name.Identifier.ValueText is "Bind" or "LongRestWorkAdmission");

        return acquisition;
    }

    // Both admitted entries refuse a missing lease and any lease that is not a Long Rest sweep lease before doing work.
    private static void AssertAdmissionKindGuard(ActualLongRestMember entry)
    {
        Assert.Equal(RefKind.None, LeaseParameter(entry).RefKind);

        Assert.Equal("if (admission is null) throw new ArgumentNullException(nameof(admission));", entry.Syntax.Body!.Statements[0].ToString());

        Assert.StartsWith("if (admission.Kind != GrimoireWorkKind.LongRestSweep) throw new ArgumentException(", entry.Syntax.Body.Statements[1].ToString(), StringComparison.Ordinal);
    }

    // Every call in the member that receives a lease receives its own lease formal, and no function inside it closes over the lease.
    private static void AssertLeaseOnlyFlowsFromTheFormal(ActualLongRestMember member, int expectedCalls, string[] callees)
    {
        IParameterSymbol formal = LeaseParameter(member);

        // `nameof(admission)` in the guard is a constant, not a use of the lease.
        InvocationExpressionSyntax[] carrying = [.. member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
            !member.Model.GetConstantValue(call).HasValue && call.ArgumentList.Arguments.Any(argument => member.Model.GetTypeInfo(argument.Expression).Type?.ToDisplayString() == ActualLongRestLeaseType))];

        Assert.True(carrying.Length == expectedCalls, $"{member.Symbol.Name} passes a lease in {carrying.Length} calls, expected {expectedCalls}: "
            + string.Join(" | ", carrying.Select(static call => call.Expression.ToString())));

        Assert.Equal(callees.Order(StringComparer.Ordinal),
            carrying.Select(call => Assert.IsAssignableFrom<IMethodSymbol>(member.Model.GetSymbolInfo(call).Symbol).Name).Order(StringComparer.Ordinal));

        foreach (InvocationExpressionSyntax call in carrying)
        {
            ArgumentSyntax[] leaseArguments = [.. call.ArgumentList.Arguments.Where(argument =>
                member.Model.GetTypeInfo(argument.Expression).Type?.ToDisplayString() == ActualLongRestLeaseType)];

            ArgumentSyntax leaseArgument = Assert.Single(leaseArguments);

            Assert.True(SymbolEqualityComparer.Default.Equals(formal, member.Model.GetSymbolInfo(leaseArgument.Expression).Symbol),
                $"{member.Symbol.Name} passes {leaseArgument.Expression} instead of its lease formal.");
        }

        AssertNoClosureCapturesLease(member, formal);
    }

    private static void AssertNoClosureCapturesLease(ActualLongRestMember member, IParameterSymbol formal)
    {
        foreach (SyntaxNode closure in member.Syntax.DescendantNodes().Where(static node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
        {
            Assert.DoesNotContain(closure.DescendantNodes().OfType<IdentifierNameSyntax>(), name =>
                SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(name).Symbol, formal));
        }
    }
}
