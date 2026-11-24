using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void Reaching_ordinary_operator_binding_refuses_only_the_independent_absent_entry()
    {
        const string sourcePath = "src/Fixture.cs";

        const string source = """
            namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions
            {
                internal sealed class GrimoireOfflineTransitionStartupRecovery
                {
                    public void RecoverBeforeBootstrapAsync(System.Data.Common.DbCommand command) =>
                        new RetroDownfall.Arcanum.Infrastructure.Guild.GuildOperatorWorkspaceWrite().Invoke(command);
                }

                internal sealed class UnknownCaller
                {
                    public void Run(System.Data.Common.DbCommand command) =>
                        new GrimoireOfflineTransitionStartupRecovery().RecoverBeforeBootstrapAsync(command);
                }
            }

            namespace RetroDownfall.Arcanum.Infrastructure.Guild
            {
                internal sealed class GuildOperatorWorkspaceWrite
                {
                    internal void Invoke(System.Data.Common.DbCommand command) => Enter(command);

                    private void Enter(System.Data.Common.DbCommand command) => command.ExecuteScalar();
                }
            }
            """;

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Infrastructure");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        const string lifecycle = "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery";

        const string nestedCaller = "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.UnknownCaller";

        var declaredEntry = new NonHostedProducerChainEntry(lifecycle + ".RecoverBeforeBootstrapAsync", sourcePath,
            lifecycle, "RecoverBeforeBootstrapAsync", HostedProducerAuthorityKind.FiniteRequest, "fixture", []);

        var otherEntry = new NonHostedProducerChainEntry(nestedCaller + ".Run", sourcePath,
            nestedCaller, "Run", HostedProducerAuthorityKind.FiniteRequest, "fixture", []);

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new([], []), [], [declaredEntry, otherEntry]);

        HostedProducerInventoryDiagnostic conflict = Assert.Single(discovery.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_ORDINARY_OPERATOR_ENTRY_REACHABLE");

        Assert.StartsWith(declaredEntry.ChainId, conflict.Identity, StringComparison.Ordinal);

        Assert.Contains(discovery.Items, site => site.AuthorityOperationId == otherEntry.ChainId
            && site.Kind == HostedProducerSiteKind.DatabaseAccess && site.Callee == "System.Data.Common.DbCommand.ExecuteScalar");
    }

    [Theory]
    [InlineData("current")]
    [InlineData("quest-binds-operator")]
    [InlineData("replace-forwarded-parameter")]
    [InlineData("republish-current-in-validation")]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Ordinary_operator_issuance_requires_the_exclusive_production_ingress(string shape)
    {
        IReadOnlyList<CSharpCompilation> production = HostedGrimoireProducerInventory.ProductionCompilations;

        CSharpCompilation infrastructure = Assert.Single(production,
            static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        if (shape == "quest-binds-operator")
        {
            SyntaxTree tree = Assert.Single(infrastructure.SyntaxTrees,
                static tree => tree.FilePath.EndsWith("/GuildWorkspaceMutationTools.cs", StringComparison.Ordinal));

            SyntaxNode original = tree.GetRoot();

            InvocationExpressionSyntax ingress = Assert.Single(original.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static node => node.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "EnterInvocation" });

            ArgumentSyntax unexpected = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(
                "new GuildOperatorWorkspaceWrite.Call(null!, \"write_file\")"))
                .WithNameColon(SyntaxFactory.NameColon("operatorCall"));

            CSharpCompilation changed = infrastructure.ReplaceSyntaxTree(tree,
                tree.WithRootAndOptions(original.ReplaceNode(ingress, ingress.AddArgumentListArguments(unexpected)), tree.Options));

            Assert.Empty(changed.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

            production = [.. production.Select(compilation => ReferenceEquals(compilation, infrastructure) ? changed : compilation)];
        }

        if (shape is "replace-forwarded-parameter" or "republish-current-in-validation")
        {
            SyntaxTree tree = Assert.Single(infrastructure.SyntaxTrees,
                static tree => tree.FilePath.EndsWith("/GuildWorkspaceMutationFence.cs", StringComparison.Ordinal));

            bool replaceParameter = shape == "replace-forwarded-parameter";

            SyntaxNode original = tree.GetRoot();

            MethodDeclarationSyntax method = Assert.Single(original.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                candidate => candidate.Identifier.ValueText == (replaceParameter ? "EnterInvocation" : "ValidateAsync"));

            StatementSyntax newStatement = SyntaxFactory.ParseStatement(replaceParameter
                ? "operatorCall ??= Current.Value?.OperatorCall;"
                : "Current.Value = invocation;");

            CSharpCompilation changed = infrastructure.ReplaceSyntaxTree(tree,
                tree.WithRootAndOptions(original.ReplaceNode(method,
                    method.WithBody(method.Body!.WithStatements(method.Body.Statements.Insert(1, newStatement)))), tree.Options));

            Assert.Empty(changed.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

            production = [.. production.Select(compilation => ReferenceEquals(compilation, infrastructure) ? changed : compilation)];
        }

        Assert.Equal(shape == "current", HostedGrimoireProducerInventory.HasExclusiveOrdinaryOperatorIngress(production));
    }
}
