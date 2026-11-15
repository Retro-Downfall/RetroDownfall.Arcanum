using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentDictionaryReferenceContractPreservesConsumedSelectorEffects(bool effectful)
    {
        string selector = effectful
            ? "entry => { System.IO.File.Delete(\"selector\"); return entry.Value; }"
            : "entry => entry.Value";

        string body = R2Admission + "_ = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OrderBy(Cache.Values, "
            + selector + ")); await Task.CompletedTask;";

        const string cache = "static class Cache { internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Values = new(); }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body, cache), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        INamedTypeSymbol dictionary = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("System.Collections.Concurrent.ConcurrentDictionary`2"));

        Assert.Equal("System.Collections.Concurrent", dictionary.ContainingAssembly.Identity.Name);

        Assert.Equal("System.Private.CoreLib", typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).Assembly.GetName().Name);

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.False(EnumerationOwnershipRefused(result, "System.Linq.Enumerable.OrderBy"),
            string.Join(global::System.Environment.NewLine, result.Diagnostics));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Concurrent.ConcurrentDictionary`2.GetEnumerator");

        if (effectful)
        {
            AssertEnumerationSite(result, admitted: true);
        }
        else
        {
            Assert.Empty(EnumerationDeleteSites(result));
        }
    }

    [Theory]
    [InlineData("lookup", "_ = Cache.Values.GetValueOrDefault(\"key\");")]
    [InlineData("where-select-spread", "int[] values = [.. System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(Cache.Values, static entry => true), static entry => entry.Value)]; _ = values;")]
    public void ReadonlyConcurrentDictionaryOriginsPreserveLookupAndConsumedPipeline(string shape, string operation)
    {
        const string cache = "static class Cache { internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Values = new(); }";

        CSharpCompilation compilation = CompileWithProductionReferencePack("using System.Collections.Generic;\n" + R2Source(R2Admission + operation + " await Task.CompletedTask;", cache), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.True(result.Diagnostics.Count == 0, shape + ": " + string.Join(global::System.Environment.NewLine, result.Diagnostics));
    }

    [Fact]
    public void AuthoredConcurrentDictionaryLookalikeDoesNotAcquireClosedEnumerationTrust()
    {
        const string counterfeit = "namespace System.Collections.Concurrent { public sealed class ConcurrentDictionary<TKey, TValue> : System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<TKey, TValue>> { "
            + "public System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<TKey, TValue>> GetEnumerator() { System.IO.File.Delete(\"authored-enumerator\"); return new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<TKey, TValue>>().GetEnumerator(); } "
            + "System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } }";

        string body = R2Admission + "var values = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(); _ = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OrderBy(values, entry => entry.Value)); await Task.CompletedTask;";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body, counterfeit), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.True(EnumerationOwnershipRefused(result, "System.Linq.Enumerable.OrderBy")
            || EnumerationDeleteSites(result).Length != 0,
            "An authored enumerator cannot disappear behind the framework dictionary spelling.");
    }

    [Fact]
    public void ForeignMetadataConcurrentDictionaryDoesNotAcquireClosedEnumerationTrust()
    {
        const string counterfeit = "namespace System.Collections.Concurrent { public sealed class ConcurrentDictionary<TKey, TValue> : System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<TKey, TValue>> { "
            + "public System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<TKey, TValue>> GetEnumerator() { System.IO.File.Delete(\"foreign-enumerator\"); return new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<TKey, TValue>>().GetEnumerator(); } "
            + "System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } }";

        CSharpCompilation foreign = CompileWithProductionReferencePack(counterfeit, "RetroDownfall.Arcanum.Api")
            .WithAssemblyName("Untrusted.ConcurrentDictionary");

        using MemoryStream image = new();

        Assert.True(foreign.Emit(image).Success);

        CSharpCompilation compilation = CompileWithProductionReferencePack("extern alias counterfeit; " + R2Source(R2Admission
            + "var values = new counterfeit::System.Collections.Concurrent.ConcurrentDictionary<string, int>(); _ = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OrderBy(values, entry => entry.Value)); await Task.CompletedTask;"), "RetroDownfall.Arcanum.Api")
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray(), new MetadataReferenceProperties(aliases: ["counterfeit"])));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.True(EnumerationOwnershipRefused(result, "System.Linq.Enumerable.OrderBy"));
    }

    [Fact]
    public void UnconsumedConcurrentDictionarySelectorDoesNotInheritAdmission()
    {
        const string cache = "static class Cache { internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Values = new(); }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission
            + "var pending = System.Linq.Enumerable.OrderBy(Cache.Values, entry => { System.IO.File.Delete(\"unconsumed-selector\"); return entry.Value; }); _ = pending; await Task.CompletedTask;", cache), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.NotEmpty(EnumerationDeleteSites(result));

        Assert.All(EnumerationDeleteSites(result), static site =>
        {
            Assert.Null(site.WorkFrontierCapsuleId);

            Assert.Null(site.EffectFrontierCapsuleId);
        });

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }
}
