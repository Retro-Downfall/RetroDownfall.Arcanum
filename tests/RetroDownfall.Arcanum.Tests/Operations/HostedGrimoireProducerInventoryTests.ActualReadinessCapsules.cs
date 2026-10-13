using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
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

        const string holderType = "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission";

        const string coordinatorType = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator";

        const string maintenanceType = "RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance";

        const string gateType = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate";

        const string leaseType = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease";

        const string workerType = "RetroDownfall.Arcanum.Infrastructure.Hosting.LongRestSweepHostedService";

        var infrastructure = Assert.Single(compilations, static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        var api = Assert.Single(compilations, static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Api");

        // These are the real three owning/borrowing entry points, not replacement fixture classes.
        AssertOwnedShell(infrastructure, coordinatorType, "ResumeAsync", "ResumeAdmittedAsync");
        AssertOwnedShell(infrastructure, coordinatorType, "RecoverAsync", "RecoverAdmittedAsync");
        AssertOwnedShell(api, maintenanceType, "ProcessAsync", "ProcessAdmittedAsync");

        Assert.Equal(17, (int)GrimoireWorkKind.LongRestSweep);

        var holderBind = Member(infrastructure, holderType, "Bind");

        AssignmentExpressionSyntax storageWrite = Assert.Single(holderBind.Syntax.DescendantNodes().OfType<AssignmentExpressionSyntax>());

        IFieldSymbol storage = Assert.IsAssignableFrom<IFieldSymbol>(holderBind.Model.GetSymbolInfo(storageWrite.Left).Symbol);

        Assert.Equal(Accessibility.Private, storage.DeclaredAccessibility);
        Assert.False(storage.IsStatic);
        Assert.True(SymbolEqualityComparer.Default.Equals(holderBind.Symbol.Parameters[0], holderBind.Model.GetSymbolInfo(storageWrite.Right).Symbol));

        IPropertySymbol leaseProperty = Assert.Single(holderBind.Symbol.ContainingType.GetMembers("Lease").OfType<IPropertySymbol>());

        PropertyDeclarationSyntax leaseSyntax = Assert.IsType<PropertyDeclarationSyntax>(Assert.Single(leaseProperty.DeclaringSyntaxReferences).GetSyntax());

        BinaryExpressionSyntax requiredLease = Assert.IsType<BinaryExpressionSyntax>(leaseSyntax.ExpressionBody!.Expression);

        Assert.True(SymbolEqualityComparer.Default.Equals(storage, infrastructure.GetSemanticModel(leaseSyntax.SyntaxTree).GetSymbolInfo(requiredLease.Left).Symbol));
        Assert.IsType<ThrowExpressionSyntax>(requiredLease.Right);

        var acquire = Member(infrastructure, holderType, "Acquire");

        InvocationExpressionSyntax acquisition = Call(acquire, gateType, "TryAcquireWorkLease");

        Assert.Equal(GrimoireWorkKind.LongRestSweep, (GrimoireWorkKind)Convert.ToInt32(
            Assert.IsAssignableFrom<IFieldSymbol>(acquire.Model.GetSymbolInfo(acquisition.ArgumentList.Arguments[0].Expression).Symbol).ConstantValue));

        var ordinary = Member(infrastructure, workerType, "RunOneAsync");

        InvocationExpressionSyntax ordinaryAcquisition = Call(ordinary, gateType, "TryAcquireWorkLease");

        Assert.Equal(GrimoireWorkKind.LongRestSweep, (GrimoireWorkKind)Convert.ToInt32(
            Assert.IsAssignableFrom<IFieldSymbol>(ordinary.Model.GetSymbolInfo(ordinaryAcquisition.ArgumentList.Arguments[0].Expression).Symbol).ConstantValue));

        InvocationExpressionSyntax ordinaryBind = Call(ordinary, holderType, "Bind");

        LocalDeclarationStatementSyntax ordinaryBinding = Assert.Single(ordinaryBind.Ancestors().OfType<LocalDeclarationStatementSyntax>());

        Assert.NotEqual(0, ordinaryBinding.UsingKeyword.RawKind);

        ILocalSymbol borrowedLease = Assert.IsAssignableFrom<ILocalSymbol>(ordinary.Model.GetSymbolInfo(ordinaryBind.ArgumentList.Arguments[0].Expression).Symbol);

        VariableDeclaratorSyntax retainedLease = Assert.IsType<VariableDeclaratorSyntax>(Assert.Single(borrowedLease.DeclaringSyntaxReferences).GetSyntax());

        LocalDeclarationStatementSyntax retainedLeaseDeclaration = Assert.IsType<LocalDeclarationStatementSyntax>(retainedLease.Parent!.Parent);

        Assert.NotEqual(0, retainedLeaseDeclaration.AwaitKeyword.RawKind);
        Assert.NotEqual(0, retainedLeaseDeclaration.UsingKeyword.RawKind);

        SingleVariableDesignationSyntax admittedVariable = Assert.Single(ordinaryAcquisition.ArgumentList.Arguments[1].DescendantNodes().OfType<SingleVariableDesignationSyntax>());

        ExpressionSyntax admittedValue = Assert.IsType<PostfixUnaryExpressionSyntax>(retainedLease.Initializer!.Value).Operand;

        Assert.True(SymbolEqualityComparer.Default.Equals(ordinary.Model.GetDeclaredSymbol(admittedVariable), ordinary.Model.GetSymbolInfo(admittedValue).Symbol));
        Assert.Same(retainedLeaseDeclaration.Parent, ordinaryBinding.Parent);
        Assert.True(retainedLeaseDeclaration.SpanStart < ordinaryBinding.SpanStart);

        var paid = Member(api, maintenanceType, "ExecutePaidAsync");

        InvocationExpressionSyntax groupCall = Call(paid, leaseType, "TryBeginExternalEffectGroup");

        ILocalSymbol groupReceiver = Assert.IsAssignableFrom<ILocalSymbol>(paid.Model.GetSymbolInfo(
            Assert.IsType<MemberAccessExpressionSyntax>(groupCall.Expression).Expression).Symbol);

        VariableDeclaratorSyntax groupLease = Assert.IsType<VariableDeclaratorSyntax>(Assert.Single(groupReceiver.DeclaringSyntaxReferences).GetSyntax());

        IPropertySymbol holderLease = Assert.IsAssignableFrom<IPropertySymbol>(paid.Model.GetSymbolInfo(groupLease.Initializer!.Value).Symbol);

        Assert.True(SymbolEqualityComparer.Default.Equals(holderLease.ContainingType, api.GetTypeByMetadataName(holderType)));
        Assert.Equal("Lease", holderLease.Name);

        SingleVariableDesignationSyntax groupVariable = Assert.Single(groupCall.ArgumentList.Arguments[0].DescendantNodes().OfType<SingleVariableDesignationSyntax>());

        ISymbol groupSymbol = paid.Model.GetDeclaredSymbol(groupVariable)!;

        LocalDeclarationStatementSyntax retainedGroup = Assert.Single(paid.Syntax.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(), declaration =>
            declaration.AwaitKeyword.RawKind != 0 && declaration.UsingKeyword.RawKind != 0
            && declaration.Declaration.Variables.Any(variable => variable.Initializer is { } initializer
                && SymbolEqualityComparer.Default.Equals(paid.Model.GetSymbolInfo(initializer.Value).Symbol, groupSymbol)));

        Assert.Same(paid.Syntax.Body, retainedGroup.Parent);
        Assert.True(groupCall.SpanStart < retainedGroup.SpanStart);

        var chat = Member(api, "RetroDownfall.Arcanum.Api.Intelligence.ModelCallExecutor", "ExecuteLongRestChatAsync");

        InvocationExpressionSyntax chatSend = Call(chat, "Microsoft.Extensions.AI.IChatClient", "GetResponseAsync");

        var embedding = Member(api, "RetroDownfall.Arcanum.Api.Intelligence.ModelCallExecutor", "ExecuteLongRestEmbeddingAsync");

        InvocationExpressionSyntax embeddingSend = Call(embedding, "Microsoft.Extensions.AI.IEmbeddingGenerator`2", "GenerateAsync");

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

        AssertRoute("LongRunningOperationStartupHostedService", startup, acquire, acquisition);
        AssertRoute("LongRestSweepHostedService", sweep, ordinary, ordinaryAcquisition);

        output.WriteLine("ACTUAL_LONG_REST_SUBSET originalCatalogRoots=2 assertedAuthorities=2 physicalBranchesPerAuthority=2 compilerOwnershipShells=3 fullAggregateQualification=false");

        void AssertRoute(string root, HostedProducerOperationEntry authority,
            (SemanticModel Model, IMethodSymbol Symbol, MethodDeclarationSyntax Syntax) workMember, InvocationExpressionSyntax workCall)
        {
            HostedProducerSite[] work = Sites(workMember, workCall, HostedProducerSiteKind.EffectFrontier);

            string workCapsule = Assert.Single(work.Select(static site => site.CapsuleId).Distinct(StringComparer.Ordinal));

            Assert.All(work, site =>
            {
                Assert.Equal(GrimoireWorkKind.LongRestSweep, site.AdmittedWorkKind);
                Assert.Equal(workCapsule, site.WorkFrontierCapsuleId);
            });

            HostedProducerSite[] groups = Sites(paid, groupCall, HostedProducerSiteKind.EffectFrontier);

            string groupCapsule = Assert.Single(groups.Select(static site => site.CapsuleId).Distinct(StringComparer.Ordinal));

            Assert.All(groups, site =>
            {
                Assert.Equal(workCapsule, site.WorkFrontierCapsuleId);
                Assert.Equal(groupCapsule, site.EffectFrontierCapsuleId);
            });

            foreach (HostedProducerSite send in Sites(chat, chatSend, HostedProducerSiteKind.ProviderCall)
                .Concat(Sites(embedding, embeddingSend, HostedProducerSiteKind.ProviderCall)))
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

            HostedProducerSite[] Sites((SemanticModel Model, IMethodSymbol Symbol, MethodDeclarationSyntax Syntax) member,
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

        static (SemanticModel Model, IMethodSymbol Symbol, MethodDeclarationSyntax Syntax) Member(Compilation compilation, string typeName, string methodName)
        {
            INamedTypeSymbol type = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName(typeName));

            IMethodSymbol method = Assert.Single(type.GetMembers(methodName).OfType<IMethodSymbol>());

            MethodDeclarationSyntax syntax = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(method.DeclaringSyntaxReferences).GetSyntax());

            return (compilation.GetSemanticModel(syntax.SyntaxTree), method, syntax);
        }

        static InvocationExpressionSyntax Call((SemanticModel Model, IMethodSymbol Symbol, MethodDeclarationSyntax Syntax) member, string typeName, string methodName)
        {
            INamedTypeSymbol type = Assert.IsAssignableFrom<INamedTypeSymbol>(member.Model.Compilation.GetTypeByMetadataName(typeName));

            return Assert.Single(member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
                member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol called && called.Name == methodName
                && SymbolEqualityComparer.Default.Equals(called.ContainingType.OriginalDefinition, type));
        }

        static void AssertOwnedShell(Compilation compilation, string typeName, string entryName, string helperName)
        {
            var entry = Member(compilation, typeName, entryName);

            InvocationExpressionSyntax acquireCall = Call(entry, holderType, "Acquire");

            InvocationExpressionSyntax bindCall = Call(entry, holderType, "Bind");

            LocalDeclarationStatementSyntax acquiredDeclaration = Assert.Single(acquireCall.Ancestors().OfType<LocalDeclarationStatementSyntax>());

            LocalDeclarationStatementSyntax bindingDeclaration = Assert.Single(bindCall.Ancestors().OfType<LocalDeclarationStatementSyntax>());

            Assert.NotEqual(0, acquiredDeclaration.AwaitKeyword.RawKind);
            Assert.NotEqual(0, acquiredDeclaration.UsingKeyword.RawKind);
            Assert.NotEqual(0, bindingDeclaration.UsingKeyword.RawKind);
            Assert.Same(entry.Syntax.Body, acquiredDeclaration.Parent);
            Assert.Same(entry.Syntax.Body, bindingDeclaration.Parent);
            Assert.True(acquiredDeclaration.SpanStart < bindingDeclaration.SpanStart);

            ISymbol acquired = entry.Model.GetDeclaredSymbol(Assert.Single(acquiredDeclaration.Declaration.Variables))!;

            Assert.True(SymbolEqualityComparer.Default.Equals(acquired,
                entry.Model.GetSymbolInfo(bindCall.ArgumentList.Arguments[0].Expression).Symbol));
            Assert.True(SymbolEqualityComparer.Default.Equals(
                entry.Model.GetSymbolInfo(Assert.IsType<MemberAccessExpressionSyntax>(acquireCall.Expression).Expression).Symbol,
                entry.Model.GetSymbolInfo(Assert.IsType<MemberAccessExpressionSyntax>(bindCall.Expression).Expression).Symbol));

            var helper = Member(compilation, typeName, helperName);

            IParameterSymbol lease = Assert.Single(helper.Symbol.Parameters, parameter =>
                SymbolEqualityComparer.Default.Equals(parameter.Type, compilation.GetTypeByMetadataName(leaseType)));

            InvocationExpressionSyntax ownedJoin = Assert.Single(entry.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
                SymbolEqualityComparer.Default.Equals(entry.Model.GetSymbolInfo(call).Symbol, helper.Symbol)
                && SymbolEqualityComparer.Default.Equals(entry.Model.GetSymbolInfo(call.ArgumentList.Arguments[lease.Ordinal].Expression).Symbol, acquired));

            Assert.True(bindingDeclaration.SpanStart < ownedJoin.SpanStart);
            Assert.Contains(ownedJoin.Ancestors(), static ancestor => ancestor is AwaitExpressionSyntax);
            Assert.Contains(ownedJoin.Ancestors(), static ancestor => ancestor is ReturnStatementSyntax);
        }
    }
}
