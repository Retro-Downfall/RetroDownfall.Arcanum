using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void Readiness_owned_lease_refuses_opaque_current_instance_call_without_holder_argument()
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        ClassDeclarationSyntax handler = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "PerUnitRecoveryHandler");

        MethodDeclarationSyntax admitted = Assert.Single(handler.Members.OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "RecoverAdmittedAsync");

        ClassDeclarationSyntax replacement = handler.ReplaceNode(admitted,
            admitted.WithBody(admitted.Body!.WithStatements(admitted.Body.Statements.Insert(0,
                SyntaxFactory.ParseStatement("OpaqueHolderUse();")))))
            .AddMembers(SyntaxFactory.ParseMemberDeclaration("""
                [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)]
                private extern void OpaqueHolderUse();
                """)!);

        source = unit.ReplaceNode(handler, replacement).ToFullString();

        AssertExplicitReadinessLeasePremises(source, "PerUnitRecoveryHandler");

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        InvocationExpressionSyntax call = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(), static call =>
            call.Expression is IdentifierNameSyntax { Identifier.ValueText: "OpaqueHolderUse" });

        IInvocationOperation invocation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(call));

        IInstanceReferenceOperation instance = Assert.IsAssignableFrom<IInstanceReferenceOperation>(invocation.Instance);

        Assert.True(instance.IsImplicit);

        Assert.Equal(InstanceReferenceKind.ContainingTypeInstance, instance.ReferenceKind);

        Assert.False(invocation.TargetMethod.IsStatic);

        Assert.True(invocation.TargetMethod.IsExtern);

        Assert.Empty(invocation.Arguments);

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitRecoveryHandler", invocation.TargetMethod.ContainingType.ToDisplayString());

        MethodDeclarationSyntax actualDeclaration = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(invocation.TargetMethod.DeclaringSyntaxReferences).GetSyntax());

        Assert.Null(actualDeclaration.Body);

        Assert.Null(actualDeclaration.ExpressionBody);

        Assert.Contains(invocation.TargetMethod.GetAttributes(), static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.MethodImplAttribute");

        // The existing fallback uses Instance.Syntax. Record that actual compiler
        // shape so a passing refusal is not misreported as a new bridge defect.
        output.WriteLine($"R_READINESS_OPAQUE_INSTANCE=receiverKind={instance.Syntax.Kind()}|receiverSyntax={instance.Syntax}|target={invocation.TargetMethod}");

        AssertExplicitReadinessDiscovery(source, "PerUnitRecoveryHandler", "opaque-current-instance");
    }
}
