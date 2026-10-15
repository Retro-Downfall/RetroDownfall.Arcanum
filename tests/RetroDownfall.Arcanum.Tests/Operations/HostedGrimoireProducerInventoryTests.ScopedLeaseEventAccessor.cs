using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void DrainedMaintenanceCustomEventAccessorDisposalEndsItsPhysicalAuthority()
    {
        string source = "#nullable enable\n" + OrdinaryScopedHolderFixture("same-scoped-lease");

        const string protocol = "public interface IGrimoireWorkLease : System.IDisposable, System.IAsyncDisposable { bool TryBeginExternalEffectGroup(out System.IDisposable group); }";

        source = source.ReplaceSdkFixtureText(protocol, "public interface IGrimoireWorkLease : System.IDisposable, System.IAsyncDisposable { bool TryBeginExternalEffectGroup(out System.IDisposable group); GrimoireConnectionAdmissionGate.DrainedMaintenanceProof? ProveDrainedMaintenance(long previousGeneration) => null; } public sealed class GrimoireConnectionAdmissionGate : IGrimoireConnectionAdmissionGate { public sealed class DrainedMaintenanceProof { } public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease) { lease = new global::Worker.EventDrainedLease(); return true; } }", StringComparison.Ordinal);

        source = source.ReplaceSdkFixtureText("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!;",
            "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = new RetroDownfall.Arcanum.Infrastructure.Data.GrimoireConnectionAdmissionGate();", StringComparison.Ordinal);

        ClassDeclarationSyntax oldLease = Assert.Single(CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "ForeignLease" && type.Parent is ClassDeclarationSyntax { Identifier.ValueText: "Worker" });

        source = source.ReplaceSdkFixtureText(oldLease.ToFullString(), """
            internal sealed class EventDrainedLease : RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease
            {
                public bool TryBeginExternalEffectGroup(out IDisposable group) { group = new ForeignGroup(); return true; }

                public RetroDownfall.Arcanum.Infrastructure.Data.GrimoireConnectionAdmissionGate.DrainedMaintenanceProof? ProveDrainedMaintenance(long previousGeneration)
                {
                    this.Changed += null;

                    return null;
                }

                private event System.Action? Changed
                {
                    add
                    {
                        this.Dispose();

                        System.IO.File.Delete("inside-drained-event-add");
                    }

                    remove { }
                }

                public void Dispose() { }

                public ValueTask DisposeAsync() => default;
            }
            """, StringComparison.Ordinal);

        source = source.ReplaceSdkFixtureText("await SendSecondAsync(ct).ConfigureAwait(false);",
            "_ = workAdmission.Lease.ProveDrainedMaintenance(0L); await SendSecondAsync(ct).ConfigureAwait(false);", StringComparison.Ordinal);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax Type(string name) => Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            type => type.Identifier.ValueText == name);

        MethodDeclarationSyntax Method(string owner, string name) => Assert.Single(Type(owner).Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == name);

        ClassDeclarationSyntax leaseSyntax = Type("EventDrainedLease");

        INamedTypeSymbol leaseType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(leaseSyntax));

        Assert.True(leaseType.IsSealed);

        Assert.Equal(compilation.Assembly.Identity, leaseType.ContainingAssembly.Identity);

        Assert.Equal(SpecialType.System_Object, leaseType.BaseType!.SpecialType);

        IMethodSymbol constructor = Assert.Single(leaseType.InstanceConstructors);

        Assert.True(constructor.IsImplicitlyDeclared);

        Assert.Empty(constructor.Parameters);

        Assert.Empty(leaseSyntax.Members.OfType<BaseFieldDeclarationSyntax>());

        Assert.Empty(leaseSyntax.Members.OfType<ConstructorDeclarationSyntax>());

        Assert.All(leaseSyntax.Members.OfType<PropertyDeclarationSyntax>(), static property => Assert.Null(property.Initializer));

        MethodDeclarationSyntax acquisitionBody = Method("GrimoireConnectionAdmissionGate", "TryAcquireWorkLease");

        IMethodSymbol gateMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(acquisitionBody));

        ISimpleAssignmentOperation assignment = Assert.IsAssignableFrom<ISimpleAssignmentOperation>(model.GetOperation(
            Assert.Single(acquisitionBody.DescendantNodes().OfType<AssignmentExpressionSyntax>())));

        Assert.True(SymbolEqualityComparer.Default.Equals(gateMethod.Parameters[1],
            Assert.IsAssignableFrom<IParameterReferenceOperation>(assignment.Target).Parameter));

        Assert.Equal(RefKind.Out, gateMethod.Parameters[1].RefKind);

        IConversionOperation leaseUpcast = Assert.IsAssignableFrom<IConversionOperation>(assignment.Value);

        Assert.Null(leaseUpcast.OperatorMethod);

        Assert.True(leaseUpcast.Conversion.IsReference);

        IObjectCreationOperation allocation = Assert.IsAssignableFrom<IObjectCreationOperation>(leaseUpcast.Operand);

        Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, allocation.Type));

        Assert.True(SymbolEqualityComparer.Default.Equals(constructor, allocation.Constructor));

        Assert.Null(allocation.Initializer);

        MethodDeclarationSyntax run = Method("Worker", "RunOneAsync");

        InvocationExpressionSyntax acquireSyntax = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TryAcquireWorkLease" });

        IInvocationOperation acquire = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(acquireSyntax));

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate", acquire.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(compilation.Assembly.Identity, acquire.TargetMethod.ContainingAssembly.Identity);

        INamedTypeSymbol gateType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(Type("GrimoireConnectionAdmissionGate")));

        Assert.True(gateType.IsSealed);

        Assert.True(SymbolEqualityComparer.Default.Equals(gateMethod, gateType.FindImplementationForInterfaceMember(acquire.TargetMethod)));

        VariableDeclaratorSyntax gateLocalSyntax = Assert.Single(run.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "gate");

        Assert.True(SymbolEqualityComparer.Default.Equals(gateType,
            Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(gateLocalSyntax.Initializer!.Value)).Type));

        Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(gateLocalSyntax),
            Assert.IsAssignableFrom<ILocalReferenceOperation>(acquire.Instance).Local));

        ILocalSymbol admitted = Assert.IsAssignableFrom<ILocalReferenceOperation>(
            Assert.IsAssignableFrom<IDeclarationExpressionOperation>(acquire.Arguments[1].Value).Expression).Local;

        VariableDeclaratorSyntax retained = Assert.Single(run.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "lease");

        Assert.True(SymbolEqualityComparer.Default.Equals(admitted, model.GetSymbolInfo(retained.Initializer!.Value).Symbol));

        Assert.True(retained.Parent!.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0, AwaitKeyword.RawKind: not 0 });

        InvocationExpressionSyntax bindSyntax = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Bind" });

        IInvocationOperation bind = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(bindSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(retained),
            Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(bind.Arguments).Value).Local));

        InvocationExpressionSyntax dispatchSyntax = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ResumeAsync" });

        Assert.True(bindSyntax.Span.End < dispatchSyntax.SpanStart);

        Assert.Contains(dispatchSyntax.Ancestors(), static node => node is AwaitExpressionSyntax);

        MethodDeclarationSyntax resume = Method("LongRestSweepCoordinator", "ResumeAsync");

        InvocationExpressionSyntax proofSyntax = Assert.Single(resume.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ProveDrainedMaintenance" });

        IInvocationOperation proof = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(proofSyntax));

        Assert.True(proof.IsVirtual && proof.TargetMethod.IsVirtual && !proof.TargetMethod.IsAbstract);

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", proof.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(compilation.Assembly.Identity, proof.TargetMethod.ContainingAssembly.Identity);

        IPropertyReferenceOperation holderRead = Assert.IsAssignableFrom<IPropertyReferenceOperation>(proof.Instance);

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission", holderRead.Property.ContainingType.ToDisplayString());

        IParameterSymbol holder = Assert.IsAssignableFrom<IParameterSymbol>(model.GetDeclaredSymbol(
            Assert.Single(Type("LongRestSweepCoordinator").ParameterList!.Parameters)));

        Assert.True(SymbolEqualityComparer.Default.Equals(holder, Assert.IsAssignableFrom<IParameterReferenceOperation>(holderRead.Instance).Parameter));

        InvocationExpressionSyntax firstSend = Assert.Single(resume.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "SendFirstAsync");

        Assert.True(firstSend.Span.End < proofSyntax.SpanStart);

        MethodDeclarationSyntax implementation = Method("EventDrainedLease", "ProveDrainedMaintenance");

        IMethodSymbol effective = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(implementation));

        Assert.True(SymbolEqualityComparer.Default.Equals(effective, leaseType.FindImplementationForInterfaceMember(proof.TargetMethod)));

        Assert.True(SymbolEqualityComparer.Default.Equals(proof.TargetMethod.ReturnType, effective.ReturnType));

        Assert.Equal(compilation.Assembly.Identity, effective.ContainingAssembly.Identity);

        EventDeclarationSyntax eventSyntax = Assert.Single(leaseSyntax.Members.OfType<EventDeclarationSyntax>());

        Assert.IsType<NullableTypeSyntax>(eventSyntax.Type);

        Assert.StartsWith("#nullable enable\n", source);

        IEventSymbol changed = Assert.IsAssignableFrom<IEventSymbol>(model.GetDeclaredSymbol(eventSyntax));

        Assert.Equal("Changed", changed.Name);

        Assert.False(changed.IsStatic || changed.IsVirtual);

        Assert.Equal(NullableAnnotation.Annotated, changed.Type.NullableAnnotation);

        Assert.Equal(TypeKind.Delegate, changed.Type.TypeKind);

        INamedTypeSymbol action = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("System.Action"));

        Assert.True(SymbolEqualityComparer.Default.Equals(action, changed.Type));

        AssertMetadataReferenceOwns(compilation, action.ContainingAssembly);

        Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, changed.ContainingType));

        Assert.Equal(compilation.Assembly.Identity, changed.ContainingAssembly.Identity);

        IMethodSymbol add = Assert.IsAssignableFrom<IMethodSymbol>(changed.AddMethod);

        Assert.NotNull(changed.RemoveMethod);

        Assert.Equal(MethodKind.EventAdd, add.MethodKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(changed, add.AssociatedSymbol));

        Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, add.ContainingType));

        Assert.False(add.IsStatic || add.IsVirtual);

        Assert.True(add.ReturnsVoid);

        IParameterSymbol handler = Assert.Single(add.Parameters);

        Assert.Equal(RefKind.None, handler.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(changed.Type, handler.Type));

        AccessorDeclarationSyntax addSyntax = Assert.IsType<AccessorDeclarationSyntax>(Assert.Single(add.DeclaringSyntaxReferences).GetSyntax());

        Assert.True(addSyntax.IsKind(SyntaxKind.AddAccessorDeclaration));

        Assert.True(SymbolEqualityComparer.Default.Equals(add, model.GetDeclaredSymbol(addSyntax)));

        Assert.Equal(tree, addSyntax.SyntaxTree);

        Assert.Equal(eventSyntax.Span, addSyntax.Parent!.Parent!.Span);

        Assert.NotNull(addSyntax.Body);

        Assert.Null(addSyntax.ExpressionBody);

        Assert.Empty(addSyntax.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>());

        Assert.Empty(addSyntax.DescendantNodes().OfType<LocalFunctionStatementSyntax>());

        AssignmentExpressionSyntax mutationSyntax = Assert.Single(implementation.DescendantNodes().OfType<AssignmentExpressionSyntax>());

        Assert.True(mutationSyntax.IsKind(SyntaxKind.AddAssignmentExpression));

        Assert.True(mutationSyntax.Right.IsKind(SyntaxKind.NullLiteralExpression));

        IEventAssignmentOperation mutation = Assert.IsAssignableFrom<IEventAssignmentOperation>(model.GetOperation(mutationSyntax));

        Assert.True(mutation.Adds);

        IEventReferenceOperation eventReference = Assert.IsAssignableFrom<IEventReferenceOperation>(mutation.EventReference);

        Assert.True(SymbolEqualityComparer.Default.Equals(changed, eventReference.Event));

        Assert.True(SymbolEqualityComparer.Default.Equals(add, eventReference.Event.AddMethod));

        Assert.Equal(compilation.Assembly.Identity, eventReference.Event.ContainingAssembly.Identity);

        IInstanceReferenceOperation eventReceiver = Assert.IsAssignableFrom<IInstanceReferenceOperation>(eventReference.Instance);

        Assert.Equal(InstanceReferenceKind.ContainingTypeInstance, eventReceiver.ReferenceKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, eventReceiver.Type));

        Assert.True(implementation.Body!.Span.Contains(mutationSyntax.Span));

        IConversionOperation nullHandler = Assert.IsAssignableFrom<IConversionOperation>(mutation.HandlerValue);

        Assert.Null(nullHandler.OperatorMethod);

        Assert.True(SymbolEqualityComparer.Default.Equals(changed.Type, nullHandler.Type));

        ILiteralOperation nullLiteral = Assert.IsAssignableFrom<ILiteralOperation>(nullHandler.Operand);

        Assert.True(nullLiteral.ConstantValue.HasValue);

        Assert.Null(nullLiteral.ConstantValue.Value);

        InvocationExpressionSyntax disposeSyntax = Assert.Single(addSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "this.Dispose");

        IInvocationOperation disposeCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(disposeSyntax));

        IMethodSymbol dispose = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(Method("EventDrainedLease", "Dispose")));

        Assert.True(SymbolEqualityComparer.Default.Equals(dispose, disposeCall.TargetMethod));

        Assert.Equal(compilation.Assembly.Identity, dispose.ContainingAssembly.Identity);

        IInstanceReferenceOperation disposedReceiver = Assert.IsAssignableFrom<IInstanceReferenceOperation>(disposeCall.Instance);

        Assert.Equal(InstanceReferenceKind.ContainingTypeInstance, disposedReceiver.ReferenceKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, disposedReceiver.Type));

        INamedTypeSymbol disposable = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("System.IDisposable"));

        IMethodSymbol disposeSlot = Assert.Single(disposable.GetMembers("Dispose").OfType<IMethodSymbol>());

        AssertMetadataReferenceOwns(compilation, disposeSlot.ContainingAssembly);

        Assert.True(SymbolEqualityComparer.Default.Equals(dispose, leaseType.FindImplementationForInterfaceMember(disposeSlot)));

        InvocationExpressionSyntax insideSyntax = Assert.Single(addSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "System.IO.File.Delete");

        Assert.True(disposeSyntax.Span.End < insideSyntax.SpanStart);

        Assert.True(addSyntax.Body!.Span.Contains(disposeSyntax.Span) && addSyntax.Body.Span.Contains(insideSyntax.Span));

        IInvocationOperation inside = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(insideSyntax));

        Assert.Equal("System.IO.File", inside.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", inside.TargetMethod.Name);

        Assert.Empty(inside.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, inside.TargetMethod.ContainingAssembly);

        HostedProducerOperationEntry root = OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep };

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]), [new("Worker", [root])], []);

        HostedProducerSite acquired = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite first = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator"
            && site.Member == "SendFirstAsync" && site.Callee == "System.IO.File.Delete");

        HostedProducerSite firstGroup = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator"
            && site.Member == "SendFirstAsync" && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        Assert.NotNull(acquired.CapsuleId);

        Assert.NotNull(firstGroup.CapsuleId);

        Assert.Equal(acquired.CapsuleId, first.WorkFrontierCapsuleId);

        Assert.Equal(firstGroup.CapsuleId, first.EffectFrontierCapsuleId);

        foreach (InvocationExpressionSyntax physicalSyntax in new[] { insideSyntax })
        {
            string coordinate = "/site@" + tree.FilePath + ":" + physicalSyntax.SpanStart;

            HostedProducerSite[] physical = result.Items.Where(site => site.Callee == "System.IO.File.Delete"
                && site.OperationId.EndsWith(coordinate, StringComparison.Ordinal)).ToArray();

            Assert.NotEmpty(physical);

            Assert.All(physical, site =>
            {
                Assert.Equal(first.AuthorityOperationId, site.AuthorityOperationId);

                Assert.Equal("Worker.EventDrainedLease", site.EnclosingType);

                Assert.Equal("add_Changed", site.Member);

                Assert.Null(site.WorkFrontierCapsuleId);

                Assert.Null(site.EffectFrontierCapsuleId);
            });

            foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == "System.IO.File.Delete"
                    && diagnostic.Identity.StartsWith(first.AuthorityOperationId + "/", StringComparison.Ordinal)
                    && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));
        }
    }
}
