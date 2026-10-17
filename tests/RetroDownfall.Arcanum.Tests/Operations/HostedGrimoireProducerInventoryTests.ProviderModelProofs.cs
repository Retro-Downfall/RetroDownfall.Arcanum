using Microsoft.CodeAnalysis;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("direct")]
    [InlineData("coalesce")]
    [InlineData("loop")]
    public void ProviderModelsDeclaredAsAnArrayAreIndexedWithoutAnOwnershipProof(string shape)
    {
        const string provider = "RetroDownfall.Arcanum.Core.Configuration.ProviderSettings";

        const string entry = "RetroDownfall.Arcanum.Core.Configuration.ModelEntry";

        string read = shape switch
        {
            "coalesce" => entry + "[] models = selected.Models ?? []; _ = models[0].Name; ",
            "loop" => "for (int i = 0; i < selected.Models.Length; i++) { _ = selected.Models[i].Name; } ",
            _ => "_ = selected.Models[0].Name; ",
        };

        string source = R2Source(R2Admission
            + provider + " selected = ProviderModelInput.Unknown(); "
            + read + "System.IO.File.Exists(\"provider-model-array-control\");",
            "internal static class ProviderModelInput { internal static extern " + provider + " Unknown(); }");

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.IReadOnlyList`1.this[]");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("unknown-list")]
    public void ProviderModelsReadThroughAnUnprovenReadOnlyListStayUnclassified(string shape)
    {
        const string provider = "RetroDownfall.Arcanum.Core.Configuration.ProviderSettings";

        const string entry = "RetroDownfall.Arcanum.Core.Configuration.ModelEntry";

        string read = shape == "alias"
            ? provider + " selected = ProviderModelInput.UnknownProvider(); System.Collections.Generic.IReadOnlyList<" + entry + "> models = selected.Models; _ = models[0].Name; "
            : "_ = ProviderModelInput.UnknownList()[0].Name; ";

        string source = R2Source(R2Admission
            + read + "System.IO.File.Exists(\"provider-model-list-control\");",
            "internal static class ProviderModelInput { internal static extern System.Collections.Generic.IReadOnlyList<" + entry + "> UnknownList(); "
            + "internal static extern " + provider + " UnknownProvider(); }");

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.IReadOnlyList`1.this[]");
    }
}
