using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string QuiescentFixtureNamespace = "RetroDownfall.Arcanum.Infrastructure.Data.Schema";

    [Fact]
    public void Closed_quiescent_dispatch_retains_both_known_effects_and_excludes_unconfigured_bodies()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverQuiescentStrategies("complete");

        Assert.Empty(result.Diagnostics);

        Assert.Contains(result.Items, static site => site.EnclosingType.EndsWith(".InstallationTurnQuotaBackfill", StringComparison.Ordinal) && site.Callee == "System.IO.File.Exists");

        Assert.Contains(result.Items, static site => site.EnclosingType.EndsWith(".LegacyVectorMirrorRetirementBackfill", StringComparison.Ordinal) && site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(result.Items, static site => site.EnclosingType.EndsWith(".UnconfiguredBackfill", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing-census-slot")]
    [InlineData("missing-retirement-slot")]
    [InlineData("wrong-retirement-slot")]
    [InlineData("duplicate-bindings")]
    public void Closed_quiescent_dispatch_refuses_an_incomplete_or_duplicate_exact_binding(string defect)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverQuiescentStrategies(defect);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_STRATEGY_BINDING_UNRESOLVED");
    }

    [Fact]
    public void Legacy_single_quiescent_implementation_keeps_its_original_resolution()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverQuiescentStrategies("legacy");

        Assert.Empty(result.Diagnostics);

        Assert.Contains(result.Items, static site => site.EnclosingType.EndsWith(".InstallationTurnQuotaBackfill", StringComparison.Ordinal) && site.Callee == "System.IO.File.Exists");

        Assert.DoesNotContain(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    private HostedProducerDiscovery<HostedProducerSite> DiscoverQuiescentStrategies(string shape)
    {
        const string slot = "public System.Threading.Tasks.Task AdvanceQuiescentBatchAsync(System.Threading.CancellationToken token)";

        const string abstractSlot = "public abstract System.Threading.Tasks.Task AdvanceQuiescentBatchAsync(System.Threading.CancellationToken token);";

        string census = shape == "missing-census-slot"
            ? "internal abstract class InstallationTurnQuotaBackfill : IGrimoireQuiescentBackfill { public string Name => \"census\"; " + abstractSlot + " }"
            : "internal sealed class InstallationTurnQuotaBackfill : IGrimoireQuiescentBackfill { " + slot + " { System.IO.File.Exists(\"census\"); return System.Threading.Tasks.Task.CompletedTask; } }";

        string retirement = shape switch
        {
            "legacy" => string.Empty,
            "missing-retirement-slot" => "internal abstract class LegacyVectorMirrorRetirementBackfill : IGrimoireQuiescentBackfill { public string Name => \"retirement\"; " + abstractSlot + " }",
            "wrong-retirement-slot" => "internal abstract class LegacyVectorMirrorRetirementBackfill : IGrimoireQuiescentBackfill { " + abstractSlot + " public System.Threading.Tasks.Task AdvanceQuiescentBatchAsync(int token) { System.IO.File.Delete(\"wrong-slot\"); return System.Threading.Tasks.Task.CompletedTask; } }",
            _ => "internal sealed class LegacyVectorMirrorRetirementBackfill : IGrimoireQuiescentBackfill { " + slot + " { System.IO.File.Delete(\"retirement\"); return System.Threading.Tasks.Task.CompletedTask; } }",
        };

        string unconfigured = shape == "legacy" ? string.Empty
            : "internal sealed class UnconfiguredBackfill : IGrimoireQuiescentBackfill { " + slot + " { System.IO.File.WriteAllText(\"unconfigured\", \"effect\"); return System.Threading.Tasks.Task.CompletedTask; } }";

        string declarations = "namespace " + QuiescentFixtureNamespace + " { internal interface IGrimoireQuiescentBackfill { System.Threading.Tasks.Task AdvanceQuiescentBatchAsync(System.Threading.CancellationToken token); } " + census + retirement + unconfigured + " }";

        CSharpCompilation primary = Compile(R2Source(R2Admission + QuiescentFixtureNamespace + ".IGrimoireQuiescentBackfill strategy = null!; await strategy.AdvanceQuiescentBatchAsync(token);", declarations)).WithAssemblyName("Quiescent.Strategies");

        Assert.Empty(primary.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        if (shape != "duplicate-bindings")
        {
            return HostedGrimoireProducerInventory.DiscoverProducerSites([primary], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);
        }

        CSharpCompilation duplicate = Compile(declarations).WithAssemblyName("Quiescent.Strategies");

        Assert.Empty(duplicate.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return HostedGrimoireProducerInventory.DiscoverProducerSites([primary, duplicate], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);
    }
}
