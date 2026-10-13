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
    [Theory]
    [InlineData("second-tree-static-constructor")]
    [InlineData("event-field-initializer")]
    [InlineData("user-defined-conversion-initializer")]
    [InlineData("default-storage-small")]
    [InlineData("default-storage-budget")]
    public void DrainedMaintenanceEntryCensusRequiresAllConstructionBoundaries(string shape)
    {
        string source = "#nullable enable\n" + OrdinaryScopedHolderFixture("same-scoped-lease");

        const string protocol = "public interface IGrimoireWorkLease : System.IDisposable, System.IAsyncDisposable { bool TryBeginExternalEffectGroup(out System.IDisposable group); }";

        source = source.ReplaceSdkFixtureText(protocol, "public interface IGrimoireWorkLease : System.IDisposable, System.IAsyncDisposable { bool TryBeginExternalEffectGroup(out System.IDisposable group); GrimoireConnectionAdmissionGate.DrainedMaintenanceProof? ProveDrainedMaintenance(long previousGeneration) => null; } public sealed class GrimoireConnectionAdmissionGate { public sealed class DrainedMaintenanceProof { } }", StringComparison.Ordinal);

        source = source.ReplaceSdkFixtureText("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!;",
            "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = new EntryBoundaryGate();", StringComparison.Ordinal);

        ClassDeclarationSyntax oldLease = Assert.Single(CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "ForeignLease" && type.Parent is ClassDeclarationSyntax { Identifier.ValueText: "Worker" });

        int storageCount = shape == "default-storage-budget" ? 140 : shape == "default-storage-small" ? 8 : 0;

        string storage = shape switch
        {
            "second-tree-static-constructor" => "private readonly object? _value = EntryBoundaryStaticOwner.Value;",
            "event-field-initializer" => "private event System.Action? Changed = CreateCallback();",
            "user-defined-conversion-initializer" => "private readonly EntryBoundaryValue _value = (EntryBoundaryValue)0;",
            _ => string.Join("\n", Enumerable.Range(0, storageCount).Select(static index => $"private readonly object? _default{index};")),
        };

        string callback = shape == "event-field-initializer"
            ? "private static System.Action? CreateCallback() { _ = System.IO.File.Exists(\"event-field-entry\"); return null; }" : "";

        source = source.ReplaceSdkFixtureText(oldLease.ToFullString(), $$"""
            private sealed class EntryBoundaryGate : RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate
            {
                public bool TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind kind,
                    out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease)
                {
                    lease = new EntryBoundaryLease();

                    return true;
                }
            }

            private sealed class EntryBoundaryLease : RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease
            {
                {{storage}}

                {{callback}}

                public bool TryBeginExternalEffectGroup(out IDisposable group) { group = new ForeignGroup(); return true; }

                public RetroDownfall.Arcanum.Infrastructure.Data.GrimoireConnectionAdmissionGate.DrainedMaintenanceProof? ProveDrainedMaintenance(long previousGeneration)
                {
                    System.IO.File.Delete("entry-boundary-physical");

                    return null;
                }

                public void Dispose() { }

                public ValueTask DisposeAsync() => default;
            }
            """, StringComparison.Ordinal);

        if (shape == "second-tree-static-constructor")
            source += "internal static partial class EntryBoundaryStaticOwner { internal static object? Value { get; set; } = null; }";

        if (shape == "user-defined-conversion-initializer")
            source += "internal readonly struct EntryBoundaryValue { public static implicit operator EntryBoundaryValue(int value) { _ = System.IO.File.Exists(\"conversion-entry\"); return default; } }";

        source = source.ReplaceSdkFixtureText("await SendSecondAsync(ct).ConfigureAwait(false);",
            "_ = workAdmission.Lease.ProveDrainedMaintenance(0L); await SendSecondAsync(ct).ConfigureAwait(false);", StringComparison.Ordinal);

        CSharpCompilation compilation = Compile(source);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxTree? hiddenTree = null;

        if (shape == "second-tree-static-constructor")
        {
            hiddenTree = CSharpSyntaxTree.ParseText("#nullable enable\ninternal static partial class EntryBoundaryStaticOwner { static EntryBoundaryStaticOwner() { _ = System.IO.File.Exists(\"second-tree-entry\"); } }",
                path: "src/EntryBoundaryHidden.cs");

            compilation = compilation.AddSyntaxTrees(hiddenTree);
        }

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Equal(hiddenTree is null ? 1 : 2, compilation.SyntaxTrees.Count());

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax Type(string name) => Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            type => type.Identifier.ValueText == name);

        MethodDeclarationSyntax Method(string owner, string name) => Assert.Single(Type(owner).Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == name);

        ClassDeclarationSyntax leaseSyntax = Type("EntryBoundaryLease");

        INamedTypeSymbol leaseType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(leaseSyntax));

        Assert.True(leaseType.IsSealed);

        Assert.Equal(compilation.Assembly.Identity, leaseType.ContainingAssembly.Identity);

        Assert.Equal(SpecialType.System_Object, leaseType.BaseType!.SpecialType);

        IMethodSymbol constructor = Assert.Single(leaseType.InstanceConstructors);

        Assert.True(constructor.IsImplicitlyDeclared);

        Assert.Empty(constructor.Parameters);

        Assert.False(leaseType.IsGenericType);

        Assert.Null(leaseSyntax.ParameterList);

        Assert.Empty(leaseSyntax.Members.OfType<ConstructorDeclarationSyntax>());

        Assert.All(leaseSyntax.Members.OfType<PropertyDeclarationSyntax>(), static property => Assert.Null(property.Initializer));

        MethodDeclarationSyntax acquisitionBody = Method("EntryBoundaryGate", "TryAcquireWorkLease");

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

        Assert.Empty(allocation.Arguments);

        MethodDeclarationSyntax run = Method("Worker", "RunOneAsync");

        InvocationExpressionSyntax acquireSyntax = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TryAcquireWorkLease" });

        IInvocationOperation acquire = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(acquireSyntax));

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate", acquire.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(compilation.Assembly.Identity, acquire.TargetMethod.ContainingAssembly.Identity);

        INamedTypeSymbol gateType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(Type("EntryBoundaryGate")));

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

        Assert.Contains(bindSyntax.Ancestors(), static node => node is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 });

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

        MethodDeclarationSyntax implementation = Method("EntryBoundaryLease", "ProveDrainedMaintenance");

        IMethodSymbol effective = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(implementation));

        Assert.True(SymbolEqualityComparer.Default.Equals(effective, leaseType.FindImplementationForInterfaceMember(proof.TargetMethod)));

        Assert.True(SymbolEqualityComparer.Default.Equals(proof.TargetMethod.ReturnType, effective.ReturnType));

        Assert.Equal(compilation.Assembly.Identity, effective.ContainingAssembly.Identity);

        Assert.StartsWith("#nullable enable\n", source);

        void AssertSourceEffect(SemanticModel effectModel, SyntaxNode body, string literal)
        {
            InvocationExpressionSyntax invocation = Assert.Single(body.DescendantNodes().OfType<InvocationExpressionSyntax>());

            IInvocationOperation operation = Assert.IsAssignableFrom<IInvocationOperation>(effectModel.GetOperation(invocation));

            Assert.Equal("System.IO.File", operation.TargetMethod.ContainingType.ToDisplayString());

            Assert.Equal("Exists", operation.TargetMethod.Name);

            Assert.Empty(operation.TargetMethod.DeclaringSyntaxReferences);

            AssertMetadataReferenceOwns(compilation, operation.TargetMethod.ContainingAssembly);

            Assert.Equal(literal, Assert.Single(operation.Arguments).Value.ConstantValue.Value);
        }

        if (shape == "second-tree-static-constructor")
        {
            Assert.NotNull(hiddenTree);

            ClassDeclarationSyntax ownerSyntax = Type("EntryBoundaryStaticOwner");

            INamedTypeSymbol owner = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(ownerSyntax));

            Assert.True(owner.IsStatic);

            Assert.Equal(2, owner.DeclaringSyntaxReferences.Length);

            Assert.Contains(owner.DeclaringSyntaxReferences, reference => ReferenceEquals(reference.SyntaxTree, tree));

            Assert.Contains(owner.DeclaringSyntaxReferences, reference => ReferenceEquals(reference.SyntaxTree, hiddenTree));

            Assert.NotEqual(tree.FilePath, hiddenTree.FilePath);

            Assert.Empty(ownerSyntax.Members.OfType<ConstructorDeclarationSyntax>());

            PropertyDeclarationSyntax propertySyntax = Assert.Single(ownerSyntax.Members.OfType<PropertyDeclarationSyntax>());

            IPropertySymbol property = Assert.IsAssignableFrom<IPropertySymbol>(model.GetDeclaredSymbol(propertySyntax));

            Assert.True(property.IsStatic);

            Assert.NotNull(property.SetMethod);

            Assert.All(propertySyntax.AccessorList!.Accessors, static accessor =>
            {
                Assert.Null(accessor.Body);

                Assert.Null(accessor.ExpressionBody);

                Assert.True(accessor.SemicolonToken.IsKind(SyntaxKind.SemicolonToken));
            });

            Optional<object?> initialValue = model.GetConstantValue(propertySyntax.Initializer!.Value);

            Assert.True(initialValue.HasValue);

            Assert.Null(initialValue.Value);

            VariableDeclaratorSyntax stored = Assert.Single(leaseSyntax.Members.OfType<FieldDeclarationSyntax>()).Declaration.Variables.Single();

            IFieldSymbol field = Assert.IsAssignableFrom<IFieldSymbol>(model.GetDeclaredSymbol(stored));

            Assert.True(field.IsReadOnly && !field.IsStatic);

            IPropertyReferenceOperation read = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(stored.Initializer!.Value));

            Assert.Null(read.Instance);

            Assert.True(SymbolEqualityComparer.Default.Equals(property, read.Property));

            SemanticModel hiddenModel = compilation.GetSemanticModel(hiddenTree);

            ConstructorDeclarationSyntax hidden = Assert.Single(hiddenTree.GetRoot().DescendantNodes().OfType<ConstructorDeclarationSyntax>());

            IMethodSymbol hiddenConstructor = Assert.IsAssignableFrom<IMethodSymbol>(hiddenModel.GetDeclaredSymbol(hidden));

            Assert.True(hiddenConstructor.IsStatic && !hiddenConstructor.IsImplicitlyDeclared);

            Assert.Equal(MethodKind.StaticConstructor, hiddenConstructor.MethodKind);

            Assert.True(SymbolEqualityComparer.Default.Equals(owner, hiddenConstructor.ContainingType));

            Assert.Equal(compilation.Assembly.Identity, hiddenConstructor.ContainingAssembly.Identity);

            SyntaxReference hiddenReference = Assert.Single(hiddenConstructor.DeclaringSyntaxReferences);

            Assert.Same(hiddenTree, hiddenReference.SyntaxTree);

            Assert.Equal(hidden.Span, hiddenReference.Span);

            AssertSourceEffect(hiddenModel, hidden, "second-tree-entry");
        }
        else if (shape == "event-field-initializer")
        {
            EventFieldDeclarationSyntax eventSyntax = Assert.Single(leaseSyntax.Members.OfType<EventFieldDeclarationSyntax>());

            VariableDeclaratorSyntax variable = Assert.Single(eventSyntax.Declaration.Variables);

            IEventSymbol changed = Assert.IsAssignableFrom<IEventSymbol>(model.GetDeclaredSymbol(variable));

            Assert.False(changed.IsStatic);

            Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, changed.ContainingType));

            Assert.Equal(NullableAnnotation.Annotated, changed.Type.NullableAnnotation);

            Assert.True(SymbolEqualityComparer.Default.Equals(compilation.GetTypeByMetadataName("System.Action"), changed.Type));

            AssertMetadataReferenceOwns(compilation, changed.Type.ContainingAssembly);

            IInvocationOperation initializer = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(variable.Initializer!.Value));

            MethodDeclarationSyntax callbackSyntax = Method("EntryBoundaryLease", "CreateCallback");

            Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(callbackSyntax), initializer.TargetMethod));

            Assert.True(initializer.TargetMethod.IsStatic);

            Assert.Equal(compilation.Assembly.Identity, initializer.TargetMethod.ContainingAssembly.Identity);

            Assert.True(SymbolEqualityComparer.Default.Equals(changed.Type, initializer.TargetMethod.ReturnType));

            SyntaxReference callbackReference = Assert.Single(initializer.TargetMethod.DeclaringSyntaxReferences);

            Assert.Same(tree, callbackReference.SyntaxTree);

            Assert.Equal(callbackSyntax.Span, callbackReference.Span);

            AssertSourceEffect(model, callbackSyntax, "event-field-entry");
        }
        else if (shape == "user-defined-conversion-initializer")
        {
            VariableDeclaratorSyntax variable = Assert.Single(leaseSyntax.Members.OfType<FieldDeclarationSyntax>()).Declaration.Variables.Single();

            IFieldSymbol field = Assert.IsAssignableFrom<IFieldSymbol>(model.GetDeclaredSymbol(variable));

            Assert.True(field.IsReadOnly && !field.IsStatic);

            Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, field.ContainingType));

            IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(model.GetOperation(variable.Initializer!.Value));

            Assert.True(conversion.Conversion.IsUserDefined);

            IMethodSymbol converter = Assert.IsAssignableFrom<IMethodSymbol>(conversion.OperatorMethod);

            Assert.Equal(MethodKind.Conversion, converter.MethodKind);

            Assert.True(converter.IsStatic);

            Assert.Equal(compilation.Assembly.Identity, converter.ContainingAssembly.Identity);

            Assert.True(SymbolEqualityComparer.Default.Equals(field.Type, converter.ReturnType));

            Assert.Equal(SpecialType.System_Int32, Assert.Single(converter.Parameters).Type.SpecialType);

            Assert.Equal(0, conversion.Operand.ConstantValue.Value);

            ConversionOperatorDeclarationSyntax converterSyntax = Assert.IsType<ConversionOperatorDeclarationSyntax>(Assert.Single(converter.DeclaringSyntaxReferences).GetSyntax());

            Assert.Same(tree, converterSyntax.SyntaxTree);

            Assert.True(SymbolEqualityComparer.Default.Equals(converter, model.GetDeclaredSymbol(converterSyntax)));

            AssertSourceEffect(model, converterSyntax, "conversion-entry");
        }
        else
        {
            Assert.Equal(shape == "default-storage-small" ? 8 : 140, storageCount);

            FieldDeclarationSyntax[] fields = leaseSyntax.Members.OfType<FieldDeclarationSyntax>().ToArray();

            Assert.Equal(storageCount, fields.Length);

            Assert.Equal(storageCount, leaseType.GetMembers().OfType<IFieldSymbol>().Count());

            Assert.Empty(leaseSyntax.Members.OfType<EventFieldDeclarationSyntax>());

            Assert.Empty(leaseSyntax.Members.OfType<PropertyDeclarationSyntax>());

            foreach (FieldDeclarationSyntax fieldSyntax in fields)
            {
                VariableDeclaratorSyntax variable = Assert.Single(fieldSyntax.Declaration.Variables);

                Assert.Null(variable.Initializer);

                IFieldSymbol field = Assert.IsAssignableFrom<IFieldSymbol>(model.GetDeclaredSymbol(variable));

                Assert.True(field.IsReadOnly && !field.IsStatic && !field.IsConst);

                Assert.Equal(SpecialType.System_Object, field.Type.SpecialType);

                Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, field.ContainingType));
            }
        }

        InvocationExpressionSyntax physicalSyntax = Assert.Single(implementation.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation physicalOperation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(physicalSyntax));

        Assert.Equal("System.IO.File", physicalOperation.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", physicalOperation.TargetMethod.Name);

        Assert.Empty(physicalOperation.TargetMethod.DeclaringSyntaxReferences);

        Assert.True(physicalOperation.TargetMethod.IsStatic);

        Assert.True(physicalOperation.TargetMethod.ReturnsVoid);

        AssertMetadataReferenceOwns(compilation, physicalOperation.TargetMethod.ContainingAssembly);

        Assert.Equal("entry-boundary-physical", Assert.Single(physicalOperation.Arguments).Value.ConstantValue.Value);

        HostedProducerOperationEntry root = OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep };

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]), [new("Worker", [root])], []);

        HostedProducerSite acquired = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite Send(string member) => Assert.Single(result.Items, site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator"
            && site.Member == member && site.Callee == "System.IO.File.Delete");

        HostedProducerSite Group(string member) => Assert.Single(result.Items, site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator"
            && site.Member == member && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        string Coordinate(InvocationExpressionSyntax invocation) => "/site@" + invocation.SyntaxTree.FilePath + ":" + invocation.SpanStart;

        InvocationExpressionSyntax Call(string owner, string member, string name) => Assert.Single(Method(owner, member)
            .DescendantNodes().OfType<InvocationExpressionSyntax>(), call => call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == name);

        HostedProducerSite first = Send("SendFirstAsync");

        HostedProducerSite firstGroup = Group("SendFirstAsync");

        Assert.NotNull(acquired.CapsuleId);

        Assert.EndsWith(Coordinate(acquireSyntax), acquired.OperationId);

        Assert.NotNull(firstGroup.CapsuleId);

        Assert.EndsWith(Coordinate(Call("LongRestSweepCoordinator", "SendFirstAsync", "TryBeginExternalEffectGroup")), firstGroup.OperationId);

        Assert.Equal(acquired.CapsuleId, firstGroup.WorkFrontierCapsuleId);

        Assert.EndsWith(Coordinate(Call("LongRestSweepCoordinator", "SendFirstAsync", "Delete")), first.OperationId);

        Assert.Equal(acquired.CapsuleId, first.WorkFrontierCapsuleId);

        Assert.Equal(firstGroup.CapsuleId, first.EffectFrontierCapsuleId);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_ROOT_UNRESOLVED"
            or "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED" or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
            && diagnostic.Identity == tree.FilePath + ":" + proofSyntax.SpanStart);

        Console.WriteLine($"SCOPED_LEASE_ENTRY_BOUNDARY_FIRST_ANCHOR shape={shape} passed");

        string physicalCoordinate = Coordinate(physicalSyntax);

        HostedProducerSite[] physical = result.Items.Where(site => site.RootType == "Worker"
            && site.EnclosingType == "Worker.EntryBoundaryLease" && site.Member == "ProveDrainedMaintenance"
            && site.Callee == "System.IO.File.Delete" && site.OperationId.EndsWith(physicalCoordinate, StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(physical);

        bool permitted = shape == "default-storage-small";

        void OwnRefusal(HostedProducerSite site, string coordinate, string code) => Assert.Contains(result.Diagnostics,
            diagnostic => diagnostic.Code == code && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.StartsWith(site.AuthorityOperationId + "/", StringComparison.Ordinal)
                && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));

        Assert.All(physical, site =>
        {
            Assert.Equal(first.AuthorityOperationId, site.AuthorityOperationId);

            Assert.Null(site.EffectFrontierCapsuleId);

            OwnRefusal(site, physicalCoordinate, "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

            if (permitted)
            {
                Assert.Equal(acquired.CapsuleId, site.WorkFrontierCapsuleId);

                Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    && diagnostic.Identity.StartsWith(site.AuthorityOperationId + "/", StringComparison.Ordinal)
                    && diagnostic.Identity.EndsWith(physicalCoordinate, StringComparison.Ordinal));
            }
            else
            {
                Assert.Null(site.WorkFrontierCapsuleId);

                OwnRefusal(site, physicalCoordinate, "HOSTED_SITE_WORK_FRONTIER_MISSING");
            }
        });

        HostedProducerSite second = Send("SendSecondAsync");

        string secondCoordinate = Coordinate(Call("LongRestSweepCoordinator", "SendSecondAsync", "Delete"));

        Assert.EndsWith(secondCoordinate, second.OperationId);

        if (permitted)
        {
            Assert.Equal(acquired.CapsuleId, second.WorkFrontierCapsuleId);

            Assert.NotNull(Group("SendSecondAsync").CapsuleId);

            Assert.Equal(Group("SendSecondAsync").CapsuleId, second.EffectFrontierCapsuleId);

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Identity.EndsWith(secondCoordinate, StringComparison.Ordinal)
                && (diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
        }
        else
        {
            Assert.Null(second.WorkFrontierCapsuleId);

            OwnRefusal(second, secondCoordinate, "HOSTED_SITE_WORK_FRONTIER_MISSING");
        }
    }
}
