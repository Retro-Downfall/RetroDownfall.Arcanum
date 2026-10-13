using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    // Lease Spine: one lease is minted at the root and passed by value to every use. These tests follow the lease
    // INSTANCE, not just its kind: the group the paid door opens must be begun on the very lease that was minted.

    [Fact]
    public async Task Spine_hosted_worker_lease_is_the_instance_the_paid_door_opens_its_groups_on()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        int disposals = 0;

        f.RecordedAdmission.BeforeWorkLeaseDisposalAsync = () =>
        {
            Interlocked.Increment(ref disposals);

            return ValueTask.CompletedTask;
        };

        using LongRestSweepHostedService host = PaidOwnershipHost(f);

        await host.RunOneAsync(f.Operation, CancellationToken.None);

        IGrimoireWorkLease minted = Assert.Single(f.RecordedAdmission.IssuedWorkLeases);

        Assert.Equal(GrimoireWorkKind.LongRestSweep, minted.Kind);

        Assert.True(f.Handler.SendCount >= 1);

        Assert.NotEmpty(f.RecordedAdmission.EffectGroupLeases);

        // Every group the sweep opened was begun on the worker's one lease, and nothing else was minted for it.
        Assert.All(f.RecordedAdmission.EffectGroupLeases, begun => Assert.Same(minted, begun));

        Assert.Equal(f.Handler.SendCount, f.RecordedAdmission.EffectGroupAttempts);

        Assert.Equal([GrimoireWorkKind.LongRestSweep], f.RecordedAdmission.RequestedWorkKinds);

        // The worker disposes its lease once, after the whole sweep.
        Assert.Equal(1, disposals);
    }

    [Fact]
    public async Task Spine_session_shell_lease_is_the_instance_the_paid_door_opens_its_groups_on()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        int disposals = 0;

        f.RecordedAdmission.BeforeWorkLeaseDisposalAsync = () =>
        {
            Interlocked.Increment(ref disposals);

            return ValueTask.CompletedTask;
        };

        LongRestSweepProgress completed = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, completed.Phase);

        IGrimoireWorkLease minted = Assert.Single(f.RecordedAdmission.IssuedWorkLeases);

        Assert.Equal(GrimoireWorkKind.LongRestSweep, minted.Kind);

        Assert.NotEmpty(f.RecordedAdmission.EffectGroupLeases);

        Assert.All(f.RecordedAdmission.EffectGroupLeases, begun => Assert.Same(minted, begun));

        Assert.Equal(1, disposals);
    }

    [Fact]
    public async Task Spine_maintenance_opens_its_group_on_exactly_the_lease_it_was_handed_and_leaves_it_to_its_owner()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        Assert.True(f.RecordedAdmission.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? handed));

        await using IGrimoireWorkLease lease = handed!;

        int disposals = 0;

        f.RecordedAdmission.BeforeWorkLeaseDisposalAsync = () =>
        {
            Interlocked.Increment(ref disposals);

            return ValueTask.CompletedTask;
        };

        // Capture, then the first paid fold: the handed lease is the only one this gate ever issued.
        LongRestSweepHarness.Require(await f.Maintenance.ProcessAdmittedAsync(f.Operation, "fixture-worker", null, lease, CancellationToken.None));

        LongRestSweepHarness.Require(await f.Maintenance.ProcessAdmittedAsync(f.Operation, "fixture-worker", null, lease, CancellationToken.None));

        Assert.Same(lease, Assert.Single(f.RecordedAdmission.IssuedWorkLeases));

        Assert.NotEmpty(f.RecordedAdmission.EffectGroupLeases);

        Assert.All(f.RecordedAdmission.EffectGroupLeases, begun => Assert.Same(lease, begun));

        // The maintenance borrowed the lease: it neither disposed it nor minted another.
        Assert.Equal(0, disposals);

        Assert.Equal([GrimoireWorkKind.LongRestSweep], f.RecordedAdmission.RequestedWorkKinds);
    }
}
