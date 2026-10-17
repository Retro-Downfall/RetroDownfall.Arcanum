using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // A lookup into an analysed tree fails closed. The analysis reads the trees under src/ that are not generated; a symbol whose
    // declaration lies in a tree it does not read (generated code under obj/, or a tree outside src/) has no model there. Each
    // lookup that reaches such a declaration used to throw KeyNotFoundException out of the whole discovery. It now refuses the
    // traversal's completion with HOSTED_TREE_UNANALYSED naming the declaration, and nothing downstream believes the callable.
    private const string UnanalysedTreeCode = HostedGrimoireProducerInventory.UnanalysedTreeCode;

    private const string GeneratedTreeEffect = "System.IO.File.Delete(\"generated-tree-effect\"); ";

    private const string GeneratedTreePath = "src/obj/Holder.g.cs";

    private HostedProducerDiscovery<HostedProducerSite> GeneratedTreeDiscover(string body, string authored, string generated, string generatedPath = GeneratedTreePath)
    {
        CSharpCompilation compilation = Compile("using System.Linq;\n" + R2Source(R2Admission + body + " await Task.CompletedTask;", authored));

        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("using System; using System.Threading.Tasks; " + generated, path: generatedPath));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> discovered = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        foreach (HostedProducerInventoryDiagnostic diagnostic in discovered.Diagnostics.Where(static diagnostic => diagnostic.Code == UnanalysedTreeCode))
        {
            output.WriteLine($"GENERATED_TREE_DIAGNOSTIC {diagnostic.Code} {diagnostic.Identity}");
        }

        return discovered;
    }

    private static void AssertRefusedAtTheGeneratedTree(HostedProducerDiscovery<HostedProducerSite> result, string path = GeneratedTreePath)
    {
        // The refusal names a declaration of the generated tree, and no site of the generated tree's body is believed.
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == UnanalysedTreeCode && diagnostic.Identity.StartsWith(path + ":", StringComparison.Ordinal));

        Assert.DoesNotContain(result.Items, site => site.SourcePath == path);
    }

    // The closed callable of a delegate field: the delegate is invoked in authored code, its field is declared with an initializer
    // in generated code. Resolving the field's value reads the initializer's tree.
    [Theory]
    [InlineData("private static readonly Action Callback = () => { " + GeneratedTreeEffect + "};", "Callback();")]
    [InlineData("private static readonly Action Callback = Effect; private static void Effect() { " + GeneratedTreeEffect + "}", "Callback();")]
    [InlineData("private static Action Callback = () => { " + GeneratedTreeEffect + "};", "Callback();")]
    [InlineData("private static readonly Func<Task> Callback = async () => { await Task.CompletedTask; " + GeneratedTreeEffect + "};", "await Callback();")]
    public void DelegateFieldDeclaredInGeneratedCodeIsRefusedNotThrown(string generated, string call)
    {
        HostedProducerDiscovery<HostedProducerSite> result = GeneratedTreeDiscover(
            "Holder.Run(); ",
            "static partial class Holder { public static async Task Run() { " + call + " } }",
            "static partial class Holder { " + generated + " }");

        AssertRefusedAtTheGeneratedTree(result);
    }

    // The proven-absent test: a delegate the cleanup provenance of a value is read from. The field is declared in generated code,
    // with an initializer or without one; a callable that generated code declares can be assigned there, so it is never proven
    // absent, and the cleanup it would have excused is still unresolved.
    [Theory]
    [InlineData("private static Func<System.IO.Stream?> Open = () => null;")]
    [InlineData("private static Func<System.IO.Stream?>? Open;")]
    [InlineData("private static readonly Func<System.IO.Stream?> Open = static () => new System.IO.MemoryStream();")]
    public void CleanupSeamDeclaredInGeneratedCodeIsRefusedNotThrown(string generated)
    {
        HostedProducerDiscovery<HostedProducerSite> result = GeneratedTreeDiscover(
            "Holder.Run(); ",
            "static partial class Holder { public static void Run() { using System.IO.Stream? stream = Open!(); } }",
            "static partial class Holder { " + generated + " }");

        AssertRefusedAtTheGeneratedTree(result);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
    }

    // A tree outside src/ is as unread as one under obj/.
    [Fact]
    public void DelegateFieldDeclaredOutsideTheAnalysedTreesIsRefusedNotThrown()
    {
        HostedProducerDiscovery<HostedProducerSite> result = GeneratedTreeDiscover(
            "Holder.Run(); ",
            "static partial class Holder { public static async Task Run() { Callback(); } }",
            "static partial class Holder { private static readonly Action Callback = () => { " + GeneratedTreeEffect + "}; }",
            "tools/Holder.cs");

        AssertRefusedAtTheGeneratedTree(result, "tools/Holder.cs");
    }

    // The twin of all of them: the same shapes with the declaration in an analysed tree resolve without the refusal, so the refusal
    // is the unread tree's and not the shape's.
    [Theory]
    [InlineData("private static readonly Action Callback = () => { " + GeneratedTreeEffect + "};", "Callback();")]
    [InlineData("private static Action? Callback;", "Callback?.Invoke();")]
    public void DelegateFieldDeclaredInAnalysedCodeIsNotRefusedAsAnUnanalysedTree(string declaration, string call)
    {
        CSharpCompilation compilation = Compile("using System.Linq;\n" + R2Source(
            R2Admission + "Holder.Run(); await Task.CompletedTask;",
            "static partial class Holder { public static async Task Run() { " + call + " } " + declaration + " }"));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == UnanalysedTreeCode);
    }

    // The pin behind the fixtures above. Every lookup of the model of an analysed tree goes through AnalysedModel, which refuses; an
    // indexer on the dictionary throws for a tree the analysis does not read, and a new one would bring the crash back at a shape no
    // fixture here reaches.
    private static readonly Regex DirectModelIndexer = new(@"\bsemanticModels\s*\[", RegexOptions.CultureInvariant);

    private static string[] DirectModelIndexers(string text) =>
        [.. DirectModelIndexer.Matches(text).Select(static match => match.Value)];

    [Fact]
    public void Every_lookup_of_an_analysed_trees_model_refuses_instead_of_throwing()
    {
        string support = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "tests", "RetroDownfall.Arcanum.Tests", "Support");

        string[] files = [.. Directory.EnumerateFiles(support, "HostedGrimoireProducerInventory*.cs")];

        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            Assert.Empty(DirectModelIndexers(File.ReadAllText(file)));
        }
    }

    [Fact]
    public void The_model_indexer_pin_finds_an_indexer_and_ignores_the_refusing_lookup()
    {
        Assert.Single(DirectModelIndexers("var model = semanticModels[node.SyntaxTree];"));

        Assert.Single(DirectModelIndexers("var model = semanticModels [node.SyntaxTree];"));

        Assert.Empty(DirectModelIndexers("if (AnalysedModel(node) is not { } model) return null;"));

        Assert.Empty(DirectModelIndexers("semanticModels.TryGetValue(node.SyntaxTree, out var model);"));
    }
}
