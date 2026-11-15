using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("exact-new")]
    [InlineData("authored-derived")]
    [InlineData("foreign-derived")]
    [InlineData("opaque")]
    public void ConcurrentDictionaryBaseTypeCannotHideAnEnumerationReimplementation(string shape)
    {
        const string derivedSource = """
            public sealed class ReimplementedConcurrent : System.Collections.Concurrent.ConcurrentDictionary<int, int>,
                System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<int, int>>
            {
                System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<int, int>>
                    System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<int, int>>.GetEnumerator()
                {
                    System.IO.File.Delete("concurrent-interface-reimplementation");

                    return ((System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<int, int>>)
                        System.Array.Empty<System.Collections.Generic.KeyValuePair<int, int>>()).GetEnumerator();
                }

                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
                    ((System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<int, int>>)this).GetEnumerator();
            }
            """;

        string initializer = shape switch
        {
            "exact-new" => "new System.Collections.Concurrent.ConcurrentDictionary<int, int>()",
            "authored-derived" => "new ReimplementedConcurrent()",
            "foreign-derived" => "new ForeignSequences.ReimplementedConcurrent()",
            "opaque" => "null!",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        string input = shape == "opaque"
            ? "static class ConcurrentReimplementationInput { internal static System.Collections.Concurrent.ConcurrentDictionary<int, int> Values { get; set; } = null!; }"
            : "static class ConcurrentReimplementationInput { internal static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> Values = " + initializer + "; }";

        // The exact-new positive keeps the hostile authored subclass in its universe.
        string helpers = input + (shape is "exact-new" or "authored-derived" ? derivedSource : string.Empty);

        string body = R2Admission
            + "_ = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(ConcurrentReimplementationInput.Values, static pair => true));"
            + " await Task.CompletedTask;";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body, helpers), "RetroDownfall.Arcanum.Api");

        if (shape == "foreign-derived")
        {
            CSharpCompilation foreign = CompileWithProductionReferencePack(
                "namespace ForeignSequences { " + derivedSource + " }", "RetroDownfall.Arcanum.Api")
                .WithAssemblyName("Foreign.ConcurrentReimplementation");

            using MemoryStream image = new();

            Assert.True(foreign.Emit(image).Success);

            compilation = compilation.AddReferences(MetadataReference.CreateFromImage(image.ToArray()));
        }

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        INamedTypeSymbol dictionary = Assert.IsAssignableFrom<INamedTypeSymbol>(
            compilation.GetTypeByMetadataName("System.Collections.Concurrent.ConcurrentDictionary`2"));

        Assert.Equal("System.Collections.Concurrent", dictionary.ContainingAssembly.Identity.Name);

        Assert.Equal("System.Private.CoreLib", typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).Assembly.GetName().Name);

        Assert.False(dictionary.IsSealed);

        Assert.Empty(dictionary.DeclaringSyntaxReferences);

        if (shape != "opaque")
        {
            string name = shape == "foreign-derived" ? "ForeignSequences.ReimplementedConcurrent" : "ReimplementedConcurrent";

            INamedTypeSymbol derived = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName(name));

            Assert.True(SymbolEqualityComparer.Default.Equals(derived.BaseType!.OriginalDefinition, dictionary));

            INamedTypeSymbol contract = Assert.Single(derived.AllInterfaces, static candidate =>
                candidate.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>");

            IMethodSymbol slot = Assert.IsAssignableFrom<IMethodSymbol>(Assert.Single(contract.GetMembers("GetEnumerator")));

            IMethodSymbol implementation = Assert.IsAssignableFrom<IMethodSymbol>(derived.FindImplementationForInterfaceMember(slot));

            Assert.True(SymbolEqualityComparer.Default.Equals(implementation.ContainingType, derived));

            Assert.Contains(implementation.ExplicitInterfaceImplementations, candidate =>
                SymbolEqualityComparer.Default.Equals(candidate, slot));

            if (shape == "foreign-derived")
            {
                Assert.Empty(implementation.DeclaringSyntaxReferences);

                Assert.Equal("Foreign.ConcurrentReimplementation", implementation.ContainingAssembly.Identity.Name);

                MetadataReference reference = Assert.Single(compilation.References, candidate =>
                    SymbolEqualityComparer.Default.Equals(compilation.GetAssemblyOrModuleSymbol(candidate), implementation.ContainingAssembly));

                Assert.IsAssignableFrom<PortableExecutableReference>(reference);
            }
        }

        if (shape is "foreign-derived" or "opaque")
        {
            HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises(
                [compilation], HostedProducerPremises.AuthoredEnumerablesAbsentByUse);

            // This existing premise excludes analyzed-source implementations only.
            // It cannot turn this foreign or opaque value into the base runtime type.
            Assert.True(ledger.Holds(HostedProducerPremises.AuthoredEnumerablesAbsentByUse));

            Assert.Empty(ledger.Violations);
        }

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        if (shape == "exact-new")
        {
            Assert.Empty(result.Diagnostics);

            Assert.Empty(EnumerationDeleteSites(result));
        }
        else
        {
            // Require the specific strict enumeration refusal, not an unrelated
            // foreign constructor, premise, or missing-root diagnostic.
            Assert.True(EnumerationOwnershipRefused(result, "System.Linq.Enumerable.Where"),
                string.Join(global::System.Environment.NewLine, result.Diagnostics));
        }
    }
}
