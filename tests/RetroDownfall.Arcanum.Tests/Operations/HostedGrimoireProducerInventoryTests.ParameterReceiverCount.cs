using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string ParameterOverrideSources = """
        internal sealed class AuthoredSqliteParameters : Microsoft.Data.Sqlite.SqliteParameterCollection
        {
            public override int Count
            {
                get
                {
                    System.IO.File.Delete("parameter-count-override");

                    return 0;
                }
            }

            public override void Clear() => System.IO.File.Delete("parameter-clear-override");
        }

        internal static class ParameterReceiverInput
        {
            internal static Microsoft.Data.Sqlite.SqliteParameterCollection Value { get; set; } = null!;
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtensibleSqliteParameterCountCannotBorrowANonextensibleReceiverProof(bool production)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(
            R2Source(R2Admission + "_ = ParameterReceiverInput.Value.Count; await Task.CompletedTask;", ParameterOverrideSources),
            "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        INamedTypeSymbol collection = Assert.IsAssignableFrom<INamedTypeSymbol>(
            compilation.GetTypeByMetadataName("Microsoft.Data.Sqlite.SqliteParameterCollection"));

        Assert.Empty(collection.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, collection.ContainingAssembly);

        Assert.False(collection.IsSealed);

        Assert.Contains(collection.InstanceConstructors, static constructor =>
            constructor.Parameters.IsEmpty && constructor.DeclaredAccessibility == Accessibility.ProtectedOrInternal);

        IPropertySymbol count = Assert.IsAssignableFrom<IPropertySymbol>(Assert.Single(collection.GetMembers("Count")));

        Assert.True(count.GetMethod!.IsVirtual || count.GetMethod.IsOverride);

        AssertAuthoredSqliteParametersViolateProviderClosure(compilation);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(compilation, production);

        // A failed global premise is evidence about the universe, not a substitute for
        // refusing this opaque receiver in the fallback per-site model.
        Assert.True(DispatchRefused(result, "Microsoft.Data.Sqlite.SqliteParameterCollection.Count"),
            string.Join(global::System.Environment.NewLine, result.Diagnostics));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactFrameworkParameterCountRemainsClosedBesideAnAuthoredSubclass(bool widened)
    {
        string declaration = widened ? "System.Data.Common.DbCommand" : "var";

        HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(
            declaration + " command = new Microsoft.Data.Sqlite.SqliteConnection().CreateCommand(); _ = command.Parameters.Count;",
            ParameterOverrideSources);

        Assert.Empty(result.Diagnostics);
    }
}
