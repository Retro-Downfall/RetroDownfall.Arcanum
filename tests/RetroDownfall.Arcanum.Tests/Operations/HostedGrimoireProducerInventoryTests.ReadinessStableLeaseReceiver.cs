using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("direct")]
    [InlineData("one-local")]
    [InlineData("two-locals")]
    [InlineData("builtin-cast")]
    [InlineData("reassigned")]
    [InlineData("ref-replaced")]
    [InlineData("out-replaced")]
    [InlineData("authored-cast")]
    [InlineData("foreign-holder")]
    public void Readiness_physical_group_requires_stable_exact_lease_value(string shape)
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        ClassDeclarationSyntax handler = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "PerUnitRecoveryHandler");

        string prefix = shape switch
        {
            "direct" => "",
            "two-locals" => "IGrimoireWorkLease first = workAdmission.Lease; IGrimoireWorkLease lease = first;",
            "builtin-cast" => "IGrimoireWorkLease first = (IGrimoireWorkLease)workAdmission.Lease; IGrimoireWorkLease lease = first;",
            "reassigned" => "IGrimoireWorkLease lease = workAdmission.Lease; lease = new ReadinessWorkLease();",
            "ref-replaced" => "IGrimoireWorkLease lease = workAdmission.Lease; ReplaceLease(ref lease);",
            "out-replaced" => "IGrimoireWorkLease lease = workAdmission.Lease; ReplaceOutLease(out lease);",
            "authored-cast" => "IGrimoireWorkLease first = (AlternateReadinessLease)(ReadinessWorkLease)workAdmission.Lease; IGrimoireWorkLease lease = first;",
            "foreign-holder" => "IGrimoireWorkLease lease = foreign.Lease;",
            _ => "IGrimoireWorkLease lease = workAdmission.Lease;",
        };

        string receiver = shape == "direct" ? "workAdmission.Lease" : "lease";

        handler = handler.ReplaceNodes(handler.Members.OfType<MethodDeclarationSyntax>()
            .Where(static method => method.Identifier.ValueText is "SendFirstAsync" or "SendSecondAsync"), (original, _) =>
            original.WithBody((BlockSyntax)SyntaxFactory.ParseStatement($$"""
                {
                    {{prefix}}

                    if (!{{receiver}}.TryBeginExternalEffectGroup(out IDisposable group)) return;

                    using var owned = group;

                    await Task.CompletedTask;

                    System.IO.File.Delete("{{original.Identifier.ValueText}}");
                }
                """)));

        if (shape == "ref-replaced") handler = handler.AddMembers(SyntaxFactory.ParseMemberDeclaration(
            "private static void ReplaceLease(ref IGrimoireWorkLease lease) { lease = new ReadinessWorkLease(); }")!);

        if (shape == "out-replaced") handler = handler.AddMembers(SyntaxFactory.ParseMemberDeclaration(
            "private static void ReplaceOutLease(out IGrimoireWorkLease lease) { lease = new ReadinessWorkLease(); }")!);

        if (shape == "foreign-holder") handler = handler.AddMembers(SyntaxFactory.ParseMemberDeclaration(
            "private static readonly LongRestWorkAdmission foreign = new LongRestWorkAdmission();")!);

        source = unit.ReplaceNode(Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "PerUnitRecoveryHandler"), handler).ToFullString();

        if (shape == "authored-cast") source += """
            namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest
            {
                internal sealed class AlternateReadinessLease : IGrimoireWorkLease
                {
                    public static explicit operator AlternateReadinessLease(ReadinessWorkLease original) => new AlternateReadinessLease();

                    public bool TryBeginExternalEffectGroup(out IDisposable group) { group = new Group(); return true; }

                    public void Dispose() { }

                    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

                    private sealed class Group : IDisposable { public void Dispose() { } }
                }
            }
            """;

        AssertExplicitReadinessLeasePremises(source, "PerUnitRecoveryHandler");

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        MethodDeclarationSyntax[] sends = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(static method => method.Identifier.ValueText is "SendFirstAsync" or "SendSecondAsync").ToArray();

        Assert.Equal(2, sends.Length);

        foreach (MethodDeclarationSyntax send in sends)
        {
            InvocationExpressionSyntax group = Assert.Single(send.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
                model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "TryBeginExternalEffectGroup" });

            IInvocationOperation invocation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(group));

            Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", invocation.TargetMethod.ContainingType.ToDisplayString());

            if (shape == "direct")
            {
                Assert.Equal("Lease", Assert.IsAssignableFrom<IPropertyReferenceOperation>(invocation.Instance).Property.Name);
            }
            else
            {
                ILocalSymbol lease = Assert.IsAssignableFrom<ILocalReferenceOperation>(invocation.Instance).Local;

                Assert.Equal("lease", lease.Name);

                VariableDeclaratorSyntax declaration = Assert.IsType<VariableDeclaratorSyntax>(Assert.Single(lease.DeclaringSyntaxReferences).GetSyntax());

                Assert.True(declaration.Span.End < group.SpanStart);

                if (shape is "two-locals" or "builtin-cast" or "authored-cast")
                {
                    ILocalSymbol first = Assert.IsAssignableFrom<ILocalSymbol>(model.GetSymbolInfo(declaration.Initializer!.Value).Symbol);

                    Assert.Equal("first", first.Name);
                }

                if (shape == "reassigned")
                {
                    AssignmentExpressionSyntax assignment = Assert.Single(send.DescendantNodes().OfType<AssignmentExpressionSyntax>());

                    Assert.True(SymbolEqualityComparer.Default.Equals(lease, model.GetSymbolInfo(assignment.Left).Symbol));

                    Assert.True(assignment.Span.End < group.SpanStart);
                }

                if (shape is "ref-replaced" or "out-replaced")
                {
                    ArgumentSyntax argument = Assert.Single(send.DescendantNodes().OfType<ArgumentSyntax>(), argument =>
                        argument.RefKindKeyword.IsKind(shape == "ref-replaced" ? SyntaxKind.RefKeyword : SyntaxKind.OutKeyword)
                        && model.GetSymbolInfo(argument.Expression).Symbol is ILocalSymbol actual
                        && SymbolEqualityComparer.Default.Equals(actual, lease));

                    Assert.True(argument.Span.End < group.SpanStart);
                }

                if (shape == "foreign-holder")
                {
                    IPropertyReferenceOperation read = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(declaration.Initializer!.Value));

                    IFieldReferenceOperation foreign = Assert.IsAssignableFrom<IFieldReferenceOperation>(read.Instance);

                    Assert.True(foreign.Field.IsStatic && foreign.Field.IsReadOnly);

                    Assert.Equal("foreign", foreign.Field.Name);
                }
            }

            if (shape is "builtin-cast" or "authored-cast")
            {
                CastExpressionSyntax cast = Assert.Single(send.DescendantNodes().OfType<CastExpressionSyntax>(), cast =>
                    cast.Parent is EqualsValueClauseSyntax);

                IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(model.GetOperation(cast));

                Assert.Equal(shape == "authored-cast", conversion.Conversion.IsUserDefined);

                if (shape == "authored-cast")
                {
                    Assert.NotNull(conversion.OperatorMethod);

                    Assert.Equal("AlternateReadinessLease", conversion.Type!.Name);
                }
                else Assert.Null(conversion.OperatorMethod);
            }
        }

        output.WriteLine($"R_READINESS_STABLE_LEASE_PREMISES={shape}|actualInterfaceGroupReceiverAndSource=passed");

        AssertExplicitReadinessDiscovery(source, "PerUnitRecoveryHandler",
            shape is "direct" or "one-local" or "two-locals" or "builtin-cast" ? "owned-lease" : "unstable-or-foreign-lease");
    }
}
