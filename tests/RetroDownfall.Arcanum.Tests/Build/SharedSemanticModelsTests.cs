using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Collections;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The semantic models of the production compilations. A model keeps everything it has bound, so a second table of models for the Build
/// scans held a second copy of everything the scans bound, and all of it stayed in the fixture lane's one test host to the end of the run.
/// The scans and the checker read one model per tree, and a scan class lets the models go when it is done.
/// </summary>
[Collection(HostedProducerAnalysisCollection.Name)]
[Trait("Category", "HostedProducerAnalysis")]
public sealed class SharedSemanticModelsTests
{
    private static CSharpCompilation Compilation() =>
        CSharpCompilation.Create(
            "ModelFixture",
            [CSharpSyntaxTree.ParseText("public sealed class Holder { public int Value => 1; }", path: "src/Fixture.cs")],
            TrustedPlatformReferences.Shared,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    [Fact]
    public void The_scans_and_the_checker_read_the_same_model_of_a_tree()
    {
        CSharpCompilation compilation = Compilation();

        SyntaxTree tree = compilation.SyntaxTrees.Single();

        SemanticModel scans = SharedSemanticModels.Of(compilation, tree);

        Assert.Same(scans, HostedGrimoireProducerInventory.ProcessSemanticModel(compilation, tree));

        Assert.Same(scans, SharedSemanticModels.Of(compilation, tree));
    }

    [Fact]
    public void Releasing_the_models_makes_the_next_request_build_a_new_one_that_answers_the_same()
    {
        CSharpCompilation compilation = Compilation();

        SyntaxTree tree = compilation.SyntaxTrees.Single();

        SemanticModel before = SharedSemanticModels.Of(compilation, tree);

        string declaredBefore = Declared(before, tree);

        HostedGrimoireProducerInventory.ReleaseProcessSemanticModels();

        SemanticModel after = SharedSemanticModels.Of(compilation, tree);

        Assert.NotSame(before, after);

        Assert.Equal(declaredBefore, Declared(after, tree));
    }

    [Fact]
    public void Every_Build_scan_class_that_reads_the_production_compilations_lets_the_models_go_when_it_is_done()
    {
        string build = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "tests", "RetroDownfall.Arcanum.Tests", "Build");

        string[] offenders =
        [
            .. Directory.EnumerateFiles(build, "*.cs")
                .Where(static path => !string.Equals(Path.GetFileName(path), nameof(SharedSemanticModelsTests) + ".cs", StringComparison.Ordinal))
                .Select(static path => (Path: path, Text: File.ReadAllText(path)))
                .Where(static file => file.Text.Contains("HostedProducerAnalysisCollection.Name", StringComparison.Ordinal)
                    && (file.Text.Contains("ProductionCompilations", StringComparison.Ordinal)
                        || file.Text.Contains("SharedSemanticModels", StringComparison.Ordinal)
                        || file.Text.Contains("CheckerLimitScans", StringComparison.Ordinal)))
                .Where(static file => !file.Text.Contains("IClassFixture<SemanticModelScope>", StringComparison.Ordinal))
                .Select(static file => Path.GetFileName(file.Path)),
        ];

        Assert.Empty(offenders);
    }

    /// <summary>The members a model declares for the one class of a tree, which a rebuilt model answers identically.</summary>
    private static string Declared(SemanticModel model, SyntaxTree tree)
    {
        ClassDeclarationSyntax declaration = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single();

        INamedTypeSymbol type = model.GetDeclaredSymbol(declaration)!;

        return string.Join(",", type.GetMembers().Select(static member => member.ToDisplayString()));
    }
}
