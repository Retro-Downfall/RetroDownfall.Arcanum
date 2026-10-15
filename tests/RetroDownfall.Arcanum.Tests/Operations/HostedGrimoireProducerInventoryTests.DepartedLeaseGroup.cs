using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // The Departed-Lease Group. An external-effect group is an Effect only on the lease it was begun on, and a send holds both the
    // Work of a lease and the Effect of a group: the pair is one lease's. The Effect carries the origin of its group's lease, and a
    // send whose Effect's lease origin is not the origin of the Work live at the send is refused, even when the Effect was begun in
    // another frame on a lease that has since been disposed, stored or handed to a callee, and the Work is a different lease's,
    // acquired in the frame that sends. The Lease Receiver Proof alone met this only inside one member, where a Work the member
    // starts after the group is refused.
    private const string DepartedGroupMismatch = HostedGrimoireProducerInventory.EffectLeaseMismatchCode;

    private const string DepartedGroupSend = "System.IO.File.Delete";

    private const string DepartedGroupAcquisition = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease";

    private const string DepartedGroupGroup = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup";

    // A worker that acquires a lease, begins a group on it, lets the lease depart in the way the row names while the group stays open,
    // and then makes the call the row names. `second` is the declaration of the member the call reaches.
    private static string DepartedGroupSource(string departure, string call, string second) => $$"""
        #nullable enable
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        using RetroDownfall.Arcanum.Infrastructure.Data;

        public class Worker(IGrimoireConnectionAdmissionGate gate) : IHostedService
        {
            private IGrimoireWorkLease? _held;

            public async Task StartAsync(CancellationToken token)
            {
                if (!gate.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out IGrimoireWorkLease? admitted)) return;

                using IGrimoireWorkLease lease = admitted!;

                if (!lease.TryBeginExternalEffectGroup(out IDisposable group)) return;

                using var held = group;

                {{departure}}

                {{call}}
            }

            public Task StopAsync(CancellationToken token) => Task.CompletedTask;

            {{second}}
        }

        public static class Composition
        {
            public static void Configure(IServiceCollection services) { services.AddHostedService<Worker>(); }
        }
        """ + AdmissionTypes;

    // The callee acquires its own lease and sends under it.
    private const string DepartedGroupOwnLeaseSecond = """
        private async Task Second(CancellationToken token)
        {
            if (!gate.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out IGrimoireWorkLease? other)) return;

            using IGrimoireWorkLease second = other!;

            await Task.CompletedTask;

            System.IO.File.Delete("departed-group-send");
        }
        """;

    private HostedProducerDiscovery<HostedProducerSite> DiscoverDepartedGroup(string label, string source)
    {
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"DEPARTED_GROUP_DIAGNOSTIC {label} {diagnostic.Code} {diagnostic.Identity}");
        }

        foreach (HostedProducerSite site in result.Items)
        {
            output.WriteLine($"DEPARTED_GROUP_SITE {label} {site.Callee} work={(site.WorkFrontierCapsuleId is null ? "null" : "set")} effect={(site.EffectFrontierCapsuleId is null ? "null" : "set")}");
        }

        return result;
    }

    // The group stays open, the lease it was begun on departs, and the callee sends under its own Work.
    [Theory]
    [InlineData("disposed", "await lease.DisposeAsync();")]
    [InlineData("stored", "_held = lease;")]
    [InlineData("opaque-callee", "System.GC.KeepAlive(lease);")]
    public void AGroupBegunOnALeaseThatDepartedIsNotTheEffectOfAnotherFramesWork(string departure, string statement)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverDepartedGroup(departure, DepartedGroupSource(statement, "await Second(token);", DepartedGroupOwnLeaseSecond));

        HostedProducerSite[] acquisitions = [.. result.Items.Where(static site => site.Callee == DepartedGroupAcquisition)];

        Assert.Equal(2, acquisitions.Length);

        HostedProducerSite send = Assert.Single(result.Items, static site => site.Callee == DepartedGroupSend);

        // The send keeps the Work of the lease acquired in its own frame and loses the Effect of the group begun on the first.
        Assert.Contains(send.WorkFrontierCapsuleId, acquisitions.Select(static site => site.CapsuleId));

        Assert.Null(send.EffectFrontierCapsuleId);

        HostedProducerInventoryDiagnostic mismatch = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == DepartedGroupMismatch);

        Assert.Contains("/site@src/Fixture.cs:", mismatch.Identity, StringComparison.Ordinal);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == DepartedGroupSend);
    }

    // The twins: the pair is one lease's, whichever way the lease and its group reach the send.
    [Theory]
    [InlineData("callee-formal", "await Second(lease, token);", "private async Task Second(IGrimoireWorkLease inner, CancellationToken token) { await Task.CompletedTask; System.IO.File.Delete(\"departed-group-send\"); }")]
    [InlineData("group-begun-in-callee", "await Second(lease, token);", "private async Task Second(IGrimoireWorkLease inner, CancellationToken token) { if (!inner.TryBeginExternalEffectGroup(out IDisposable inside)) return; using var insideHeld = inside; await Task.CompletedTask; System.IO.File.Delete(\"departed-group-send\"); }")]
    [InlineData("send-in-the-frame-that-began-the-group", "await Task.CompletedTask; System.IO.File.Delete(\"departed-group-send\");", "")]
    public void AGroupBegunOnTheLeaseWhoseWorkIsLiveAtTheSendIsStillItsEffect(string label, string call, string second)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverDepartedGroup(label, DepartedGroupSource("", call, second));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == DepartedGroupMismatch);

        HostedProducerSite acquisition = Assert.Single(result.Items, static site => site.Callee == DepartedGroupAcquisition);

        HostedProducerSite send = Assert.Single(result.Items, static site => site.Callee == DepartedGroupSend);

        Assert.Equal(acquisition.CapsuleId, send.WorkFrontierCapsuleId);

        Assert.NotNull(send.EffectFrontierCapsuleId);

        Assert.Contains(send.EffectFrontierCapsuleId, result.Items.Where(static site => site.Callee == DepartedGroupGroup).Select(static site => site.CapsuleId));
    }
}
