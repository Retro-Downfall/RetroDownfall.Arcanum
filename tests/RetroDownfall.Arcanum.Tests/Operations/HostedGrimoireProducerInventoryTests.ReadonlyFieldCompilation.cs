using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameworkEntryCleanupUsesReferencedReadonlyInitializerCompilation(bool effectful)
    {
        CSharpCompilation library = Compile("""
            public sealed class ReferencedOwner : System.IDisposable
            {
                private readonly ReferencedCleanup inner = new ReferencedCleanup();

                public void Dispose() => inner.Dispose();
            }

            public sealed class ReferencedCleanup : System.IDisposable
            {
                public void Dispose() { CLEANUP }
            }
            """.Replace("CLEANUP", effectful ? "System.IO.File.Delete(\"effect\");" : "", StringComparison.Ordinal))
            .WithAssemblyName("ReadonlyInitializerLibrary");

        CSharpCompilation consumer = Compile("""
            public sealed class Inspect
            {
                public int Value
                {
                    get
                    {
                        using var owned = new ReferencedOwner();

                        return 1;
                    }
                }
            }
            """)
            .WithAssemblyName("ReadonlyInitializerConsumer")
            .AddReferences(library.ToMetadataReference());

        Assert.All(new[] { library, consumer }, static compilation => Assert.Empty(compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

        SyntaxTree consumerTree = Assert.Single(consumer.SyntaxTrees);

        ObjectCreationExpressionSyntax creation = Assert.Single(consumerTree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>());

        IMethodSymbol constructor = Assert.IsAssignableFrom<IMethodSymbol>(consumer.GetSemanticModel(consumerTree).GetSymbolInfo(creation).Symbol);

        Assert.True(constructor.IsImplicitlyDeclared);

        IFieldSymbol field = Assert.IsAssignableFrom<IFieldSymbol>(Assert.Single(constructor.ContainingType.GetMembers("inner")));

        SyntaxTree initializerTree = Assert.Single(field.DeclaringSyntaxReferences).SyntaxTree;

        Assert.Contains(initializerTree, library.SyntaxTrees);

        Assert.DoesNotContain(initializerTree, consumer.SyntaxTrees);

        var reached = HostedGrimoireProducerInventory.ProbeAllFrameworkEntryPointsThatReach([library, consumer]);

        bool accessorReaches = reached[HostedGrimoireProducerInventory.FrameworkEntryPoints.Accessors]
            .Any(static finding => finding.StartsWith("Inspect.get_Value :: ", StringComparison.Ordinal));

        Assert.True(effectful == accessorReaches, string.Join("\n", reached.SelectMany(static pair => pair.Value.Select(finding => pair.Key + ": " + finding))));
    }
}
