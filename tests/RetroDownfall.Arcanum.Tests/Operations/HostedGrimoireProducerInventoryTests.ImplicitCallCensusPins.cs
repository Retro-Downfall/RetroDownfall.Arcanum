using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The pins of the Implicit Call Census. The census is a safety net only while it is complete, so the Roslyn
// surface it classifies is pinned: a kind, or a member that carries a symbol or a conversion, that a Roslyn upgrade
// adds fails here until someone has decided what it means for the traversal.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void OperationKindCensusClassifiesEveryRoslynOperationKind()
    {
        OperationKind[] declared = [.. Enum.GetValues<OperationKind>().Distinct()];

        OperationKind[] unclassified = [.. declared.Where(static kind => !HostedGrimoireProducerInventory.OperationKindCensus.ContainsKey(kind))];

        Assert.True(unclassified.Length == 0, "Operation kinds the census has not classified: " + string.Join(", ", unclassified));

        OperationKind[] stale = [.. HostedGrimoireProducerInventory.OperationKindCensus.Keys.Where(kind => !declared.Contains(kind))];

        Assert.True(stale.Length == 0, "Classified operation kinds Roslyn no longer declares: " + string.Join(", ", stale));

        Assert.All(HostedGrimoireProducerInventory.OperationKindCensus, static entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value.Reason), entry.Key + " has no reviewed reason."));
    }

    [Fact]
    public void OperationSymbolCarriersMatchTheRoslynOperationInterfaces()
    {
        string[] roslyn = [.. HostedGrimoireProducerInventory.RoslynOperationSymbolCarriers()];

        string[] reviewed = [.. HostedGrimoireProducerInventory.OperationSymbolCarriers.Keys.Order(StringComparer.Ordinal)];

        Assert.True(roslyn.Except(reviewed).Count() == 0, "Symbol-carrying operation members the census has not reviewed: " + string.Join(", ", roslyn.Except(reviewed)));

        Assert.True(reviewed.Except(roslyn).Count() == 0, "Reviewed operation members Roslyn no longer declares: " + string.Join(", ", reviewed.Except(roslyn)));
    }

    // The census walks the operation tree of every member body, so a body Roslyn cannot produce a tree for would be
    // a body no implicit call is ever found in.
    [Fact]
    public void EveryProductionMemberBodyHasAnOperationTree()
    {
        List<string> missing = [];

        foreach (CSharpCompilation compilation in HostedGrimoireProducerInventory.ProductionCompilations)
        {
            foreach (SyntaxTree tree in compilation.SyntaxTrees.Where(static tree => tree.FilePath.StartsWith("src/", StringComparison.Ordinal) && !tree.FilePath.Contains("/obj/", StringComparison.Ordinal)))
            {
                SemanticModel model = compilation.GetSemanticModel(tree);

                foreach (SyntaxNode node in tree.GetRoot().DescendantNodes().Where(static node => node is BaseMethodDeclarationSyntax { Body: not null } or BaseMethodDeclarationSyntax { ExpressionBody: not null } or AccessorDeclarationSyntax { Body: not null } or AccessorDeclarationSyntax { ExpressionBody: not null }))
                {
                    if (model.GetOperation(node) is null)
                    {
                        missing.Add(tree.FilePath + ":" + node.SpanStart);
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, "Member bodies without an operation tree: " + string.Join(", ", missing.Take(10)));
    }

    // Every kind of implicit call is exercised by at least one construct of the corpus, so a kind whose collector
    // silently stops producing items is noticed.
    [Fact]
    public void TheCorpusExercisesEveryKindOfImplicitCall()
    {
        HashSet<HostedGrimoireProducerInventory.ImplicitCallKind> exercised = [];

        foreach (CensusConstruct construct in CensusCorpus)
        {
            CSharpCompilation compilation = CensusCompile(construct, admitted: true, effect: true);

            SyntaxTree tree = compilation.SyntaxTrees.Single();

            foreach (SyntaxNode member in tree.GetRoot().DescendantNodes().Where(static node => node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax))
            {
                foreach (HostedGrimoireProducerInventory.ImplicitCall call in HostedGrimoireProducerInventory.CollectImplicitCalls(compilation.GetSemanticModel(tree), member))
                {
                    exercised.Add(call.Kind);
                }
            }
        }

        // Roslyn produces these two only for shapes C# has no syntax for today; the census keeps them so that a shape
        // it starts producing is traversed, not dropped.
        HashSet<HostedGrimoireProducerInventory.ImplicitCallKind> defensive =
        [
            HostedGrimoireProducerInventory.ImplicitCallKind.UnnamedInvocation,
            HostedGrimoireProducerInventory.ImplicitCallKind.UnnamedObjectCreation,
        ];

        HostedGrimoireProducerInventory.ImplicitCallKind[] missing = [.. Enum.GetValues<HostedGrimoireProducerInventory.ImplicitCallKind>().Where(kind => !exercised.Contains(kind) && !defensive.Contains(kind))];

        Assert.True(missing.Length == 0, "Implicit call kinds the corpus never produces: " + string.Join(", ", missing));
    }
}
