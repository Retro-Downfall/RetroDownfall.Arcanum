using Microsoft.CodeAnalysis;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("simple")]
    [InlineData("null-guard")]
    [InlineData("formal-handoff")]
    public void Nested_owned_admission_pairs_the_inner_work_and_effect(string shape)
    {
        var discovered = DiscoverNestedAdmission(shape);

        Assert.Contains(discovered.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(discovered.Diagnostics, static diagnostic => diagnostic.Code is
            "HOSTED_ADMISSION_RECEIVER_MISMATCH" or "HOSTED_ADMISSION_EFFECT_LEASE_MISMATCH"
            or "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Fact]
    public void Conditional_effect_guard_remains_unproved_despite_a_constant_true_argument()
    {
        // The supported admission grammar does not substitute caller booleans into a
        // conditional acquisition. Refusal grants no Effect from the outer lease.
        var discovered = DiscoverNestedAdmission("constant-conditional");

        Assert.Contains(discovered.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_ADMISSION_EFFECT_LEASE_MISMATCH");

        var send = Assert.Single(discovered.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.NotNull(send.WorkFrontierCapsuleId);

        Assert.Null(send.EffectFrontierCapsuleId);
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverNestedAdmission(string shape)
    {
        string workGuard = shape == "null-guard" ? " || admitted is null" : "";

        string effectGuard = shape == "constant-conditional" ? "effect && " : "";

        string rootAdmission = shape == "formal-handoff"
            ? AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal)
            : R2Admission;

        string source = R2Source(rootAdmission + "await Nested(gate, lease, true, token);").Replace("public Task StopAsync", $$"""
            private static async Task Nested(
                RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate,
                RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease outer,
                bool effect, CancellationToken token)
            {
                if (!gate.TryAcquireWorkLease(
                    RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing,
                    out var admitted){{workGuard}}) return;

                await using var own = admitted;

                IDisposable group = null;

                if ({{effectGuard}}!own.TryBeginExternalEffectGroup(out group)) return;

                using var held = group;

                await Task.CompletedTask;

                System.IO.File.Delete("nested-owned-effect");
            }

            public Task StopAsync
            """, StringComparison.Ordinal);

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerOperationEntry[] roots = shape == "formal-handoff"
            ? [OrdinaryRoot(), OrdinaryRoot("Worker.Nested") with { Member = "Nested", LeaseFormal = "outer" }]
            : [OrdinaryRoot()];

        return HostedGrimoireProducerInventory.DiscoverProducerSites([compilation],
            new(["Worker"], []), [new("Worker", roots)], []);
    }
}
