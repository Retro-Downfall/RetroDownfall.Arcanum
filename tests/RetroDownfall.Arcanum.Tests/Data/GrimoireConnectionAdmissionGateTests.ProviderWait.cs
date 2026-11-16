using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{
    [Fact]
    public async Task Closing_signals_unsent_provider_wait_without_revoking_active_effect_group()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.TapestryWeaving, out IGrimoireWorkLease? work));
        Assert.True(work!.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group));
        CancellationToken waitToken = work.ProviderAdmissionRevocation;
        Assert.True(waitToken.CanBeCanceled);

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(16));

        Assert.True(waitToken.IsCancellationRequested);
        Assert.False(work.MaintenanceRevocation.IsCancellationRequested);
        Task drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();
        Assert.False(drain.IsCompleted);
        await group!.DisposeAsync();
        Assert.True(work.MaintenanceRevocation.IsCancellationRequested);
        await work.DisposeAsync();
        await drain.WaitAsync(BoundedWait);
    }

}
