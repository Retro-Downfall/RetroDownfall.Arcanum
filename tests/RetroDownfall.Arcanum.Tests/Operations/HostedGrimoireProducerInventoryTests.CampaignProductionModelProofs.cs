using Microsoft.CodeAnalysis;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CampaignModelProductionSelectorPreservesOnlyOwnedJsonSettings(bool owned)
    {
        const string settings = "RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings";

        string input = owned
            ? "System.Text.Json.JsonSerializer.Deserialize(new byte[0], RetroDownfall.Arcanum.Core.Serialization.ConfigurationJsonContext.Default.ArcanumSettings)!"
            : "CampaignProductionSettings.Raw()";

        string source = R2Source(R2Admission
            + settings + " configuration = " + input + "; "
            + "_ = RetroDownfall.Arcanum.Core.Configuration.ProviderResolver.TryResolveProviderForModel(configuration, null, out var selected, out string resolvedModel); "
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

        bool unresolved = result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.IReadOnlyList`1.this[]");

        if (owned)
        {
            Assert.False(unresolved, string.Join(System.Environment.NewLine, result.Diagnostics));
        }
        else
        {
            Assert.True(unresolved, string.Join(System.Environment.NewLine, result.Diagnostics));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CampaignModelProductionSelectorDistinguishesReboundCloneFromRetainedAlias(bool retainOldAlias)
    {
        const string settings = "RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings";

        const string provider = "RetroDownfall.Arcanum.Core.Configuration.ProviderSettings";

        const string entry = "RetroDownfall.Arcanum.Core.Configuration.ModelEntry";

        const string context = "RetroDownfall.Arcanum.Core.Serialization.ConfigurationJsonContext.Default";

        string retained = retainOldAlias ? provider + " oldAlias = selected!; " : string.Empty;

        string afterClone = retainOldAlias
            ? "oldAlias.Models = CampaignProductionRebind.UnknownModels(); _ = configuration.Providers[0].Models[0].Name; "
            : "CampaignProductionRebind.Consume(selected); ";

        string source = R2Source(R2Admission
            + settings + " configuration = System.Text.Json.JsonSerializer.Deserialize(new byte[0], " + context + ".ArcanumSettings)!; "
            + "_ = RetroDownfall.Arcanum.Core.Configuration.ProviderResolver.TryResolveProviderForModel(configuration, null, out var selected, out string resolvedModel); "
            + retained
            + "selected = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(selected, " + context + ".ProviderSettings), " + context + ".ProviderSettings)!; "
            + afterClone + "System.IO.File.Exists(\"production-rebind-control\");",
            "internal static class CampaignProductionRebind { internal static extern void Consume(" + provider + " selected); "
            + "internal static extern System.Collections.Generic.IReadOnlyList<" + entry + "> UnknownModels(); }");

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

        bool unresolved = result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.IReadOnlyList`1.this[]");

        if (retainOldAlias)
        {
            Assert.True(unresolved, string.Join(System.Environment.NewLine, result.Diagnostics));
        }
        else
        {
            Assert.False(unresolved, string.Join(System.Environment.NewLine, result.Diagnostics));
        }
    }

    [Theory]
    [InlineData("while")]
    [InlineData("for")]
    [InlineData("do")]
    [InlineData("conditional")]
    [InlineData("tuple")]
    public void CampaignModelProductionCloneWaiverRejectsCurrentStatementResets(string shape)
    {
        const string settings = "RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings";

        const string provider = "RetroDownfall.Arcanum.Core.Configuration.ProviderSettings";

        const string context = "RetroDownfall.Arcanum.Core.Serialization.ConfigurationJsonContext.Default";

        string use = shape switch
        {
            "while" => "while (CampaignProductionDominance.ConsumeAndContinue(selected)) { selected = configuration.Providers[0]; } ",
            "for" => "for (int index = 0; CampaignProductionDominance.ConsumeAndContinue(selected); index++) { selected = configuration.Providers[0]; } ",
            "do" => "do { selected = configuration.Providers[0]; } while (CampaignProductionDominance.ConsumeAndContinue(selected)); ",
            "conditional" => "if ((selected = configuration.Providers[0]) != null && CampaignProductionDominance.ConsumeAndContinue(selected)) { } ",
            _ => "_ = ((selected = configuration.Providers[0]), CampaignProductionDominance.ConsumeAndContinue(selected)); ",
        };

        string source = R2Source(R2Admission
            + settings + " configuration = System.Text.Json.JsonSerializer.Deserialize(new byte[0], " + context + ".ArcanumSettings)!; "
            + "_ = RetroDownfall.Arcanum.Core.Configuration.ProviderResolver.TryResolveProviderForModel(configuration, null, out var selected, out string resolvedModel); "
            + "selected = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(selected, " + context + ".ProviderSettings), " + context + ".ProviderSettings)!; "
            + use + "_ = configuration.Providers[0].Models[0].Name; System.IO.File.Exists(\"production-dominance-control\");",
            "internal static class CampaignProductionDominance { internal static extern bool ConsumeAndContinue(" + provider + " selected); }");

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

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.IReadOnlyList`1.this[]");
    }

    [Fact]
    public void CampaignModelProductionFactoryPreservesOwnedModelIndexerProof()
    {
        string source = R2Source(R2Admission
            + "_ = await new RetroDownfall.Arcanum.Api.Intelligence.CampaignRollupProviderClientFactory(null!, null!, null!).ResolveClientAsync(null, token); "
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
