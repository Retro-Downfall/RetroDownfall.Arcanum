using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(257, false)]
    public void Detached_callback_statement_budget_refuses_incomplete_capture_analysis(int statementCount, bool retained)
    {
        string source = R2Source(R2Admission
            + " using var registration = token.Register(() => { " + new string(';', statementCount) + " });"
            + " System.IO.File.Exists(\"after-registration\"); await Task.CompletedTask;");

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        InvocationExpressionSyntax registration = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "token.Register");

        IInvocationOperation selected = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(registration));

        Assert.Equal("System.Threading.CancellationToken", selected.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(typeof(CancellationToken).Assembly.GetName().Name, selected.TargetMethod.ContainingAssembly.Name);

        Assert.DoesNotContain(selected.TargetMethod.Locations, static location => location.IsInSource);

        Assert.Equal("System.Action", Assert.Single(selected.TargetMethod.Parameters).Type.ToDisplayString());

        IDelegateCreationOperation converted = Assert.IsAssignableFrom<IDelegateCreationOperation>(Assert.Single(selected.Arguments).Value);

        IAnonymousFunctionOperation callback = Assert.IsAssignableFrom<IAnonymousFunctionOperation>(converted.Target);

        ParenthesizedLambdaExpressionSyntax lambda = Assert.IsType<ParenthesizedLambdaExpressionSyntax>(callback.Syntax);

        BlockSyntax body = Assert.IsType<BlockSyntax>(lambda.Body);

        Assert.Equal(statementCount, body.Statements.Count);

        Assert.All(body.Statements, static statement => Assert.IsType<EmptyStatementSyntax>(statement));

        Assert.Empty(body.DescendantNodes().OfType<IdentifierNameSyntax>());

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation],
            new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        HostedProducerSite after = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Equal(retained, after.WorkFrontierCapsuleId is not null);

        if (retained)
        {
            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_SITE_WORK_FRONTIER_MISSING");
        }
        else
        {
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Identity.EndsWith("/callback@" + tree.FilePath + ":" + registration.SpanStart, StringComparison.Ordinal)
                && diagnostic.Detail.StartsWith("System.Threading.CancellationToken.Register;", StringComparison.Ordinal));

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Identity == after.OperationId);
        }
    }
}
