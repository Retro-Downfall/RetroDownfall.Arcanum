using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // The inventory proves that every awaitable the Long Rest worker's wake starts is awaited where it starts, so no
    // timer-backed or channel task outlives the call. It places no disposal obligation on a CancellationTokenSource: the
    // constructor and CreateLinkedTokenSource are allow-listed, so an undisposed timer source or an undisposed linked source
    // (which keeps one registration on the host's stopping token per wake) is proven with no site, exactly like the disposed
    // shape. Disposal of the worker's sources is therefore pinned here, from the production syntax, and by the armed-timer
    // counts of the Parked Rescan behaviour tests.
    private static (string Creation, bool DisposedByUsing)[] CancellationSourceCreations(SemanticModel model, SyntaxNode scope)
    {
        static bool IsCancellationTokenSource(ITypeSymbol? type) => type?.ToDisplayString() == "System.Threading.CancellationTokenSource";

        List<(string Creation, bool DisposedByUsing)> creations = [];

        foreach (SyntaxNode node in scope.DescendantNodes())
        {
            bool creates = node switch
            {
                BaseObjectCreationExpressionSyntax => IsCancellationTokenSource(model.GetTypeInfo(node).Type),
                InvocationExpressionSyntax invocation => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "CreateLinkedTokenSource", IsStatic: true } factory
                    && IsCancellationTokenSource(factory.ContainingType),
                _ => false,
            };

            if (!creates) continue;

            // The source is the whole initializer of a local that a `using` declaration or statement owns.
            bool owned = node.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }
                && (declaration.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } || declaration.Parent is UsingStatementSyntax);

            creations.Add((node.ToString(), owned));
        }

        return [.. creations];
    }

    [Fact]
    public void TheLongRestWakeDisposesBothOfItsCancellationSourcesBeforeItReturns()
    {
        CSharpCompilation infrastructure = HostedGrimoireProducerInventory.ProductionCompilations
            .Single(static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        INamedTypeSymbol worker = Assert.IsAssignableFrom<INamedTypeSymbol>(
            infrastructure.GetTypeByMetadataName("RetroDownfall.Arcanum.Infrastructure.Hosting.LongRestSweepHostedService"));

        MethodDeclarationSyntax wake = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(
            Assert.Single(worker.GetMembers("WaitForWakeAsync")).DeclaringSyntaxReferences).GetSyntax());

        SemanticModel model = infrastructure.GetSemanticModel(wake.SyntaxTree);

        (string Creation, bool DisposedByUsing)[] sources = CancellationSourceCreations(model, wake);

        // The TimeProvider-backed rescan source and the source linked over the stopping token and the rescan token.
        Assert.Equal(2, sources.Length);

        Assert.Contains(sources, static source => source.Creation.StartsWith("new(", StringComparison.Ordinal) && source.Creation.EndsWith("timeProvider)", StringComparison.Ordinal));

        Assert.Contains(sources, static source => source.Creation == "CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, rescan.Token)");

        Assert.All(sources, static source => Assert.True(source.DisposedByUsing, source.Creation));

        // No other method of the worker may create a source it does not dispose either.
        foreach (SyntaxReference part in worker.DeclaringSyntaxReferences)
        {
            SyntaxNode declaration = part.GetSyntax();

            Assert.All(CancellationSourceCreations(infrastructure.GetSemanticModel(declaration.SyntaxTree), declaration),
                static source => Assert.True(source.DisposedByUsing, source.Creation));
        }
    }

    [Theory]
    [InlineData("disposed", 2, 2)]
    [InlineData("timer-source-not-disposed", 2, 1)]
    [InlineData("linked-source-not-disposed", 2, 1)]
    [InlineData("source-passed-as-an-argument", 1, 0)]
    public void ThePinOnTheWakeSourcesSeesAnUndisposedSource(string shape, int created, int disposed)
    {
        string body = shape switch
        {
            "disposed" => """
                using System.Threading.CancellationTokenSource rescan = new(delay, clock);

                using System.Threading.CancellationTokenSource wake = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, rescan.Token);
                """,
            "timer-source-not-disposed" => """
                System.Threading.CancellationTokenSource rescan = new(delay, clock);

                using System.Threading.CancellationTokenSource wake = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, rescan.Token);
                """,
            "linked-source-not-disposed" => """
                using System.Threading.CancellationTokenSource rescan = new(delay, clock);

                System.Threading.CancellationTokenSource wake = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, rescan.Token);
                """,
            "source-passed-as-an-argument" => "System.GC.KeepAlive(new System.Threading.CancellationTokenSource(delay, clock));",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        CSharpCompilation compilation = Compile("static class Waiter { internal static void Wait(System.TimeSpan delay, System.TimeProvider clock, System.Threading.CancellationToken stoppingToken) { " + body + " } }");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        (string Creation, bool DisposedByUsing)[] sources = CancellationSourceCreations(compilation.GetSemanticModel(tree), tree.GetRoot());

        Assert.Equal(created, sources.Length);

        Assert.Equal(disposed, sources.Count(static source => source.DisposedByUsing));
    }
}
