using Microsoft.CodeAnalysis;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("ValidateAsync", false)]
    [InlineData("RetainRecoveryAsync", false)]
    [InlineData("ValidateAsync", true)]
    [InlineData("RetainRecoveryAsync", true)]
    public void Workspace_lease_dispatch_requires_complete_census_and_preserves_effect_frontiers(string operation, bool drift)
    {
        string source = FixtureSource("RetroDownfall.Arcanum.Infrastructure.Workspaces.IWorkspaceMutationLease lease = null!; lease."
            + operation + "();", """
            namespace RetroDownfall.Arcanum.Infrastructure.Workspaces
            {
                internal interface IWorkspaceMutationLease { void ValidateAsync(); void RetainRecoveryAsync(); }
                internal static class WorkspaceMutationFence
                {
                    private sealed class CompositeLease : IWorkspaceMutationLease
                    {
                        public void ValidateAsync() { System.IO.File.Delete("composite-validation"); }
                        public void RetainRecoveryAsync() { System.IO.File.Delete("composite-recovery"); }
                    }
                }
                /* EXTRA */
            }
            namespace RetroDownfall.Arcanum.Infrastructure.Guild
            {
                internal sealed class GuildWorkspaceMutationFence
                {
                    private sealed class Lease : RetroDownfall.Arcanum.Infrastructure.Workspaces.IWorkspaceMutationLease
                    {
                        public void ValidateAsync() { System.IO.File.Delete("guild-validation"); }
                        public void RetainRecoveryAsync() { System.IO.File.Delete("guild-recovery"); }
                    }
                }
            }
            """.Replace("/* EXTRA */", drift ? """
                internal sealed class UnexpectedLease : IWorkspaceMutationLease
                {
                    public void ValidateAsync() { System.IO.File.Delete("unexpected-validation"); }
                    public void RetainRecoveryAsync() { System.IO.File.Delete("unexpected-recovery"); }
                }
                """ : string.Empty, StringComparison.Ordinal));

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (drift)
        {
            Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CLOSED_DISPATCH_CHANGED");
        }
        else
        {
            HostedProducerSite[] effects = discovery.Items.Where(static site => site.Callee == "System.IO.File.Delete").ToArray();

            Assert.Equal(2, effects.Length);

            Assert.All(effects, static effect =>
            {
                Assert.Null(effect.WorkFrontierCapsuleId);

                Assert.Null(effect.EffectFrontierCapsuleId);
            });

            Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

            Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

            Assert.DoesNotContain(discovery.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CALL_TARGET_UNRESOLVED" or "HOSTED_CLOSED_DISPATCH_CHANGED");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reference_comparer_singleton_requires_exact_framework_getter_identity(bool authoredLookalike)
    {
        string helper = authoredLookalike ? """
            namespace System.Collections.Generic
            {
                public sealed class ReferenceEqualityComparer
                {
                    public static ReferenceEqualityComparer Instance
                    {
                        get { System.IO.File.Delete("authored-getter"); return new ReferenceEqualityComparer(); }
                    }
                }
            }
            """ : string.Empty;

        var compilation = CompileWithProductionReferencePack(
            FixtureSource("object comparer = System.Collections.Generic.ReferenceEqualityComparer.Instance;", helper),
            "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (authoredLookalike)
        {
            Assert.Contains(discovery.Items, static site => site.Callee == "System.IO.File.Delete"
                && site.WorkFrontierCapsuleId is null && site.EffectFrontierCapsuleId is null);
        }
        else Assert.Empty(discovery.Diagnostics);
    }
}
