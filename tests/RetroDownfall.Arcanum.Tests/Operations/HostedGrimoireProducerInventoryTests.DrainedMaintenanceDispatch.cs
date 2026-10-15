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
    [InlineData("benign")]
    [InlineData("override-effect")]
    [InlineData("unknown-receiver")]
    [InlineData("foreign-holder")]
    [InlineData("self-dispose-explicit")]
    [InlineData("self-dispose-implicit")]
    [InlineData("local-self-dispose")]
    [InlineData("detached-self-dispose")]
    [InlineData("foreign-instance-group")]
    [InlineData("field-initializer")]
    [InlineData("property-initializer")]
    [InlineData("static-property-sibling-initializer")]
    [InlineData("static-property-partial-constructor")]
    public void DrainedMaintenanceInterfaceDispatchRequiresTheActualBoundLease(string shape)
    {
        string source = OrdinaryScopedHolderFixture("same-scoped-lease");

        const string protocol = "public interface IGrimoireWorkLease : System.IDisposable, System.IAsyncDisposable { bool TryBeginExternalEffectGroup(out System.IDisposable group); }";

        source = source.ReplaceSdkFixtureText(protocol, "public interface IGrimoireWorkLease : System.IDisposable, System.IAsyncDisposable { bool TryBeginExternalEffectGroup(out System.IDisposable group); GrimoireConnectionAdmissionGate.DrainedMaintenanceProof? ProveDrainedMaintenance(long previousGeneration) => null; } public sealed class GrimoireConnectionAdmissionGate : IGrimoireConnectionAdmissionGate { public sealed class DrainedMaintenanceProof { } public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease) { lease = new global::Worker.DrainedWorkLease(); return true; } }", StringComparison.Ordinal);

        source = source.ReplaceSdkFixtureText("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!;",
            "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = new RetroDownfall.Arcanum.Infrastructure.Data.GrimoireConnectionAdmissionGate();", StringComparison.Ordinal);

        ClassDeclarationSyntax oldLease = Assert.Single(CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "ForeignLease" && type.Parent is ClassDeclarationSyntax { Identifier.ValueText: "Worker" });

        string effect = shape == "benign" ? "" : "System.IO.File.Delete(\"drained-override-effect\");";

        if (shape == "foreign-instance-group") effect = "using (foreignGroup) { " + effect + " }";

        string storage = shape == "foreign-instance-group"
            ? "private readonly RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease _foreignLease = global::UnknownDrainedReceiver.Value;" : "";

        if (shape == "field-initializer")
            storage = "private readonly bool _initialized = System.IO.File.Exists(\"lease-field-initializer\");";

        if (shape == "property-initializer")
            storage = "private bool Initialized { get; } = System.IO.File.Exists(\"lease-property-initializer\");";

        if (shape is "static-property-sibling-initializer" or "static-property-partial-constructor")
            storage = "private readonly RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease _foreignLease = global::UnknownDrainedReceiver.Value;";

        string release = shape switch
        {
            "self-dispose-explicit" => "this.Dispose();",
            "self-dispose-implicit" => "Dispose();",
            "local-self-dispose" => "void Drop() { this.Dispose(); } Drop();",
            "detached-self-dispose" => "_ = Task.Run(() => this.Dispose());",
            "foreign-instance-group" => "if (!this._foreignLease.TryBeginExternalEffectGroup(out IDisposable foreignGroup)) return null;",
            _ => "",
        };

        source = source.ReplaceSdkFixtureText(oldLease.ToFullString(), $$"""
            internal sealed class DrainedWorkLease : RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease
            {
                {{storage}}

                public bool TryBeginExternalEffectGroup(out IDisposable group) { group = new ForeignGroup(); return true; }

                public RetroDownfall.Arcanum.Infrastructure.Data.GrimoireConnectionAdmissionGate.DrainedMaintenanceProof? ProveDrainedMaintenance(long previousGeneration)
                {
                    {{release}}

                    {{effect}}

                    return null;
                }

                public void Dispose() { }

                public ValueTask DisposeAsync() => default;
            }
            """, StringComparison.Ordinal);

        string prelude = shape == "foreign-holder"
            ? "var other = new global::ForeignHolder { Lease = global::UnknownDrainedReceiver.Value }; " : "";

        string receiver = shape switch
        {
            "unknown-receiver" => "global::UnknownDrainedReceiver.Value",
            "foreign-holder" => "other.Lease",
            _ => "workAdmission.Lease",
        };

        source = source.ReplaceSdkFixtureText("await SendSecondAsync(ct).ConfigureAwait(false);",
            prelude + "_ = " + receiver + ".ProveDrainedMaintenance(0L); await SendSecondAsync(ct).ConfigureAwait(false);", StringComparison.Ordinal)
            + "internal static class UnknownDrainedReceiver { internal static RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease Value { get; set; } = null!; }"
            + "internal sealed class ForeignHolder { internal RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease Lease { get; set; } = null!; }";

        if (shape == "static-property-sibling-initializer")
            source = source.ReplaceSdkFixtureText("internal static class UnknownDrainedReceiver { ",
                "internal static class UnknownDrainedReceiver { private static readonly bool Hidden = System.IO.File.Exists(\"static-property-sibling-initializer\"); ", StringComparison.Ordinal);

        if (shape == "static-property-partial-constructor")
            source = source.ReplaceSdkFixtureText("internal static class UnknownDrainedReceiver { ",
                "internal static partial class UnknownDrainedReceiver { ", StringComparison.Ordinal)
                + "internal static partial class UnknownDrainedReceiver { static UnknownDrainedReceiver() { _ = System.IO.File.Exists(\"static-property-partial-constructor\"); } }";

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax Type(string name) => Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            type => type.Identifier.ValueText == name);

        MethodDeclarationSyntax Method(string owner, string name) => Assert.Single(Type(owner).Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == name);

        MethodDeclarationSyntax run = Method("Worker", "RunOneAsync");

        InvocationExpressionSyntax acquisitionSyntax = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TryAcquireWorkLease" });

        IInvocationOperation acquisition = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(acquisitionSyntax));

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate", acquisition.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(compilation.Assembly.Identity, acquisition.TargetMethod.ContainingAssembly.Identity);

        INamedTypeSymbol gateType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(Type("GrimoireConnectionAdmissionGate")));

        Assert.True(gateType.IsSealed);

        IMethodSymbol gateImplementation = Assert.IsAssignableFrom<IMethodSymbol>(gateType.FindImplementationForInterfaceMember(acquisition.TargetMethod));

        Assert.True(SymbolEqualityComparer.Default.Equals(gateImplementation, model.GetDeclaredSymbol(Method("GrimoireConnectionAdmissionGate", "TryAcquireWorkLease"))));

        VariableDeclaratorSyntax gateSyntax = Assert.Single(run.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "gate");

        IObjectCreationOperation gateAllocation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(gateSyntax.Initializer!.Value));

        Assert.True(SymbolEqualityComparer.Default.Equals(gateType, gateAllocation.Type));

        ILocalSymbol gateLocal = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(gateSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(gateLocal, Assert.IsAssignableFrom<ILocalReferenceOperation>(acquisition.Instance).Local));

        MethodDeclarationSyntax gateBody = Method("GrimoireConnectionAdmissionGate", "TryAcquireWorkLease");

        ISimpleAssignmentOperation outAssignment = Assert.IsAssignableFrom<ISimpleAssignmentOperation>(model.GetOperation(
            Assert.Single(gateBody.DescendantNodes().OfType<AssignmentExpressionSyntax>())));

        Assert.True(SymbolEqualityComparer.Default.Equals(gateImplementation.Parameters[1],
            Assert.IsAssignableFrom<IParameterReferenceOperation>(outAssignment.Target).Parameter));

        Assert.Equal(RefKind.Out, gateImplementation.Parameters[1].RefKind);

        INamedTypeSymbol leaseType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(Type("DrainedWorkLease")));

        Assert.True(leaseType.IsSealed);

        IConversionOperation leaseUpcast = Assert.IsAssignableFrom<IConversionOperation>(outAssignment.Value);

        Assert.Null(leaseUpcast.OperatorMethod);

        Assert.True(leaseUpcast.Conversion.IsReference);

        Assert.True(SymbolEqualityComparer.Default.Equals(leaseType,
            Assert.IsAssignableFrom<IObjectCreationOperation>(leaseUpcast.Operand).Type));

        if (shape is "field-initializer" or "property-initializer")
        {
            IObjectCreationOperation leaseAllocation = Assert.IsAssignableFrom<IObjectCreationOperation>(leaseUpcast.Operand);

            IMethodSymbol implicitConstructor = Assert.Single(leaseType.InstanceConstructors);

            Assert.True(implicitConstructor.IsImplicitlyDeclared && !implicitConstructor.IsStatic);

            Assert.Empty(implicitConstructor.Parameters);

            Assert.True(SymbolEqualityComparer.Default.Equals(implicitConstructor, leaseAllocation.Constructor));

            Assert.Null(leaseAllocation.Initializer);

            Assert.Empty(leaseAllocation.Arguments);

            Assert.False(leaseType.IsGenericType);

            Assert.DoesNotContain(Type("DrainedWorkLease").Members, static member => member is ConstructorDeclarationSyntax);

            EqualsValueClauseSyntax initializer;

            if (shape == "field-initializer")
            {
                FieldDeclarationSyntax fieldSyntax = Assert.Single(Type("DrainedWorkLease").Members.OfType<FieldDeclarationSyntax>());

                VariableDeclaratorSyntax variable = Assert.Single(fieldSyntax.Declaration.Variables);

                IFieldSymbol field = Assert.IsAssignableFrom<IFieldSymbol>(model.GetDeclaredSymbol(variable));

                Assert.True(field.IsReadOnly && !field.IsStatic);

                Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, field.ContainingType));

                Assert.Equal(SpecialType.System_Boolean, field.Type.SpecialType);

                initializer = Assert.IsType<EqualsValueClauseSyntax>(variable.Initializer);
            }
            else
            {
                PropertyDeclarationSyntax propertySyntax = Assert.Single(Type("DrainedWorkLease").Members.OfType<PropertyDeclarationSyntax>());

                IPropertySymbol property = Assert.IsAssignableFrom<IPropertySymbol>(model.GetDeclaredSymbol(propertySyntax));

                Assert.False(property.IsStatic);

                Assert.Null(property.SetMethod);

                Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, property.ContainingType));

                Assert.Equal(SpecialType.System_Boolean, property.Type.SpecialType);

                Assert.All(propertySyntax.AccessorList!.Accessors, static accessor =>
                {
                    Assert.Null(accessor.Body);

                    Assert.Null(accessor.ExpressionBody);
                });

                initializer = Assert.IsType<EqualsValueClauseSyntax>(propertySyntax.Initializer);
            }

            SyntaxReference leaseDeclaration = Assert.Single(leaseType.DeclaringSyntaxReferences);

            Assert.Same(tree, leaseDeclaration.SyntaxTree);

            Assert.True(leaseDeclaration.Span.Contains(initializer.Span));

            IInvocationOperation initialization = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(initializer.Value));

            Assert.Equal("System.IO.File", initialization.TargetMethod.ContainingType.ToDisplayString());

            Assert.Equal("Exists", initialization.TargetMethod.Name);

            Assert.True(initialization.TargetMethod.IsStatic);

            AssertMetadataReferenceOwns(compilation, initialization.TargetMethod.ContainingAssembly);

            Assert.Equal(shape == "field-initializer" ? "lease-field-initializer" : "lease-property-initializer",
                Assert.Single(initialization.Arguments).Value.ConstantValue.Value);

            Assert.Equal("System.IO.File.Delete", Assert.Single(Method("DrainedWorkLease", "ProveDrainedMaintenance")
                .DescendantNodes().OfType<InvocationExpressionSyntax>()).Expression.ToString());

            output.WriteLine("SCOPED_LEASE_INITIALIZER_PREMISES shape=" + shape
                + " implicitConstructorExactAcquiredAllocationDeclarationOwnerAndExecutableInitializer=passed");
        }

        if (shape is "static-property-sibling-initializer" or "static-property-partial-constructor")
        {
            IObjectCreationOperation leaseAllocation = Assert.IsAssignableFrom<IObjectCreationOperation>(leaseUpcast.Operand);

            IMethodSymbol implicitConstructor = Assert.Single(leaseType.InstanceConstructors);

            Assert.True(implicitConstructor.IsImplicitlyDeclared && !implicitConstructor.IsStatic);

            Assert.Empty(implicitConstructor.Parameters);

            Assert.True(SymbolEqualityComparer.Default.Equals(implicitConstructor, leaseAllocation.Constructor));

            Assert.Null(leaseAllocation.Initializer);

            Assert.Empty(leaseAllocation.Arguments);

            Assert.False(leaseType.IsGenericType);

            Assert.DoesNotContain(Type("DrainedWorkLease").Members, static member => member is ConstructorDeclarationSyntax);

            VariableDeclaratorSyntax storageSyntax = Assert.Single(Assert.Single(Type("DrainedWorkLease")
                .Members.OfType<FieldDeclarationSyntax>()).Declaration.Variables);

            IFieldSymbol storageSymbol = Assert.IsAssignableFrom<IFieldSymbol>(model.GetDeclaredSymbol(storageSyntax));

            Assert.True(storageSymbol.IsReadOnly && !storageSymbol.IsStatic);

            Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, storageSymbol.ContainingType));

            IPropertyReferenceOperation passiveRead = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(storageSyntax.Initializer!.Value));

            Assert.Null(passiveRead.Instance);

            Assert.Empty(passiveRead.Arguments);

            Assert.True(passiveRead.Property.IsStatic);

            Assert.NotNull(passiveRead.Property.GetMethod);

            Assert.NotNull(passiveRead.Property.SetMethod);

            Assert.Equal(compilation.Assembly.Identity, passiveRead.Property.ContainingAssembly.Identity);

            Assert.True(SymbolEqualityComparer.Default.Equals(storageSymbol.Type, passiveRead.Type));

            PropertyDeclarationSyntax passiveProperty = Assert.IsType<PropertyDeclarationSyntax>(Assert.Single(passiveRead.Property.DeclaringSyntaxReferences).GetSyntax());

            Assert.True(SymbolEqualityComparer.Default.Equals(passiveRead.Property, model.GetDeclaredSymbol(passiveProperty)));

            Assert.All(passiveProperty.AccessorList!.Accessors, static accessor =>
            {
                Assert.Null(accessor.Body);

                Assert.Null(accessor.ExpressionBody);
            });

            PostfixUnaryExpressionSyntax nullInitializer = Assert.IsType<PostfixUnaryExpressionSyntax>(passiveProperty.Initializer!.Value);

            Assert.True(nullInitializer.IsKind(SyntaxKind.SuppressNullableWarningExpression));

            Assert.True(nullInitializer.Operand.IsKind(SyntaxKind.NullLiteralExpression));

            INamedTypeSymbol passiveOwner = passiveRead.Property.ContainingType;

            Assert.True(passiveOwner.IsStatic && !passiveOwner.IsGenericType);

            ClassDeclarationSyntax[] ownerParts = passiveOwner.DeclaringSyntaxReferences.Select(reference =>
                Assert.IsType<ClassDeclarationSyntax>(reference.GetSyntax())).ToArray();

            Assert.All(ownerParts, part =>
            {
                Assert.Same(tree, part.SyntaxTree);

                Assert.True(SymbolEqualityComparer.Default.Equals(passiveOwner, model.GetDeclaredSymbol(part)));
            });

            InvocationExpressionSyntax hiddenCall;

            if (shape == "static-property-sibling-initializer")
            {
                ClassDeclarationSyntax ownerPart = Assert.Single(ownerParts);

                Assert.Equal(ownerPart.Span, passiveProperty.Parent!.Span);

                VariableDeclaratorSyntax hidden = Assert.Single(Assert.Single(ownerPart.Members.OfType<FieldDeclarationSyntax>()).Declaration.Variables);

                IFieldSymbol hiddenSymbol = Assert.IsAssignableFrom<IFieldSymbol>(model.GetDeclaredSymbol(hidden));

                Assert.True(hiddenSymbol.IsStatic && hiddenSymbol.IsReadOnly);

                Assert.True(SymbolEqualityComparer.Default.Equals(passiveOwner, hiddenSymbol.ContainingType));

                hiddenCall = Assert.IsType<InvocationExpressionSyntax>(hidden.Initializer!.Value);
            }
            else
            {
                Assert.Equal(2, ownerParts.Length);

                Assert.All(ownerParts, static part => Assert.Contains(part.Modifiers, static token => token.IsKind(SyntaxKind.PartialKeyword)));

                ConstructorDeclarationSyntax constructor = Assert.Single(ownerParts.SelectMany(static part => part.Members.OfType<ConstructorDeclarationSyntax>()));

                Assert.NotEqual(passiveProperty.Parent!.Span, constructor.Parent!.Span);

                IMethodSymbol constructorSymbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(constructor));

                Assert.Equal(MethodKind.StaticConstructor, constructorSymbol.MethodKind);

                Assert.True(constructorSymbol.IsStatic && !constructorSymbol.IsImplicitlyDeclared);

                Assert.True(SymbolEqualityComparer.Default.Equals(passiveOwner, constructorSymbol.ContainingType));

                hiddenCall = Assert.Single(constructor.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>());
            }

            IInvocationOperation hiddenExecution = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(hiddenCall));

            Assert.Equal("System.IO.File", hiddenExecution.TargetMethod.ContainingType.ToDisplayString());

            Assert.Equal("Exists", hiddenExecution.TargetMethod.Name);

            Assert.True(hiddenExecution.TargetMethod.IsStatic);

            AssertMetadataReferenceOwns(compilation, hiddenExecution.TargetMethod.ContainingAssembly);

            Assert.Equal(shape, Assert.Single(hiddenExecution.Arguments).Value.ConstantValue.Value);

            Assert.Equal("System.IO.File.Delete", Assert.Single(Method("DrainedWorkLease", "ProveDrainedMaintenance")
                .DescendantNodes().OfType<InvocationExpressionSyntax>()).Expression.ToString());

            output.WriteLine("SCOPED_LEASE_STATIC_INITIALIZER_PREMISES shape=" + shape
                + " implicitConstructorExactAcquiredAllocationPassivePropertyAndHiddenExecution=passed");
        }

        VariableDeclaratorSyntax retainedLease = Assert.Single(run.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "lease");

        Assert.True(retainedLease.Parent!.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0, AwaitKeyword.RawKind: not 0 });

        IDeclarationExpressionOperation outDeclaration = Assert.IsAssignableFrom<IDeclarationExpressionOperation>(acquisition.Arguments[1].Value);

        ILocalSymbol admitted = Assert.IsAssignableFrom<ILocalReferenceOperation>(outDeclaration.Expression).Local;

        Assert.True(SymbolEqualityComparer.Default.Equals(admitted, model.GetSymbolInfo(retainedLease.Initializer!.Value).Symbol));

        InvocationExpressionSyntax bindSyntax = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Bind" });

        IInvocationOperation bind = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(bindSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(retainedLease),
            Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(bind.Arguments).Value).Local));

        ILocalSymbol scope = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(Assert.Single(run.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "scope")));

        foreach (string localName in new[] { "holder", "coordinator" })
        {
            VariableDeclaratorSyntax resolved = Assert.Single(run.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
                variable => variable.Identifier.ValueText == localName);

            InvocationExpressionSyntax resolutionSyntax = Assert.IsType<InvocationExpressionSyntax>(resolved.Initializer!.Value);

            IInvocationOperation resolution = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(resolutionSyntax));

            Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions", resolution.TargetMethod.ContainingType.ToDisplayString());

            Assert.Empty(resolution.TargetMethod.DeclaringSyntaxReferences);

            AssertMetadataReferenceOwns(compilation, resolution.TargetMethod.ContainingAssembly);

            MemberAccessExpressionSyntax provider = Assert.IsType<MemberAccessExpressionSyntax>(
                Assert.IsType<MemberAccessExpressionSyntax>(resolutionSyntax.Expression).Expression);

            Assert.Equal("ServiceProvider", provider.Name.Identifier.ValueText);

            Assert.True(SymbolEqualityComparer.Default.Equals(scope, model.GetSymbolInfo(provider.Expression).Symbol));

            if (localName == "holder")
                Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(resolved),
                    Assert.IsAssignableFrom<ILocalReferenceOperation>(bind.Instance).Local));
        }

        InvocationExpressionSyntax dispatch = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ResumeAsync" });

        Assert.True(bindSyntax.Span.End < dispatch.SpanStart);

        Assert.Contains(dispatch.Ancestors(), static node => node is AwaitExpressionSyntax);

        MethodDeclarationSyntax resume = Method("LongRestSweepCoordinator", "ResumeAsync");

        InvocationExpressionSyntax proofSyntax = Assert.Single(resume.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ProveDrainedMaintenance" });

        IInvocationOperation proof = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(proofSyntax));

        IMethodSymbol slot = proof.TargetMethod;

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", slot.ContainingType.ToDisplayString());

        Assert.Equal(compilation.Assembly.Identity, slot.ContainingAssembly.Identity);

        Assert.True(slot.IsVirtual && !slot.IsAbstract && !slot.IsStatic);

        Assert.True(proof.IsVirtual);

        Assert.Equal(SpecialType.System_Int64, Assert.Single(slot.Parameters).Type.SpecialType);

        Assert.Equal(RefKind.None, slot.Parameters[0].RefKind);

        Assert.True(Assert.Single(proof.Arguments).Value.ConstantValue.HasValue);

        Assert.Equal(0L, Assert.Single(proof.Arguments).Value.ConstantValue.Value);

        MethodDeclarationSyntax defaultBody = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(slot.DeclaringSyntaxReferences).GetSyntax());

        Assert.IsType<InterfaceDeclarationSyntax>(defaultBody.Parent);

        Assert.True(defaultBody.ExpressionBody!.Expression.IsKind(SyntaxKind.NullLiteralExpression));

        IMethodSymbol effective = Assert.IsAssignableFrom<IMethodSymbol>(leaseType.FindImplementationForInterfaceMember(slot));

        Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(Method("DrainedWorkLease", "ProveDrainedMaintenance")), effective));

        Assert.False(SymbolEqualityComparer.Default.Equals(slot, effective));

        Assert.Equal(compilation.Assembly.Identity, effective.ContainingAssembly.Identity);

        Assert.True(SymbolEqualityComparer.Default.Equals(slot.ReturnType, effective.ReturnType));

        IPropertyReferenceOperation receiverRead = Assert.IsAssignableFrom<IPropertyReferenceOperation>(proof.Instance);

        if (shape == "unknown-receiver")
        {
            Assert.Equal("UnknownDrainedReceiver", receiverRead.Property.ContainingType.Name);

            Assert.NotNull(receiverRead.Property.SetMethod);
        }
        else
        {
            if (shape == "foreign-holder")
            {
                VariableDeclaratorSyntax foreignHolderSyntax = Assert.Single(resume.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
                    static variable => variable.Identifier.ValueText == "other");

                ILocalSymbol foreignHolder = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(foreignHolderSyntax));

                IObjectCreationOperation foreignAllocation = Assert.IsAssignableFrom<IObjectCreationOperation>(
                    model.GetOperation(foreignHolderSyntax.Initializer!.Value));

                Assert.Equal("ForeignHolder", foreignAllocation.Type!.ToDisplayString());

                Assert.Equal("ForeignHolder", receiverRead.Property.ContainingType.ToDisplayString());

                Assert.NotNull(receiverRead.Property.SetMethod);

                Assert.True(SymbolEqualityComparer.Default.Equals(foreignHolder,
                    Assert.IsAssignableFrom<ILocalReferenceOperation>(receiverRead.Instance).Local));

                Assert.DoesNotContain(resume.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                    static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Bind" });
            }
            else
            {
                Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission", receiverRead.Property.ContainingType.ToDisplayString());

                IParameterSymbol holder = Assert.IsAssignableFrom<IParameterSymbol>(model.GetDeclaredSymbol(
                    Assert.Single(Type("LongRestSweepCoordinator").ParameterList!.Parameters)));

                Assert.True(SymbolEqualityComparer.Default.Equals(holder,
                    Assert.IsAssignableFrom<IParameterReferenceOperation>(receiverRead.Instance).Parameter));
            }
        }

        InvocationExpressionSyntax firstCall = Assert.Single(resume.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "SendFirstAsync");

        InvocationExpressionSyntax secondCall = Assert.Single(resume.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "SendSecondAsync");

        Assert.True(firstCall.Span.End < proofSyntax.SpanStart && proofSyntax.Span.End < secondCall.SpanStart);

        InvocationExpressionSyntax? effectSyntax = null;

        if (shape != "benign")
        {
            effectSyntax = Assert.Single(Method("DrainedWorkLease", "ProveDrainedMaintenance")
                .DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static call => call.Expression.ToString() == "System.IO.File.Delete");

            Assert.Equal("System.IO.File.Delete", effectSyntax.Expression.ToString());

            IInvocationOperation effectCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

            Assert.Equal("System.IO.File", effectCall.TargetMethod.ContainingType.ToDisplayString());

            Assert.Empty(effectCall.TargetMethod.DeclaringSyntaxReferences);

            AssertMetadataReferenceOwns(compilation, effectCall.TargetMethod.ContainingAssembly);

            Assert.True(Assert.Single(effective.DeclaringSyntaxReferences).Span.Contains(effectSyntax.Span));
        }

        if (shape is "self-dispose-explicit" or "self-dispose-implicit")
        {
            MethodDeclarationSyntax implementationBody = Method("DrainedWorkLease", "ProveDrainedMaintenance");

            InvocationExpressionSyntax releaseSyntax = Assert.Single(implementationBody.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static call => call.Expression is IdentifierNameSyntax { Identifier.ValueText: "Dispose" }
                    or MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Dispose" });

            IInvocationOperation releaseCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(releaseSyntax));

            IMethodSymbol dispose = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(Method("DrainedWorkLease", "Dispose")));

            Assert.True(SymbolEqualityComparer.Default.Equals(dispose, releaseCall.TargetMethod));

            Assert.Equal(compilation.Assembly.Identity, dispose.ContainingAssembly.Identity);

            Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, dispose.ContainingType));

            Assert.False(releaseCall.IsVirtual);

            Assert.Empty(releaseCall.Arguments);

            IInstanceReferenceOperation currentLease = Assert.IsAssignableFrom<IInstanceReferenceOperation>(releaseCall.Instance);

            Assert.Equal(InstanceReferenceKind.ContainingTypeInstance, currentLease.ReferenceKind);

            Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, currentLease.Type));

            INamedTypeSymbol disposable = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("System.IDisposable"));

            IMethodSymbol disposeSlot = Assert.Single(disposable.GetMembers("Dispose").OfType<IMethodSymbol>());

            Assert.Empty(disposeSlot.DeclaringSyntaxReferences);

            AssertMetadataReferenceOwns(compilation, disposeSlot.ContainingAssembly);

            Assert.True(SymbolEqualityComparer.Default.Equals(dispose, leaseType.FindImplementationForInterfaceMember(disposeSlot)));

            if (shape == "self-dispose-explicit")
                Assert.IsType<ThisExpressionSyntax>(Assert.IsType<MemberAccessExpressionSyntax>(releaseSyntax.Expression).Expression);
            else
                Assert.IsType<IdentifierNameSyntax>(releaseSyntax.Expression);

            Assert.NotNull(effectSyntax);

            Assert.True(releaseSyntax.Span.End < effectSyntax.SpanStart);

            Assert.True(Assert.Single(effective.DeclaringSyntaxReferences).Span.Contains(releaseSyntax.Span));
        }

        InvocationExpressionSyntax? detachedSyntax = null;

        if (shape is "local-self-dispose" or "detached-self-dispose")
        {
            MethodDeclarationSyntax implementationBody = Method("DrainedWorkLease", "ProveDrainedMaintenance");

            InvocationExpressionSyntax disposeSyntax = Assert.Single(implementationBody.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Dispose" });

            IInvocationOperation disposeCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(disposeSyntax));

            IMethodSymbol dispose = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(Method("DrainedWorkLease", "Dispose")));

            Assert.True(SymbolEqualityComparer.Default.Equals(dispose, disposeCall.TargetMethod));

            Assert.Equal(compilation.Assembly.Identity, dispose.ContainingAssembly.Identity);

            IInstanceReferenceOperation currentLease = Assert.IsAssignableFrom<IInstanceReferenceOperation>(disposeCall.Instance);

            Assert.Equal(InstanceReferenceKind.ContainingTypeInstance, currentLease.ReferenceKind);

            Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, currentLease.Type));

            INamedTypeSymbol disposable = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("System.IDisposable"));

            IMethodSymbol disposeSlot = Assert.Single(disposable.GetMembers("Dispose").OfType<IMethodSymbol>());

            Assert.Empty(disposeSlot.DeclaringSyntaxReferences);

            AssertMetadataReferenceOwns(compilation, disposeSlot.ContainingAssembly);

            Assert.True(SymbolEqualityComparer.Default.Equals(dispose, leaseType.FindImplementationForInterfaceMember(disposeSlot)));

            if (shape == "local-self-dispose")
            {
                LocalFunctionStatementSyntax local = Assert.Single(implementationBody.DescendantNodes().OfType<LocalFunctionStatementSyntax>());

                IMethodSymbol localMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(local));

                Assert.Equal("Drop", localMethod.Name);

                Assert.Equal(MethodKind.LocalFunction, localMethod.MethodKind);

                Assert.False(localMethod.IsStatic);

                Assert.True(SymbolEqualityComparer.Default.Equals(effective, localMethod.ContainingSymbol));

                Assert.True(local.Body!.Span.Contains(disposeSyntax.Span));

                InvocationExpressionSyntax invokeSyntax = Assert.Single(implementationBody.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                    static call => call.Expression is IdentifierNameSyntax { Identifier.ValueText: "Drop" });

                IInvocationOperation invoke = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(invokeSyntax));

                Assert.True(SymbolEqualityComparer.Default.Equals(localMethod, invoke.TargetMethod));

                Assert.Null(invoke.Instance);

                Assert.Empty(invoke.Arguments);

                Assert.NotNull(effectSyntax);

                Assert.True(invokeSyntax.Span.End < effectSyntax.SpanStart);
            }
            else
            {
                detachedSyntax = Assert.Single(implementationBody.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                    static call => call.Expression.ToString() == "Task.Run");

                IInvocationOperation runCallback = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(detachedSyntax));

                Assert.Equal("System.Threading.Tasks.Task", runCallback.TargetMethod.ContainingType.ToDisplayString());

                Assert.Equal("Run", runCallback.TargetMethod.Name);

                Assert.True(runCallback.TargetMethod.IsStatic);

                Assert.Empty(runCallback.TargetMethod.TypeArguments);

                Assert.Equal("System.Action", Assert.Single(runCallback.TargetMethod.Parameters).Type.ToDisplayString());

                Assert.Empty(runCallback.TargetMethod.DeclaringSyntaxReferences);

                AssertMetadataReferenceOwns(compilation, runCallback.TargetMethod.ContainingAssembly);

                IDelegateCreationOperation callback = Assert.IsAssignableFrom<IDelegateCreationOperation>(Assert.Single(runCallback.Arguments).Value);

                IAnonymousFunctionOperation lambda = Assert.IsAssignableFrom<IAnonymousFunctionOperation>(callback.Target);

                Assert.True(lambda.Syntax.Span.Contains(disposeSyntax.Span));

                Assert.True(SymbolEqualityComparer.Default.Equals(dispose,
                    Assert.Single(lambda.Body.DescendantsAndSelf().OfType<IInvocationOperation>()).TargetMethod));

                Assert.DoesNotContain(detachedSyntax.Ancestors(), static node => node is AwaitExpressionSyntax);

                Assert.NotNull(effectSyntax);

                Assert.True(detachedSyntax.Span.End < effectSyntax.SpanStart);
            }
        }

        InvocationExpressionSyntax? foreignGroupSyntax = null;

        if (shape == "foreign-instance-group")
        {
            MethodDeclarationSyntax implementationBody = Method("DrainedWorkLease", "ProveDrainedMaintenance");

            foreignGroupSyntax = Assert.Single(implementationBody.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TryBeginExternalEffectGroup" });

            IInvocationOperation foreignGroup = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(foreignGroupSyntax));

            Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", foreignGroup.TargetMethod.ContainingType.ToDisplayString());

            Assert.Equal(compilation.Assembly.Identity, foreignGroup.TargetMethod.ContainingAssembly.Identity);

            IFieldReferenceOperation fieldRead = Assert.IsAssignableFrom<IFieldReferenceOperation>(foreignGroup.Instance);

            Assert.Equal("_foreignLease", fieldRead.Field.Name);

            Assert.True(fieldRead.Field.IsReadOnly && !fieldRead.Field.IsStatic);

            Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, fieldRead.Field.ContainingType));

            Assert.Equal(InstanceReferenceKind.ContainingTypeInstance,
                Assert.IsAssignableFrom<IInstanceReferenceOperation>(fieldRead.Instance).ReferenceKind);

            VariableDeclaratorSyntax fieldSyntax = Assert.IsType<VariableDeclaratorSyntax>(Assert.Single(fieldRead.Field.DeclaringSyntaxReferences).GetSyntax());

            IPropertyReferenceOperation unknown = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(fieldSyntax.Initializer!.Value));

            Assert.Equal("UnknownDrainedReceiver", unknown.Property.ContainingType.Name);

            Assert.True(unknown.Property.IsStatic);

            Assert.NotNull(unknown.Property.SetMethod);

            Assert.Null(unknown.Instance);

            IArgumentOperation groupArgument = Assert.Single(foreignGroup.Arguments);

            IParameterSymbol groupParameter = Assert.IsAssignableFrom<IParameterSymbol>(groupArgument.Parameter);

            Assert.Equal(RefKind.Out, groupParameter.RefKind);

            Assert.Equal("System.IDisposable", groupParameter.Type.ToDisplayString());

            ILocalSymbol foreignLocal = Assert.IsAssignableFrom<ILocalReferenceOperation>(
                Assert.IsAssignableFrom<IDeclarationExpressionOperation>(groupArgument.Value).Expression).Local;

            UsingStatementSyntax retainedForeignGroup = Assert.Single(implementationBody.DescendantNodes().OfType<UsingStatementSyntax>());

            Assert.True(SymbolEqualityComparer.Default.Equals(foreignLocal, model.GetSymbolInfo(retainedForeignGroup.Expression!).Symbol));

            Assert.NotNull(effectSyntax);

            Assert.True(retainedForeignGroup.Statement.Span.Contains(effectSyntax.Span));

            Assert.True(foreignGroupSyntax.Span.End < effectSyntax.SpanStart);
        }

        InvocationExpressionSyntax secondEffectSyntax = Assert.Single(Method("LongRestSweepCoordinator", "SendSecondAsync")
            .DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "System.IO.File.Delete");

        IInvocationOperation secondEffectCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(secondEffectSyntax));

        Assert.Equal("System.IO.File", secondEffectCall.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", secondEffectCall.TargetMethod.Name);

        Assert.Empty(secondEffectCall.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, secondEffectCall.TargetMethod.ContainingAssembly);

        HostedProducerOperationEntry root = OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep };

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]), [new("Worker", [root])], []);

        HostedProducerSite acquired = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite Send(string name) => Assert.Single(result.Items, site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator"
            && site.Member == name && site.Callee == "System.IO.File.Delete");

        HostedProducerSite Group(string name) => Assert.Single(result.Items, site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator"
            && site.Member == name && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        HostedProducerSite first = Send("SendFirstAsync");

        HostedProducerSite second = Send("SendSecondAsync");

        Assert.Equal(acquired.CapsuleId, first.WorkFrontierCapsuleId);

        Assert.Equal(Group("SendFirstAsync").CapsuleId, first.EffectFrontierCapsuleId);

        string callee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.ProveDrainedMaintenance";

        string coordinate = tree.FilePath + ":" + proofSyntax.SpanStart;

        if (shape is "field-initializer" or "property-initializer")
        {
            Assert.NotNull(acquired.CapsuleId);

            Assert.Equal(acquired.CapsuleId, Group("SendFirstAsync").WorkFrontierCapsuleId);

            Assert.NotNull(Group("SendFirstAsync").CapsuleId);

            Assert.EndsWith("/site@" + tree.FilePath + ":" + acquisitionSyntax.SpanStart, acquired.OperationId);

            InvocationExpressionSyntax firstEffectSyntax = Assert.Single(Method("LongRestSweepCoordinator", "SendFirstAsync")
                .DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static call => call.Expression.ToString() == "System.IO.File.Delete");

            InvocationExpressionSyntax firstGroupSyntax = Assert.Single(Method("LongRestSweepCoordinator", "SendFirstAsync")
                .DescendantNodes().OfType<InvocationExpressionSyntax>(),
                call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryBeginExternalEffectGroup" });

            Assert.EndsWith("/site@" + tree.FilePath + ":" + firstEffectSyntax.SpanStart, first.OperationId);

            Assert.EndsWith("/site@" + tree.FilePath + ":" + firstGroupSyntax.SpanStart, Group("SendFirstAsync").OperationId);

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_ROOT_UNRESOLVED"
                or "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED" or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED");

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
                && diagnostic.Identity == coordinate);

            Assert.NotNull(effectSyntax);

            string effectCoordinate = "/site@" + tree.FilePath + ":" + effectSyntax.SpanStart;

            HostedProducerSite[] physical = result.Items.Where(site => site.RootType == "Worker"
                && site.EnclosingType == "Worker.DrainedWorkLease" && site.Member == "ProveDrainedMaintenance"
                && site.Callee == "System.IO.File.Delete"
                && site.OperationId.EndsWith(effectCoordinate, StringComparison.Ordinal)).ToArray();

            Assert.NotEmpty(physical);

            Assert.All(physical, site =>
            {
                Assert.Null(site.WorkFrontierCapsuleId);

                Assert.Null(site.EffectFrontierCapsuleId);

                foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
                    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code
                        && diagnostic.Detail == "System.IO.File.Delete"
                        && diagnostic.Identity.StartsWith(site.AuthorityOperationId + "/", StringComparison.Ordinal)
                        && diagnostic.Identity.EndsWith(effectCoordinate, StringComparison.Ordinal));
            });

            Assert.Null(second.WorkFrontierCapsuleId);

            string secondCoordinate = "/site@" + tree.FilePath + ":" + secondEffectSyntax.SpanStart;

            Assert.EndsWith(secondCoordinate, second.OperationId);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.StartsWith(second.AuthorityOperationId + "/", StringComparison.Ordinal)
                && diagnostic.Identity.EndsWith(secondCoordinate, StringComparison.Ordinal));

            return;
        }

        if (shape is "static-property-sibling-initializer" or "static-property-partial-constructor")
        {
            Assert.NotNull(acquired.CapsuleId);

            Assert.Equal(acquired.CapsuleId, Group("SendFirstAsync").WorkFrontierCapsuleId);

            Assert.NotNull(Group("SendFirstAsync").CapsuleId);

            Assert.EndsWith("/site@" + tree.FilePath + ":" + acquisitionSyntax.SpanStart, acquired.OperationId);

            InvocationExpressionSyntax firstEffectSyntax = Assert.Single(Method("LongRestSweepCoordinator", "SendFirstAsync")
                .DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static call => call.Expression.ToString() == "System.IO.File.Delete");

            InvocationExpressionSyntax firstGroupSyntax = Assert.Single(Method("LongRestSweepCoordinator", "SendFirstAsync")
                .DescendantNodes().OfType<InvocationExpressionSyntax>(),
                call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryBeginExternalEffectGroup" });

            Assert.EndsWith("/site@" + tree.FilePath + ":" + firstEffectSyntax.SpanStart, first.OperationId);

            Assert.EndsWith("/site@" + tree.FilePath + ":" + firstGroupSyntax.SpanStart, Group("SendFirstAsync").OperationId);

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_ROOT_UNRESOLVED"
                or "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED" or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED");

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
                && diagnostic.Identity == coordinate);

            output.WriteLine("SCOPED_LEASE_STATIC_INITIALIZER_FIRST_ANCHOR shape=" + shape + " passed");

            Assert.NotNull(effectSyntax);

            string effectCoordinate = "/site@" + tree.FilePath + ":" + effectSyntax.SpanStart;

            HostedProducerSite[] physical = result.Items.Where(site => site.RootType == "Worker"
                && site.EnclosingType == "Worker.DrainedWorkLease" && site.Member == "ProveDrainedMaintenance"
                && site.Callee == "System.IO.File.Delete"
                && site.OperationId.EndsWith(effectCoordinate, StringComparison.Ordinal)).ToArray();

            Assert.NotEmpty(physical);

            Assert.All(physical, site =>
            {
                Assert.Null(site.WorkFrontierCapsuleId);

                Assert.Null(site.EffectFrontierCapsuleId);

                foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
                    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code
                        && diagnostic.Detail == "System.IO.File.Delete"
                        && diagnostic.Identity.StartsWith(site.AuthorityOperationId + "/", StringComparison.Ordinal)
                        && diagnostic.Identity.EndsWith(effectCoordinate, StringComparison.Ordinal));
            });

            Assert.Null(second.WorkFrontierCapsuleId);

            string secondCoordinate = "/site@" + tree.FilePath + ":" + secondEffectSyntax.SpanStart;

            Assert.EndsWith(secondCoordinate, second.OperationId);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.StartsWith(second.AuthorityOperationId + "/", StringComparison.Ordinal)
                && diagnostic.Identity.EndsWith(secondCoordinate, StringComparison.Ordinal));

            return;
        }

        if (shape is "unknown-receiver" or "foreign-holder")
        {
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
                && diagnostic.Identity == coordinate && diagnostic.Detail == callee);

            Assert.DoesNotContain(result.Items, static site => site.EnclosingType == "Worker.DrainedWorkLease" && site.Callee == "System.IO.File.Delete");

            return;
        }

        if (shape == "override-effect")
        {
            Assert.NotNull(effectSyntax);

            string effectCoordinate = "/site@" + tree.FilePath + ":" + effectSyntax.SpanStart;

            HostedProducerSite[] physical = result.Items.Where(site => site.Callee == "System.IO.File.Delete"
                && site.OperationId.EndsWith(effectCoordinate, StringComparison.Ordinal)).ToArray();

            Assert.NotEmpty(physical);

            Assert.All(physical, site =>
            {
                Assert.Equal(acquired.CapsuleId, site.WorkFrontierCapsuleId);

                Assert.Null(site.EffectFrontierCapsuleId);
            });

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.EndsWith(effectCoordinate, StringComparison.Ordinal));

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Identity.EndsWith(effectCoordinate, StringComparison.Ordinal));
        }

        if (shape is "self-dispose-explicit" or "self-dispose-implicit" or "local-self-dispose" or "detached-self-dispose")
        {
            Assert.NotNull(effectSyntax);

            string effectCoordinate = "/site@" + tree.FilePath + ":" + effectSyntax.SpanStart;

            HostedProducerSite[] physical = result.Items.Where(site => site.Callee == "System.IO.File.Delete"
                && site.OperationId.EndsWith(effectCoordinate, StringComparison.Ordinal)).ToArray();

            Assert.NotEmpty(physical);

            Assert.All(physical, site =>
            {
                Assert.Null(site.WorkFrontierCapsuleId);

                Assert.Null(site.EffectFrontierCapsuleId);
            });

            foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code
                    && diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.EndsWith(effectCoordinate, StringComparison.Ordinal));

            if (shape == "detached-self-dispose")
            {
                Assert.NotNull(detachedSyntax);

                string callbackCoordinate = "/callback@" + tree.FilePath + ":" + detachedSyntax.SpanStart;

                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    && diagnostic.Identity == first.AuthorityOperationId + callbackCoordinate
                    && diagnostic.Detail.StartsWith("System.Threading.Tasks.Task.Run;", StringComparison.Ordinal));
            }

            Assert.Null(second.WorkFrontierCapsuleId);

            string secondCoordinate = "/site@" + tree.FilePath + ":" + secondEffectSyntax.SpanStart;

            Assert.EndsWith(secondCoordinate, second.OperationId);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.StartsWith(second.AuthorityOperationId + "/", StringComparison.Ordinal)
                && diagnostic.Identity.EndsWith(secondCoordinate, StringComparison.Ordinal));

            return;
        }

        if (shape == "foreign-instance-group")
        {
            Assert.NotNull(effectSyntax);

            Assert.NotNull(foreignGroupSyntax);

            string groupCoordinate = "/site@" + tree.FilePath + ":" + foreignGroupSyntax.SpanStart;

            HostedProducerSite group = Assert.Single(result.Items, site => site.RootType == "Worker"
                && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup"
                && site.OperationId.EndsWith(groupCoordinate, StringComparison.Ordinal));

            Assert.Equal(acquired.CapsuleId, group.WorkFrontierCapsuleId);

            string effectCoordinate = "/site@" + tree.FilePath + ":" + effectSyntax.SpanStart;

            HostedProducerSite[] physical = result.Items.Where(site => site.Callee == "System.IO.File.Delete"
                && site.OperationId.EndsWith(effectCoordinate, StringComparison.Ordinal)).ToArray();

            Assert.NotEmpty(physical);

            Assert.All(physical, site =>
            {
                Assert.Equal(acquired.CapsuleId, site.WorkFrontierCapsuleId);

                Assert.Null(site.EffectFrontierCapsuleId);
            });

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.EndsWith(effectCoordinate, StringComparison.Ordinal));

            return;
        }

        Assert.Equal(acquired.CapsuleId, second.WorkFrontierCapsuleId);

        Assert.Equal(Group("SendSecondAsync").CapsuleId, second.EffectFrontierCapsuleId);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED" && diagnostic.Identity == coordinate);

        if (shape == "benign") Assert.Empty(result.Diagnostics);
    }
}
