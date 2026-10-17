using Microsoft.CodeAnalysis;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CampaignModelProductionSelectorNeedsNoOwnedSettingsToWalkModels(bool owned)
    {
        const string settings = "RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings";

        string input = owned
            ? "System.Text.Json.JsonSerializer.Deserialize(new byte[0], RetroDownfall.Arcanum.Core.Serialization.ConfigurationJsonContext.Default.ArcanumSettings)!"
            : "CampaignProductionSettings.Raw()";

        string source = R2Source(R2Admission
            + settings + " configuration = " + input + "; "
            + "_ = RetroDownfall.Arcanum.Core.Configuration.ProviderResolver.TryResolveProviderForModel(configuration, null, out var selected, out string resolvedModel); "
            + "_ = RetroDownfall.Arcanum.Core.Configuration.ProviderResolver.TryResolveModelEntry(selected!, resolvedModel, out var entry); "
            + "foreach (string advertised in RetroDownfall.Arcanum.Core.Configuration.ProviderResolver.EnumerateAdvertisedModels(selected!)) { _ = advertised; } "
            + "System.IO.File.Exists(\"production-model-control\");",
            "internal static class CampaignProductionSettings { internal static extern " + settings + " Raw(); }");

        HostedProducerProductionOverlay overlay = HostedGrimoireProducerInventory.GetProductionOverlay(
            source, HostedProducerProductionOverlayKind.ModelCore);

        var compilation = overlay.Fixture;

        var core = overlay.Production.Single(static candidate => candidate.AssemblyName == "RetroDownfall.Arcanum.Core");

        Assert.Same(overlay, HostedGrimoireProducerInventory.GetProductionOverlay(
            source, HostedProducerProductionOverlayKind.ModelCore));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Contains(compilation.References.OfType<CompilationReference>(), reference =>
            ReferenceEquals(reference.Compilation, core));

        HostedProducerDiscovery<HostedProducerSite> result = overlay.Discovery;

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists" && site.SourcePath == "src/Fixture.cs");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.IReadOnlyList`1.this[]");
    }

    [Fact]
    public void CampaignModelProductionFactoryNeedsNoModelIndexerProof()
    {
        string source = R2Source(R2Admission
            + "_ = await new RetroDownfall.Arcanum.Api.Intelligence.CampaignRollupProviderClientFactory(null!, null!).ResolveClientAsync(null, token); "
            + "System.IO.File.Exists(\"production-factory-model-control\");");

        HostedProducerProductionOverlay overlay = HostedGrimoireProducerInventory.GetProductionOverlay(
            source, HostedProducerProductionOverlayKind.ModelFactory);

        var compilation = overlay.Fixture;

        var production = overlay.Production;

        Assert.Same(overlay, HostedGrimoireProducerInventory.GetProductionOverlay(
            source, HostedProducerProductionOverlayKind.ModelFactory));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Equal(3, production.Length);

        Assert.Contains(compilation.References.OfType<CompilationReference>(), reference =>
            ReferenceEquals(reference.Compilation, production.Single(static candidate => candidate.AssemblyName == "RetroDownfall.Arcanum.Api")));

        HostedProducerDiscovery<HostedProducerSite> result = overlay.Discovery;

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists" && site.SourcePath == "src/Fixture.cs");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.IReadOnlyList`1.this[]");
    }
}
