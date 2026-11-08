using Microsoft.CodeAnalysis;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("missing-work")]
    [InlineData("write")]
    [InlineData("provider")]
    public void Nested_read_work_does_not_borrow_an_unrelated_outer_effect(string shape)
    {
        string innerAdmission = shape == "missing-work" ? string.Empty : """
            if (!gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var admitted)) return;
            using var own = admitted;
            """;

        string send = shape switch
        {
            "write" => "System.IO.File.Delete(\"nested-send\");",
            "provider" => "System.Net.Http.HttpClient client = null!; await client.GetStringAsync(\"https://example.test\", token);",
            _ => "System.IO.File.Exists(\"nested-send\");",
        };

        string source = R2Source(R2Admission + (shape == "missing-work" ? "lease.Dispose(); " : string.Empty)
            + "await Inner(gate, token);").Replace("public Task StopAsync", $$"""
            private static async Task Inner(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate, CancellationToken token)
            {
                {{innerAdmission}}
                {{send}}
                await Task.CompletedTask;
            }
            public Task StopAsync
            """, StringComparison.Ordinal);

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        string callee = shape switch
        {
            "write" => "System.IO.File.Delete",
            "provider" => "System.Net.Http.HttpClient.GetStringAsync",
            _ => "System.IO.File.Exists",
        };

        HostedProducerSite site = Assert.Single(discovery.Items, site => site.Callee == callee);

        if (shape == "read")
        {
            Assert.NotNull(site.WorkFrontierCapsuleId);

            Assert.Null(site.EffectFrontierCapsuleId);

            Assert.Empty(discovery.Diagnostics);
        }
        else if (shape == "missing-work")
        {
            Assert.Null(site.WorkFrontierCapsuleId);

            Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Detail.StartsWith(callee, StringComparison.Ordinal));
        }
        else
        {
            Assert.NotNull(site.WorkFrontierCapsuleId);

            Assert.Null(site.EffectFrontierCapsuleId);

            Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_ADMISSION_EFFECT_LEASE_MISMATCH"
                && diagnostic.Detail.StartsWith(callee, StringComparison.Ordinal));

            Assert.Contains(discovery.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                && diagnostic.Detail.StartsWith(callee, StringComparison.Ordinal));
        }
    }
}
