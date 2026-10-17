using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("callback-local")]
    [InlineData("callback-lambda")]
    [InlineData("start-local")]
    [InlineData("start-lambda")]
    public void ServeSourceAssociationCannotUseAwaitOnlyInsideAnUninvokedCallable(string shape)
    {
        CSharpCompilation original = Assert.Single(HostedGrimoireProducerInventory.ProductionCompilations,
            static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Cli");

        var tree = Assert.Single(original.SyntaxTrees,
            static candidate => candidate.FilePath == "src/RetroDownfall.Arcanum.Cli/Commands/ServeCommand.cs");

        Assert.Empty(original.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.True(HasServeSourceAssociation(original, tree, hosted: true));

        string text = tree.GetText().ToString();

        string expected = shape.StartsWith("callback", StringComparison.Ordinal)
            ? "return await startHost(cancellationToken).ConfigureAwait(false);"
            : "await app.StartAsync(cancellationToken).ConfigureAwait(false);";

        string replacement = shape switch
        {
            "callback-local" => "async Task<int> UninvokedSourceCallback() { return await startHost(cancellationToken).ConfigureAwait(false); } return 0;",
            "callback-lambda" => "Func<Task<int>> uninvokedSourceCallback = async () => await startHost(cancellationToken).ConfigureAwait(false); return 0;",
            "start-local" => "async Task UninvokedSourceStart() { await app.StartAsync(cancellationToken).ConfigureAwait(false); }",
            _ => "Func<Task> uninvokedSourceStart = async () => { await app.StartAsync(cancellationToken).ConfigureAwait(false); };",
        };

        text = text.ReplaceSdkFixtureText(expected, replacement, StringComparison.Ordinal);

        var overlay = CSharpSyntaxTree.ParseText(text, (CSharpParseOptions)tree.Options, tree.FilePath);

        CSharpCompilation candidate = original.ReplaceSyntaxTree(tree, overlay);

        Assert.Empty(candidate.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.False(HasServeSourceAssociation(candidate, overlay, hosted: true));
    }
}
