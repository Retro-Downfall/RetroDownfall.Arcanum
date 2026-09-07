using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{

    [Fact]
    public async Task Recording_observer_snapshots_remain_stable_during_later_admission()
    {

        RecordingGrimoireWorkAdmissionGate observer = new(CreateGate());

        Assert.True(observer.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out IGrimoireWorkLease? first));

        await using IGrimoireWorkLease firstLease = first!;

        IReadOnlyList<GrimoireWorkKind> kinds = observer.RequestedWorkKinds;

        IReadOnlyList<IGrimoireWorkLease> leases = observer.InnerWorkLeases;

        Assert.True(observer.TryAcquireWorkLease(GrimoireWorkKind.SagaExtraction, out IGrimoireWorkLease? second));

        await using IGrimoireWorkLease secondLease = second!;

        Assert.Equal([GrimoireWorkKind.WorkspaceIndexing], kinds);

        Assert.Single(leases);

        Assert.Equal([GrimoireWorkKind.WorkspaceIndexing, GrimoireWorkKind.SagaExtraction], observer.RequestedWorkKinds);

        Assert.Equal(2, observer.InnerWorkLeases.Count);

    }

    [Fact]
    public async Task Recording_observer_counts_every_concurrent_work_and_effect_attempt()
    {

        RecordingGrimoireWorkAdmissionGate observer = new(CreateGate());

        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task[] workers = Enumerable.Range(0, 8).Select(async _ =>
        {

            await start.Task;

            for (int attempt = 0; attempt < 256; attempt++)
            {

                Assert.True(observer.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out IGrimoireWorkLease? work));

                await using IGrimoireWorkLease lease = work!;

                Assert.True(lease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect));

                await using IGrimoireExternalEffectGroup group = effect!;

            }

        }).ToArray();

        start.SetResult();

        await Task.WhenAll(workers).WaitAsync(BoundedWait);

        Assert.Equal(2_048, observer.RequestedWorkKinds.Count);

        Assert.Equal(2_048, observer.InnerWorkLeases.Count);

        Assert.Equal(2_048, observer.EffectGroupAttempts);

    }

}
