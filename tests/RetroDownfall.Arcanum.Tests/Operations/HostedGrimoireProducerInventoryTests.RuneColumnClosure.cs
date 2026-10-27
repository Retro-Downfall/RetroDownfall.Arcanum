using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("actual", false)]
    [InlineData("static", false)]
    [InlineData("extension", false)]
    [InlineData("method-group", false)]
    [InlineData("factory", true)]
    [InlineData("conversion", true)]
    [InlineData("expired", true)]
    [InlineData("authored", true)]
    [InlineData("unknown", false)]
    [InlineData("foreign", false)]
    [InlineData("foreign-method-group", false)]
    public void HostedContinuationRuneColumnCalculationKeepsItsActualSourceAndArgumentEffects(string shape, bool effect)
    {
        string calculation = shape switch
        {
            "foreign" => "_ = untrusted::Terminal.Gui.Text.RuneExtensions.GetColumns(new System.Text.Rune('x'));",
            "foreign-method-group" => "System.Func<System.Text.Rune, int> width = untrusted::Terminal.Gui.Text.RuneExtensions.GetColumns; _ = width(new System.Text.Rune('x'));",
            "actual" => "_ = RetroDownfall.Arcanum.Cli.CommandCenter.TerminalCellMetrics.MeasureWidth(\"wide\u4e2d\");",
            "extension" => "_ = new System.Text.Rune('x').GetColumns();",
            "method-group" => "System.Func<System.Text.Rune, int> width = Terminal.Gui.Text.RuneExtensions.GetColumns; _ = width(new System.Text.Rune('x'));",
            "factory" or "expired" => "_ = Terminal.Gui.Text.RuneExtensions.GetColumns(RuneColumnInput.Effect());",
            "conversion" => "_ = Terminal.Gui.Text.RuneExtensions.GetColumns(new RuneColumnInput());",
            "authored" => "_ = RuneColumnInput.GetColumns(new System.Text.Rune('x'));",
            "unknown" => "_ = Terminal.Gui.Text.RuneExtensions.GetColumns(RuneColumnInput.Unknown());",
            _ => "_ = Terminal.Gui.Text.RuneExtensions.GetColumns(new System.Text.Rune('x'));",
        };

        string helper = "internal sealed class RuneColumnInput { internal static System.Text.Rune Effect() { System.IO.File.Delete(\"rune-factory\"); return new('x'); } internal static extern System.Text.Rune Unknown(); public static implicit operator System.Text.Rune(RuneColumnInput value) { System.IO.File.Delete(\"rune-conversion\"); return new('x'); } internal static int GetColumns(System.Text.Rune rune) { System.IO.File.Delete(\"authored-rune-columns\"); return 1; } }";

        const string path = "src/RetroDownfall.Arcanum.Cli/CommandCenter/TerminalCellMetrics.cs";

        SyntaxTree actual = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), path)), path: path);

        string body = R2Admission + (shape == "expired" ? "held.Dispose(); lease.Dispose(); " : "") + calculation + " await Task.CompletedTask;";

        bool foreign = shape.StartsWith("foreign", StringComparison.Ordinal);

        CSharpCompilation compilation = CompileWithProductionReferencePack((foreign ? "extern alias untrusted; " : "") + "using Terminal.Gui.Text; " + R2Source(body, helper), "RetroDownfall.Arcanum.Cli")
            .AddSyntaxTrees(actual, CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;", path: "src/Fixture.GlobalUsings.g.cs"));

        if (foreign)
        {
            CSharpCompilation untrusted = CompileWithProductionReferencePack("namespace Terminal.Gui.Text { public static class RuneExtensions { public static int GetColumns(System.Text.Rune rune) { System.IO.File.Delete(\"untrusted-column-effect\"); return 1; } } }", "RetroDownfall.Arcanum.Cli")
                .WithAssemblyName("UntrustedRuneMetadataFixture");

            using MemoryStream bytes = new();

            Microsoft.CodeAnalysis.Emit.EmitResult emitted = untrusted.Emit(bytes);

            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));

            compilation = compilation.AddReferences(MetadataReference.CreateFromImage(bytes.ToArray(), new MetadataReferenceProperties(aliases: ["untrusted"])));
        }

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (shape == "foreign-method-group")
        {
            // An untrusted metadata delegate keeps its existing callback refusal;
            // the exact leaf profile grants no unknown target or lifetime.
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
        }
        else if (shape == "method-group")
        {
            Assert.Empty(result.Diagnostics);
        }
        else
        {
            Assert.Equal(foreign, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "Terminal.Gui.Text.RuneExtensions.GetColumns"));
        }

        Assert.Equal(shape == "unknown", result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "RuneColumnInput.Unknown"));

        Assert.Equal(effect, result.Items.Any(static site => site.Callee == "System.IO.File.Delete" && site.EnclosingType == "RuneColumnInput"));

        if (effect)
        {
            HostedProducerSite argumentEffect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.EnclosingType == "RuneColumnInput");

            Assert.Equal(shape != "expired", argumentEffect.WorkFrontierCapsuleId is not null);

            Assert.Equal(shape != "expired", argumentEffect.EffectFrontierCapsuleId is not null);
        }
    }
}
