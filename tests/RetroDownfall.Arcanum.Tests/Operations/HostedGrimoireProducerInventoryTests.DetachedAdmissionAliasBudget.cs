using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(257)]
    public void Detached_callback_alias_depth_cannot_restore_parent_admission(int aliasCount)
    {
        string aliases = string.Join(" ", Enumerable.Range(0, aliasCount)
            .Select(index => $"var captured{index} = " + (index == 0 ? "lease" : $"captured{index - 1}") + ";"));

        string source = R2Source(R2Admission + aliases
            + $" using var registration = token.Register(() => captured{aliasCount - 1}.Dispose());"
            + " System.IO.File.Exists(\"after-registration\"); await Task.CompletedTask;");

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        SyntaxNode syntax = tree.GetRoot();

        VariableDeclaratorSyntax lease = Assert.Single(syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static declaration => declaration.Identifier.ValueText == "lease");

        ISymbol previous = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(lease));

        foreach (int index in Enumerable.Range(0, aliasCount))
        {
            VariableDeclaratorSyntax alias = Assert.Single(syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
                declaration => declaration.Identifier.ValueText == $"captured{index}");

            Assert.True(SymbolEqualityComparer.Default.Equals(previous,
                Assert.IsAssignableFrom<ILocalReferenceOperation>(model.GetOperation(alias.Initializer!.Value)).Local));

            previous = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(alias));
        }

        InvocationExpressionSyntax registration = Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "token.Register");

        IInvocationOperation selected = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(registration));

        Assert.Equal("System.Threading.CancellationToken", selected.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(typeof(CancellationToken).Assembly.GetName().Name, selected.TargetMethod.ContainingAssembly.Name);

        Assert.Equal("System.Action", Assert.Single(selected.TargetMethod.Parameters).Type.ToDisplayString());

        IAnonymousFunctionOperation callback = Assert.IsAssignableFrom<IAnonymousFunctionOperation>(
            Assert.IsAssignableFrom<IDelegateCreationOperation>(Assert.Single(selected.Arguments).Value).Target);

        IInvocationOperation disposal = Assert.Single(callback.Body.DescendantsAndSelf().OfType<IInvocationOperation>());

        Assert.Equal("Dispose", disposal.TargetMethod.Name);

        Assert.True(SymbolEqualityComparer.Default.Equals(previous,
            Assert.IsAssignableFrom<ILocalReferenceOperation>(disposal.Instance).Local));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation],
            new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        HostedProducerSite after = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Null(after.WorkFrontierCapsuleId);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Identity == after.OperationId);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Identity.EndsWith("/callback@" + tree.FilePath + ":" + registration.SpanStart, StringComparison.Ordinal)
            && diagnostic.Detail.StartsWith("System.Threading.CancellationToken.Register;", StringComparison.Ordinal));
    }
}
