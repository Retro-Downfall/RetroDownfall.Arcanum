using Microsoft.CodeAnalysis;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Engine_wait_dispatch_preserves_held_port_body_and_refuses_implementation_drift(bool drift)
    {
        string source = R2Source("RetroDownfall.Arcanum.Infrastructure.Guild.IGuildLocalEngineWait waiter = null!; await waiter.WaitAsync(token);", """
            namespace RetroDownfall.Arcanum.Infrastructure.Guild
            {
                internal interface IGuildLocalEngineWait { Task WaitAsync(CancellationToken token); }
                internal sealed class GuildLocalPort
                {
                    private Task WaitAsync(LocalRun run, CancellationToken token)
                    {
                        System.IO.File.Delete(run.Path);
                        return Task.CompletedTask;
                    }
                    private sealed class LocalRun { internal readonly string Path = "held-run-effect"; }
                    private sealed class EngineWait(GuildLocalPort port, LocalRun run) : IGuildLocalEngineWait
                    {
                        public Task WaitAsync(CancellationToken token) => port.WaitAsync(run, token);
                    }
                }
                /* EXTRA */
            }
            """.Replace("/* EXTRA */", drift ? """
                internal sealed class UnexpectedWait : IGuildLocalEngineWait
                {
                    public Task WaitAsync(CancellationToken token) => Task.CompletedTask;
                }
                """ : string.Empty, StringComparison.Ordinal));

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (drift)
            Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
                && diagnostic.Detail == "RetroDownfall.Arcanum.Infrastructure.Guild.IGuildLocalEngineWait.WaitAsync");
        else
        {
            HostedProducerSite effect = Assert.Single(discovery.Items, static site => site.Callee == "System.IO.File.Delete");

            Assert.Null(effect.WorkFrontierCapsuleId);

            Assert.Null(effect.EffectFrontierCapsuleId);

            Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

            Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

            Assert.DoesNotContain(discovery.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CLOSED_DISPATCH_CHANGED" or "HOSTED_CALL_TARGET_UNRESOLVED");
        }
    }
}
