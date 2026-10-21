using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void Actual_unicode_nfc_computation_is_effect_free_with_a_bounded_source_graph()
    {
        CSharpCompilation compilation = Compile(R2Source(
            "_ = RetroDownfall.Arcanum.Core.Primitives.UnicodeNfc.Normalize(\"Café/각\"); await Task.CompletedTask;"));

        string root = TestRepositoryPaths.RepositoryRoot();

        string[] sources =
        [
            "src/RetroDownfall.Arcanum.Core/Primitives/UnicodeNfc.cs",
            "src/RetroDownfall.Arcanum.Core/Primitives/StrictUtf8.cs",
            "src/RetroDownfall.Arcanum.Core/Covenant/CovenantUnicode17Tables.g.cs",
        ];

        compilation = compilation.AddSyntaxTrees(sources.Select(path =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path)), path: path)));

        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "global using System;", path: "src/Fixture.GlobalUsings.g.cs"));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        // This isolated closure uses the real normalizer, encoder and Unicode tables. A stricter
        // local budget exposes recursive context amplification without running the restore root
        // or changing the production traversal cap, its admitted roots or its headroom ceilings.
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], [],
            maximumAnalyzedStatesPerRoot: 256);

        Assert.True(result.Diagnostics.Count == 0,
            string.Join(global::System.Environment.NewLine, result.Diagnostics));

        Assert.Empty(result.Items);

        HostedProducerRootTraversalMetric traversal = Assert.Single(result.AnalysisMetrics!.RootTraversals,
            static metric => metric.RootOperation == "Worker.StartAsync");

        output.WriteLine($"Unicode NFC authored closure states: {traversal.AnalyzedStates}");

        Assert.InRange(traversal.AnalyzedStates, 1, 256);
    }
}
