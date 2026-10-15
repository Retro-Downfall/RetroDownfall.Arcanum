using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("direct-lease", true)]
    [InlineData("borrowed-shell", true)]
    [InlineData("destructive-observation", false)]
    [InlineData("lease-ended-before-observation", false)]
    [InlineData("lease-ended-after-observation", true)]
    [InlineData("composite-prior-disposal", false)]
    public void OrdinaryRetainedHolderSurvivesTheActualBorrowedAdmissionShell(string shape, bool admitted)
    {
        const string coordinatorName = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator";

        const string holderName = "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission";

        string source = OrdinaryScopedHolderFixture("same-scoped-lease");

        ClassDeclarationSyntax original = Assert.Single(CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes().OfType<ClassDeclarationSyntax>(), static type => type.Identifier.ValueText == "LongRestSweepCoordinator");

        string sends = string.Join(global::System.Environment.NewLine, original.Members.OfType<MethodDeclarationSyntax>()
            .Where(static method => method.Identifier.ValueText is "SendFirstAsync" or "SendSecondAsync")
            .Select(static method => method.ToFullString()));

        string shell = shape == "direct-lease"
            ? "await ResumeAdmittedAsync(workAdmission.Lease, ct).ConfigureAwait(false);"
            : """
                ct.ThrowIfCancellationRequested();

                if (workAdmission.HasLease)
                {
                    await ResumeAdmittedAsync(workAdmission.Lease, ct).ConfigureAwait(false);

                    return;
                }

                await using IGrimoireWorkLease acquired = workAdmission.Acquire(ct);

                using IDisposable binding = workAdmission.Bind(acquired);

                await ResumeAdmittedAsync(acquired, ct).ConfigureAwait(false);
                """;

        if (shape == "lease-ended-before-observation")
            shell = "workAdmission.Lease.Dispose();" + global::System.Environment.NewLine + shell;

        if (shape == "lease-ended-after-observation")
            shell = shell.ReplaceSdkFixtureText("await ResumeAdmittedAsync(workAdmission.Lease, ct).ConfigureAwait(false);",
                "await ResumeAdmittedAsync(workAdmission.Lease, ct).ConfigureAwait(false); ((global::Worker.ForeignLease)workAdmission.Lease).Dispose();", StringComparison.Ordinal);

        string? compositeShell = shape == "composite-prior-disposal" ? shell : null;

        if (compositeShell is not null) shell = "await ResumeEnabledAsync(true, ct).ConfigureAwait(false);";

        string replacement = $$"""
            internal sealed class LongRestSweepCoordinator(LongRestWorkAdmission workAdmission) : ILongRestSweepCoordinator
            {
                public async Task ResumeAsync(Guid operation, object? capability, CancellationToken ct)
                {
                    {{shell}}
                }

                private async Task ResumeAdmittedAsync(IGrimoireWorkLease admission, CancellationToken ct)
                {
                    await SendFirstAsync(ct).ConfigureAwait(false);

                    await SendSecondAsync(ct).ConfigureAwait(false);
                }

                {{sends}}
            }
            """;

        if (compositeShell is not null)
        {
            string composite = "private async Task ResumeEnabledAsync(bool enabled, CancellationToken ct) { "
                + "bool localFlag = false; if (workAdmission.HasLease) localFlag = true; "
                + "if (enabled && localFlag) workAdmission.Lease.Dispose(); " + compositeShell + " }";

            replacement = replacement.Insert(replacement.LastIndexOf('}'), composite);
        }

        source = source.ReplaceSdkFixtureText(original.ToFullString(), replacement, StringComparison.Ordinal);

        string observation = shape == "destructive-observation"
            ? "internal bool HasLease { get { current!.Dispose(); return true; } }"
            : "internal bool HasLease => current is not null;";

        const string getter = "internal IGrimoireWorkLease Lease => current ?? throw new InvalidOperationException();";

        source = source.ReplaceSdkFixtureText(getter, getter + global::System.Environment.NewLine + observation + """

                    internal IGrimoireWorkLease Acquire(CancellationToken ct)
                    {
                        ct.ThrowIfCancellationRequested();

                        IGrimoireConnectionAdmissionGate gate = null!;

                        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease acquired))
                            throw new InvalidOperationException();

                        return acquired ?? throw new InvalidOperationException();
                    }
                """, StringComparison.Ordinal);

        if (shape == "lease-ended-after-observation")
        {
            source = source.ReplaceSdkFixtureText(
                "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!;",
                "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = new RetroDownfall.Arcanum.Infrastructure.Data.GrimoireConnectionAdmissionGate();",
                StringComparison.Ordinal);

            source = source.ReplaceSdkFixtureText("private sealed class ForeignLease", "internal sealed class ForeignLease", StringComparison.Ordinal);

            // The gate the fixture acquires from is the reviewed gate (see the Single Gate Census), and the lease it mints is the fixture's own.
            source += """

                namespace RetroDownfall.Arcanum.Infrastructure.Data
                {
                    internal sealed class GrimoireConnectionAdmissionGate : IGrimoireConnectionAdmissionGate
                    {
                        public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease)
                        {
                            lease = new global::Worker.ForeignLease();

                            return true;
                        }
                    }
                }
                """;
        }

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax Type(string name) => Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            type => type.Identifier.ValueText == name);

        MethodDeclarationSyntax Method(string owner, string name) => Assert.Single(Type(owner).Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == name);

        IParameterSymbol holder = Assert.IsAssignableFrom<IParameterSymbol>(model.GetDeclaredSymbol(
            Assert.Single(Type("LongRestSweepCoordinator").ParameterList!.Parameters)));

        Assert.Equal(holderName, holder.Type.ToDisplayString());

        Assert.Equal(MethodKind.Constructor, Assert.IsAssignableFrom<IMethodSymbol>(holder.ContainingSymbol).MethodKind);

        PropertyDeclarationSyntax observed = Assert.Single(Type("LongRestWorkAdmission").Members.OfType<PropertyDeclarationSyntax>(),
            static property => property.Identifier.ValueText == "HasLease");

        IPropertySymbol observationSymbol = Assert.IsAssignableFrom<IPropertySymbol>(model.GetDeclaredSymbol(observed));

        Assert.Equal(compilation.Assembly.Identity, observationSymbol.ContainingAssembly.Identity);

        AssignmentExpressionSyntax store = Assert.Single(Method("LongRestWorkAdmission", "Bind")
            .DescendantNodes().OfType<AssignmentExpressionSyntax>(), assignment => model.GetSymbolInfo(assignment.Left).Symbol is IFieldSymbol);

        IFieldSymbol storage = Assert.IsAssignableFrom<IFieldSymbol>(model.GetSymbolInfo(store.Left).Symbol);

        IMethodSymbol bindDefinition = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(
            Method("LongRestWorkAdmission", "Bind")));

        IParameterSymbol storedLeaseParameter = Assert.Single(bindDefinition.Parameters);

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", storedLeaseParameter.Type.ToDisplayString());

        Assert.Equal(compilation.Assembly.Identity, storedLeaseParameter.Type.ContainingAssembly.Identity);

        Assert.Equal(RefKind.None, storedLeaseParameter.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(storedLeaseParameter,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(model.GetOperation(store.Right)).Parameter));

        if (shape != "destructive-observation")
        {
            IsPatternExpressionSyntax pattern = Assert.IsType<IsPatternExpressionSyntax>(observed.ExpressionBody!.Expression);

            var storageRead = Assert.IsAssignableFrom<IFieldReferenceOperation>(model.GetOperation(pattern.Expression));

            Assert.True(SymbolEqualityComparer.Default.Equals(storage, storageRead.Field));

            Assert.IsAssignableFrom<IInstanceReferenceOperation>(storageRead.Instance);

            Assert.IsType<UnaryPatternSyntax>(pattern.Pattern);
        }
        else
        {
            InvocationExpressionSyntax cleanup = Assert.Single(observed.DescendantNodes().OfType<InvocationExpressionSyntax>());

            var cleanupCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(cleanup));

            Assert.Equal("System.IDisposable", cleanupCall.TargetMethod.ContainingType.ToDisplayString());

            Assert.Equal("Dispose", cleanupCall.TargetMethod.Name);

            IdentifierNameSyntax storedReceiver = Assert.Single(cleanup.DescendantNodes().OfType<IdentifierNameSyntax>(),
                identifier => model.GetSymbolInfo(identifier).Symbol is IFieldSymbol);

            Assert.True(SymbolEqualityComparer.Default.Equals(storage, model.GetSymbolInfo(storedReceiver).Symbol));
        }

        MethodDeclarationSyntax resume = Method("LongRestSweepCoordinator", "ResumeAsync");

        MethodDeclarationSyntax shellMember = shape == "composite-prior-disposal"
            ? Method("LongRestSweepCoordinator", "ResumeEnabledAsync") : resume;

        if (shape != "direct-lease")
        {
            IfStatementSyntax guard = Assert.Single(shellMember.DescendantNodes().OfType<IfStatementSyntax>(),
                candidate => candidate.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Any(call => model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "ResumeAdmittedAsync" }));

            var selectedObservation = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(guard.Condition));

            Assert.True(SymbolEqualityComparer.Default.Equals(observationSymbol, selectedObservation.Property));

            Assert.True(SymbolEqualityComparer.Default.Equals(holder,
                Assert.IsAssignableFrom<IParameterReferenceOperation>(selectedObservation.Instance).Parameter));

            Assert.Single(shellMember.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(),
                statement => statement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)
                    && statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword));
        }

        MemberAccessExpressionSyntax lease = Assert.Single(shellMember.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            access => access.Name.Identifier.ValueText == "Lease" && access.Parent is ArgumentSyntax);

        var leaseRead = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(lease));

        Assert.Equal(holderName, leaseRead.Property.ContainingType.ToDisplayString());

        Assert.True(SymbolEqualityComparer.Default.Equals(holder,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(leaseRead.Instance).Parameter));

        InvocationExpressionSyntax borrowed = Assert.Single(shellMember.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "ResumeAdmittedAsync" }
                && call.ArgumentList.Arguments[0].Expression == lease);

        Assert.IsType<AwaitExpressionSyntax>(borrowed.Parent!.Parent!.Parent);

        var joinedCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(borrowed));

        Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(Method("LongRestSweepCoordinator", "ResumeAdmittedAsync")),
            joinedCall.TargetMethod));

        Assert.IsAssignableFrom<IInstanceReferenceOperation>(joinedCall.Instance);

        if (shape is "lease-ended-before-observation" or "lease-ended-after-observation" or "composite-prior-disposal")
        {
            InvocationExpressionSyntax leaseCleanup = Assert.Single(shellMember.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                call => model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "Dispose" });

            var releaseCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(leaseCleanup));

            IOperation? releaseReceiver = releaseCall.Instance;

            if (shape == "lease-ended-after-observation")
            {
                var concreteCast = Assert.IsAssignableFrom<IConversionOperation>(releaseReceiver);

                Assert.True(concreteCast.Conversion.IsReference && !concreteCast.Conversion.IsUserDefined);

                Assert.Null(concreteCast.OperatorMethod);

                Assert.True(SymbolEqualityComparer.Default.Equals(concreteCast.Type, model.GetDeclaredSymbol(Type("ForeignLease"))));

                Assert.True(SymbolEqualityComparer.Default.Equals(concreteCast.Type, releaseCall.TargetMethod.ContainingType));

                Assert.Equal(compilation.Assembly.Identity, releaseCall.TargetMethod.ContainingAssembly.Identity);

                releaseReceiver = concreteCast.Operand;
            }
            else Assert.Equal("System.IDisposable", releaseCall.TargetMethod.ContainingType.ToDisplayString());

            var releasedProperty = Assert.IsAssignableFrom<IPropertyReferenceOperation>(releaseReceiver);

            Assert.True(SymbolEqualityComparer.Default.Equals(leaseRead.Property, releasedProperty.Property));

            Assert.True(SymbolEqualityComparer.Default.Equals(holder,
                Assert.IsAssignableFrom<IParameterReferenceOperation>(releasedProperty.Instance).Parameter));

            Assert.Equal(shape == "lease-ended-after-observation", borrowed.Span.End < leaseCleanup.SpanStart);

            if (shape == "composite-prior-disposal")
            {
                InvocationExpressionSyntax enabledCall = Assert.Single(resume.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                    call => model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "ResumeEnabledAsync" });

                var enabledInvocation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(enabledCall));

                Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(shellMember), enabledInvocation.TargetMethod));

                Assert.IsType<AwaitExpressionSyntax>(enabledCall.Parent!.Parent!.Parent);

                IArgumentOperation enabledArgument = Assert.Single(enabledInvocation.Arguments,
                    static argument => argument.Parameter!.Name == "enabled");

                Assert.Equal(true, enabledArgument.Value.ConstantValue.Value);

                Assert.True(enabledArgument.InConversion.IsIdentity);

                BinaryExpressionSyntax compositeCondition = Assert.IsType<BinaryExpressionSyntax>(
                    Assert.IsType<IfStatementSyntax>(leaseCleanup.Parent!.Parent).Condition);

                Assert.True(compositeCondition.IsKind(SyntaxKind.LogicalAndExpression));

                Assert.True(SymbolEqualityComparer.Default.Equals(enabledArgument.Parameter,
                    model.GetSymbolInfo(compositeCondition.Left).Symbol));

                ILocalSymbol flag = Assert.IsAssignableFrom<ILocalSymbol>(model.GetSymbolInfo(compositeCondition.Right).Symbol);

                AssignmentExpressionSyntax flagAssignment = Assert.Single(shellMember.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                    assignment => SymbolEqualityComparer.Default.Equals(flag, model.GetSymbolInfo(assignment.Left).Symbol));

                Assert.Equal(true, model.GetConstantValue(flagAssignment.Right).Value);

                var earlierObservation = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(
                    Assert.IsType<IfStatementSyntax>(flagAssignment.Parent!.Parent).Condition));

                Assert.True(SymbolEqualityComparer.Default.Equals(observationSymbol, earlierObservation.Property));

                Assert.True(SymbolEqualityComparer.Default.Equals(holder,
                    Assert.IsAssignableFrom<IParameterReferenceOperation>(earlierObservation.Instance).Parameter));

                Assert.True(flagAssignment.Span.End < leaseCleanup.SpanStart && leaseCleanup.Span.End < borrowed.SpanStart);
            }
        }

        MethodDeclarationSyntax run = Method("Worker", "RunOneAsync");

        InvocationExpressionSyntax rootBinding = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "Bind" });

        var rootBindCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(rootBinding));

        Assert.Equal(holderName, rootBindCall.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(compilation.Assembly.Identity, rootBindCall.TargetMethod.ContainingAssembly.Identity);

        Assert.True(SymbolEqualityComparer.Default.Equals(bindDefinition, rootBindCall.TargetMethod));

        ILocalSymbol rootHolderLocal = Assert.IsAssignableFrom<ILocalReferenceOperation>(rootBindCall.Instance).Local;

        VariableDeclaratorSyntax rootHolderDeclaration = Assert.IsType<VariableDeclaratorSyntax>(
            Assert.Single(rootHolderLocal.DeclaringSyntaxReferences).GetSyntax());

        InvocationExpressionSyntax rootHolderResolution = Assert.IsType<InvocationExpressionSyntax>(rootHolderDeclaration.Initializer!.Value);

        IMethodSymbol rootHolderResolver = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(rootHolderResolution).Symbol);

        ITypeSymbol rootHolderArgument = Assert.Single(rootHolderResolver.TypeArguments);

        Assert.Equal(holderName, rootHolderArgument.ToDisplayString());

        Assert.Equal(compilation.Assembly.Identity, rootHolderArgument.ContainingAssembly.Identity);

        Assert.True(SymbolEqualityComparer.Default.Equals(holder.Type, rootHolderArgument));

        InvocationExpressionSyntax rootDispatch = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "ResumeAsync" });

        var rootDispatchCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(rootDispatch));

        ILocalSymbol rootCoordinatorLocal = Assert.IsAssignableFrom<ILocalReferenceOperation>(rootDispatchCall.Instance).Local;

        VariableDeclaratorSyntax rootCoordinatorDeclaration = Assert.IsType<VariableDeclaratorSyntax>(
            Assert.Single(rootCoordinatorLocal.DeclaringSyntaxReferences).GetSyntax());

        InvocationExpressionSyntax rootCoordinatorResolution = Assert.IsType<InvocationExpressionSyntax>(rootCoordinatorDeclaration.Initializer!.Value);

        IMethodSymbol rootCoordinatorResolver = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(rootCoordinatorResolution).Symbol);

        ITypeSymbol rootCoordinatorArgument = Assert.Single(rootCoordinatorResolver.TypeArguments);

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestSweepCoordinator", rootCoordinatorArgument.ToDisplayString());

        Assert.Equal(compilation.Assembly.Identity, rootCoordinatorArgument.ContainingAssembly.Identity);

        Assert.True(SymbolEqualityComparer.Default.Equals(rootCoordinatorArgument, rootDispatchCall.TargetMethod.ContainingType));

        Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(resume),
            holder.ContainingType.FindImplementationForInterfaceMember(rootDispatchCall.TargetMethod)));

        Assert.IsType<AwaitExpressionSyntax>(rootDispatch.Parent!.Parent!.Parent);

        Assert.True(rootBinding.Span.End < rootCoordinatorResolution.SpanStart
            && rootCoordinatorResolution.Span.End < rootDispatch.SpanStart);

        IArgumentOperation rootArgument = Assert.Single(rootBindCall.Arguments);

        Assert.True(rootArgument.InConversion.IsIdentity && rootArgument.OutConversion.IsIdentity);

        Assert.Equal(RefKind.None, rootArgument.Parameter!.RefKind);

        Assert.Equal("lease", Assert.IsAssignableFrom<ILocalReferenceOperation>(rootArgument.Value).Local.Name);

        if (shape == "lease-ended-after-observation")
        {
            InvocationExpressionSyntax closedAcquisition = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                call => model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "TryAcquireWorkLease" });

            var closedAcquisitionCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(closedAcquisition));

            ILocalSymbol closedGateLocal = Assert.IsAssignableFrom<ILocalReferenceOperation>(closedAcquisitionCall.Instance).Local;

            VariableDeclaratorSyntax closedGateDeclaration = Assert.IsType<VariableDeclaratorSyntax>(
                Assert.Single(closedGateLocal.DeclaringSyntaxReferences).GetSyntax());

            var closedGateCreation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(closedGateDeclaration.Initializer!.Value));

            INamedTypeSymbol closedGateType = Assert.IsAssignableFrom<INamedTypeSymbol>(closedGateCreation.Type);

            Assert.True(closedGateType.IsSealed);

            IMethodSymbol closedGateMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(Method("GrimoireConnectionAdmissionGate", "TryAcquireWorkLease")));

            Assert.True(SymbolEqualityComparer.Default.Equals(closedGateMethod,
                closedGateType.FindImplementationForInterfaceMember(closedAcquisitionCall.TargetMethod)));

            Assert.Equal(compilation.Assembly.Identity, closedGateMethod.ContainingAssembly.Identity);

            AssignmentExpressionSyntax closedAssignment = Assert.Single(Method("GrimoireConnectionAdmissionGate", "TryAcquireWorkLease")
                .DescendantNodes().OfType<AssignmentExpressionSyntax>());

            IParameterSymbol closedOutParameter = Assert.IsAssignableFrom<IParameterSymbol>(model.GetSymbolInfo(closedAssignment.Left).Symbol);

            Assert.Equal(RefKind.Out, closedOutParameter.RefKind);

            Assert.True(SymbolEqualityComparer.Default.Equals(closedOutParameter, closedGateMethod.Parameters[1]));

            var closedLeaseCreation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(closedAssignment.Right));

            INamedTypeSymbol closedLeaseType = Assert.IsAssignableFrom<INamedTypeSymbol>(closedLeaseCreation.Type);

            Assert.True(closedLeaseType.IsSealed);

            Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(Type("ForeignLease")), closedLeaseType));

            IMethodSymbol closedCleanup = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(Method("ForeignLease", "Dispose")));

            INamedTypeSymbol closedDisposable = Assert.Single(closedLeaseType.AllInterfaces,
                static contract => contract.ToDisplayString() == "System.IDisposable");

            Assert.True(SymbolEqualityComparer.Default.Equals(closedCleanup,
                closedLeaseType.FindImplementationForInterfaceMember(Assert.Single(closedDisposable.GetMembers("Dispose")))));

            IArgumentOperation closedOutArgument = Assert.Single(closedAcquisitionCall.Arguments,
                static argument => argument.Parameter!.RefKind == RefKind.Out);

            ILocalSymbol closedAdmittedLocal = Assert.IsAssignableFrom<ILocalReferenceOperation>(
                Assert.IsAssignableFrom<IDeclarationExpressionOperation>(closedOutArgument.Value).Expression).Local;

            ILocalSymbol closedRetainedLocal = Assert.IsAssignableFrom<ILocalReferenceOperation>(rootArgument.Value).Local;

            VariableDeclaratorSyntax closedRetainedDeclaration = Assert.IsType<VariableDeclaratorSyntax>(
                Assert.Single(closedRetainedLocal.DeclaringSyntaxReferences).GetSyntax());

            Assert.True(SymbolEqualityComparer.Default.Equals(closedAdmittedLocal,
                model.GetSymbolInfo(closedRetainedDeclaration.Initializer!.Value).Symbol));
        }

        InvocationExpressionSyntax[] resolutions = run.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "GetRequiredService" }).ToArray();

        Assert.Equal(2, resolutions.Length);

        Assert.Contains(rootHolderResolution, resolutions);

        Assert.Contains(rootCoordinatorResolution, resolutions);

        ISymbol? actualScope = null;

        foreach (InvocationExpressionSyntax resolution in resolutions)
        {
            IMethodSymbol resolver = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(resolution).Symbol);

            Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions", resolver.ContainingType.ToDisplayString());

            Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", resolver.ContainingAssembly.Identity.Name);

            Assert.Empty(resolver.DeclaringSyntaxReferences);

            MemberAccessExpressionSyntax provider = Assert.IsType<MemberAccessExpressionSyntax>(
                Assert.IsType<MemberAccessExpressionSyntax>(resolution.Expression).Expression);

            var providerRead = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(provider));

            Assert.Equal("Microsoft.Extensions.DependencyInjection.AsyncServiceScope", providerRead.Property.ContainingType.ToDisplayString());

            ILocalSymbol scope = Assert.IsAssignableFrom<ILocalReferenceOperation>(providerRead.Instance).Local;

            if (actualScope is null) actualScope = scope;
            else Assert.True(SymbolEqualityComparer.Default.Equals(actualScope, scope));
        }

        output.WriteLine("ORDINARY_BORROWED_SHELL_PREMISES shape=" + shape + " compilerGetterStorageAndJoinedReceiver=passed");

        HostedProducerDiscovery<string> hosted = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]);

        Assert.Empty(hosted.Diagnostics);

        HostedProducerOperationEntry root = OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep };

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], hosted, [new("Worker", [root])], []);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED"
            or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED" or "HOSTED_ROOT_UNRESOLVED");

        HostedProducerSite acquisition = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        Assert.Equal(GrimoireWorkKind.LongRestSweep, acquisition.AdmittedWorkKind);

        HostedProducerSite[] physical = result.Items.Where(static site => site.RootType == "Worker"
            && site.EnclosingType == coordinatorName && site.Callee == "System.IO.File.Delete").ToArray();

        bool expiredPureObservation = shape is "lease-ended-before-observation" or "composite-prior-disposal";

        if (expiredPureObservation)
        {
            foreach (string sendMember in new[] { "SendFirstAsync", "SendSecondAsync" })
                Assert.Contains(physical, site => site.Member == sendMember && site.WorkFrontierCapsuleId is null);
        }
        else Assert.Equal(2, physical.Length);

        foreach (HostedProducerSite send in physical)
        {
            if (admitted)
            {
                Assert.Equal(acquisition.CapsuleId, send.WorkFrontierCapsuleId);

                HostedProducerSite group = Assert.Single(result.Items, site => site.EnclosingType == coordinatorName
                    && site.Member == send.Member && site.RootType == "Worker"
                    && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

                Assert.Equal(group.CapsuleId, send.EffectFrontierCapsuleId);
            }
            else
            {
                if (expiredPureObservation)
                {
                    Assert.NotEqual(acquisition.CapsuleId, send.WorkFrontierCapsuleId);

                    if (send.WorkFrontierCapsuleId is not null) continue;
                }
                else Assert.Null(send.WorkFrontierCapsuleId);

                string coordinate = send.OperationId[send.OperationId.LastIndexOf("/site@", StringComparison.Ordinal)..];

                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    && diagnostic.Detail == send.Callee && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));
            }
        }

        if (admitted)
        {
            Assert.NotEqual(physical[0].EffectFrontierCapsuleId, physical[1].EffectFrontierCapsuleId);

            Assert.Empty(result.Diagnostics);
        }
    }
}
