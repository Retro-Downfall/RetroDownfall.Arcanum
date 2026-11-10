using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void GuildMemoryReachableHostedRootsRetainTheirClosedAuthorityProof()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        Assert.Contains(ObserveFullProductionRoot(result, "SagaExtractionService"), static site =>
            site.Kind == HostedProducerSiteKind.DatabaseAccess);

        Assert.Contains(ObserveFullProductionRoot(result, "SessionAttachmentIndexingService"), static site =>
            site.Kind == HostedProducerSiteKind.FileSystemRead);

        Assert.True(result.Diagnostics.Count == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(result.Diagnostics));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameworkStreamBaseConstructionDoesNotHideAnAuthoredConstructorEffect(bool effect)
    {
        string helper = "sealed class ReadWrapper : System.IO.Stream { public ReadWrapper() { "
            + (effect ? "System.IO.File.Delete(\"constructor-effect\");" : "")
            + " } public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; "
            + "public override long Length => 0; public override long Position { get => 0; set { } } public override void Flush() { } "
            + "public override int Read(byte[] buffer, int offset, int count) => 0; public override long Seek(long offset, System.IO.SeekOrigin origin) => 0; "
            + "public override void SetLength(long value) { } public override void Write(byte[] buffer, int offset, int count) { } }";

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(
            CensusCompileSource("_ = new ReadWrapper();", helper, admitted: false));

        Assert.DoesNotContain(result.Items, static site => site.Callee == "System.IO.Stream..ctor");

        Assert.Equal(effect, result.Items.Any(static site => site.Callee == "System.IO.File.Delete"));

        Assert.Equal(effect, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));

        if (!effect)
        {
            Assert.Empty(result.Diagnostics);
        }
    }

    [Fact]
    public void CounterfeitStreamBaseConstructorRemainsEffectful()
    {
        CSharpCompilation foreign = Compile("namespace System.IO { public class Stream { protected Stream() { File.Delete(\"counterfeit\"); } } }")
            .WithAssemblyName("ForeignStreamConstructor");

        using MemoryStream bytes = new();

        Assert.True(foreign.Emit(bytes).Success);

        PortableExecutableReference reference = MetadataReference.CreateFromImage(bytes.ToArray(),
            new MetadataReferenceProperties(aliases: ["counterfeit"]));

        CSharpCompilation compilation = Compile("extern alias counterfeit; " + R2Source("_ = new Foreign(); await Task.CompletedTask;",
            "sealed class Foreign : counterfeit::System.IO.Stream { } ")).AddReferences(reference);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.Stream..ctor");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.Stream..ctor");
    }

    [Fact]
    public void ParameterCollectionCountRequiresTheClosedProviderDispatchProof()
    {
        const string statement = "_ = DispatchInput.Parameters.Count;";

        const string callee = "System.Data.Common.DbParameterCollection.Count";

        Assert.True(DispatchRefused(DispatchDiscover(statement), callee));

        HostedProducerDiscovery<HostedProducerSite> closed = DispatchDiscoverUnder(
            HostedProducerPremises.DbProviderTypesClosedByUse, statement);

        Assert.Empty(closed.Diagnostics);

        const string authored = "internal abstract class EffectfulParameters : System.Data.Common.DbParameterCollection { public override int Count { get { System.IO.File.Delete(\"count-effect\"); return 0; } } }";

        HostedProducerDiscovery<HostedProducerSite> open = DispatchDiscoverUnder(
            HostedProducerPremises.DbProviderTypesClosedByUse, statement, authored);

        Assert.NotEmpty(PremiseViolations(open));

        Assert.True(DispatchRefused(open, callee));
    }
}
