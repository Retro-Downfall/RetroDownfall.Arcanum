using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The SharedSemanticModels fixtures: the semantic model of a tree is one per compilation object and tree object for the whole process,
// so a discovery finds bound what an earlier discovery over the same compilation bound. The twins pin that nothing else is shared: a
// compilation made from another (a replaced tree) reads models of its own, and its verdict is its own.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string SharedModelStatement = "System.GC.KeepAlive(new Outer(new Leaf()));";

    private const string SharedModelRecords = "sealed record Outer(object Inner);";

    private static bool SharedModelReached(CSharpCompilation compilation)
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        return EnumerationDeleteSites(result).Length != 0 || result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void ADiscoveryReadsTheModelOfItsCompilationAndNoOtherCompilationDoes()
    {
        CSharpCompilation compilation = CensusCompileSource(SharedModelStatement, SharedModelRecords + " " + CallbackReachLeaf("Equals", effect: false));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        CSharpCompilation other = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Elsewhere { }", (CSharpParseOptions)tree.Options, "src/Elsewhere.cs"));

        (bool shared, bool distinct) = HostedGrimoireProducerInventory.ProbeSemanticModelSharing(compilation, other, tree);

        Assert.True(shared, "Two discoveries over one compilation read different models of one tree.");

        Assert.True(distinct, "A compilation made from another reads the model of the compilation it was made from.");
    }

    [Fact]
    public void ADiscoveryOverAReplacedTreeNeverReadsTheModelsOfTheCompilationItWasMadeFrom()
    {
        CSharpCompilation clean = CensusCompileSource(SharedModelStatement, SharedModelRecords + " " + CallbackReachLeaf("Equals", effect: false));

        SyntaxTree cleanTree = Assert.Single(clean.SyntaxTrees);

        string refusingText = cleanTree.GetText().ToString().Replace("return ReferenceEquals(this, other);", CallbackReachEffect + "return ReferenceEquals(this, other);", StringComparison.Ordinal);

        Assert.NotEqual(cleanTree.GetText().ToString(), refusingText);

        CSharpCompilation refusing = clean.ReplaceSyntaxTree(cleanTree, CSharpSyntaxTree.ParseText(refusingText, (CSharpParseOptions)cleanTree.Options, cleanTree.FilePath));

        Assert.Empty(refusing.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        // The order mixes the two compilations: each verdict is the verdict of its own source, whichever discovery came first.
        Assert.False(SharedModelReached(clean));

        Assert.True(SharedModelReached(refusing));

        Assert.False(SharedModelReached(clean));

        Assert.True(SharedModelReached(refusing));
    }

    [Fact]
    public void ADiscoveryRepeatedOverTheSameCompilationRepeatsItsVerdict()
    {
        CSharpCompilation compilation = CensusCompileSource(SharedModelStatement, SharedModelRecords + " " + CallbackReachLeaf("Equals", effect: true));

        HostedProducerDiscovery<HostedProducerSite> first = EnumerationDiscover(compilation);

        HostedProducerDiscovery<HostedProducerSite> second = EnumerationDiscover(compilation);

        Assert.Equal(first.Items.Select(HostedGrimoireProducerInventory.SiteIdentity), second.Items.Select(HostedGrimoireProducerInventory.SiteIdentity));

        Assert.Equal(first.Diagnostics.Select(static diagnostic => diagnostic.Code + "|" + diagnostic.Identity + "|" + diagnostic.Detail), second.Diagnostics.Select(static diagnostic => diagnostic.Code + "|" + diagnostic.Identity + "|" + diagnostic.Detail));

        Assert.True(first.Diagnostics.Count != 0 || first.Items.Count != 0);
    }
}
