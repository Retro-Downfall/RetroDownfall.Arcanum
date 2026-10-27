using Microsoft.CodeAnalysis;

using Xunit;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("direct", false, true, false)]
    [InlineData("direct", false, true, true)]
    [InlineData("record-scalar", false, false, false)]
    [InlineData("record-scalar", false, false, true)]
    [InlineData("record-effect", false, true, false)]
    [InlineData("record-effect", false, true, true)]
    [InlineData("record-object", false, true, false)]
    [InlineData("record-object", false, true, true)]
    [InlineData("record-nested", false, true, false)]
    [InlineData("record-nested", false, true, true)]
    [InlineData("record-rest", false, true, false)]
    [InlineData("record-rest", false, true, true)]
    [InlineData("record-expired", false, true, false)]
    [InlineData("record-expired", false, true, true)]
    [InlineData("written", true, false, false)]
    [InlineData("written", true, false, true)]
    [InlineData("record-deep", true, false, false)]
    [InlineData("record-deep", true, false, true)]
    public void HostedContinuationTupleFormattingTraversesTheStoredElements(string shape, bool refused, bool effect, bool referencePack)
    {
        string tupleType = shape switch
        {
            "record-scalar" => "(int, string)",
            "record-object" => "(int, object)",
            "record-nested" => "(int, (string, TupleFormatEffect))",
            "record-deep" => "(int, (int, (int, (int, (int, TupleFormatEffect)))))",
            "record-rest" => "(int, int, int, int, int, int, int, TupleFormatEffect)",
            _ => "(int, TupleFormatEffect)",
        };

        string value = shape switch
        {
            "record-scalar" => "(1, \"stored\")",
            "record-nested" => "(1, (\"stored\", new TupleFormatEffect()))",
            "record-deep" => "(1, (2, (3, (4, (5, new TupleFormatEffect())))))",
            "record-rest" => "(1, 2, 3, 4, 5, 6, 7, new TupleFormatEffect())",
            _ => "(1, new TupleFormatEffect())",
        };

        string formatting = shape switch
        {
            "direct" => "_ = $\"{(1, new TupleFormatEffect())}\";",
            "written" => "_ = (1, \"stored\").ToString();",
            _ => "_ = $\"{new TupleFormatRecord(" + value + ")}\";",
        };

        string body = R2Admission + (shape == "record-expired" ? "held.Dispose(); lease.Dispose(); " : "") + formatting + " await Task.CompletedTask;";

        string helper = "internal sealed record TupleFormatRecord(" + tupleType + " Value); internal sealed class TupleFormatEffect { public override string ToString() { System.IO.File.Delete(\"tuple-format-effect\"); return \"effect\"; } }";

        string source = R2Source(body, helper);

        Microsoft.CodeAnalysis.CSharp.CSharpCompilation compilation = referencePack
            ? CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Infrastructure")
            : Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Equal(refused, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail.StartsWith("System.ValueTuple`", StringComparison.Ordinal)));

        Assert.Equal(effect, result.Items.Any(static site => site.Callee == "System.IO.File.Delete" && site.EnclosingType == "TupleFormatEffect"));

        if (effect)
        {
            HostedProducerSite storedEffect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.EnclosingType == "TupleFormatEffect");

            Assert.Equal(shape != "record-expired", storedEffect.WorkFrontierCapsuleId is not null);

            Assert.Equal(shape != "record-expired", storedEffect.EffectFrontierCapsuleId is not null);
        }
    }
}
