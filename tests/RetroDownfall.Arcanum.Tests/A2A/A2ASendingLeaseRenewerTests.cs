using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.A2A;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed class A2ASendingLeaseRenewerTests
{
    [Fact]
    public async Task EmptyRenewalSkipsAdmissionAndScope()
    {
        FakeTimeProvider clock = new();

        GrimoireConnectionAdmissionGate inner = new(clock);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireConnectionAdmissionGate>(admission);

        await using ServiceProvider provider = services.BuildServiceProvider();

        CountingServiceScopeFactory scopes = new(
            provider.GetRequiredService<IServiceScopeFactory>());

        A2ASendingLeaseRenewer renewer = ActivatorUtilities.CreateInstance<A2ASendingLeaseRenewer>(
            provider,
            scopes,
            clock,
            NullLogger<A2ASendingLeaseRenewer>.Instance);

        Assert.Equal(0, await renewer.RenewHeldAsync(CancellationToken.None));

        Assert.Empty(admission.RequestedWorkKinds);

        Assert.Equal(0, scopes.Created);
    }

    [Fact]
    public async Task RenewalDenialCreatesNoScopeAndKeepsHeldSendings()
    {
        FakeTimeProvider clock = new();

        FakeLongRunningOperationStore store = new(clock);

        GrimoireConnectionAdmissionGate inner = new(clock);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        ServiceCollection services = new();

        services.AddSingleton<ILongRunningOperationStore>(store);

        services.AddSingleton<IGrimoireConnectionAdmissionGate>(admission);

        await using ServiceProvider provider = services.BuildServiceProvider();

        CountingServiceScopeFactory scopes = new(
            provider.GetRequiredService<IServiceScopeFactory>());

        A2ASendingLeaseRenewer renewer = ActivatorUtilities.CreateInstance<A2ASendingLeaseRenewer>(
            provider,
            scopes,
            clock,
            NullLogger<A2ASendingLeaseRenewer>.Instance);

        renewer.Track(new A2ASendingLedgerEntry(Guid.NewGuid(), "held-owner"));

        IGrimoireExclusiveClosedLease closed = await PeriodicHostMaintenance.CloseAsync(inner);

        Assert.Equal(0, await renewer.RenewHeldAsync(CancellationToken.None));

        Assert.Equal(0, scopes.Created);

        Assert.Equal([GrimoireWorkKind.A2ASendingLeaseRenewal], admission.RequestedWorkKinds);

        await PeriodicHostMaintenance.ReopenAsync(closed);

        Assert.Equal(1, await renewer.RenewHeldAsync(CancellationToken.None));

        Assert.Equal(1, scopes.Created);
    }

    [Fact]
    public async Task AdmittedRenewalDrainsThroughScopeDisposal()
    {
        FakeTimeProvider clock = new();

        FakeLongRunningOperationStore store = new(clock);

        GrimoireConnectionAdmissionGate inner = new(clock);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        ServiceCollection services = new();

        services.AddSingleton<ILongRunningOperationStore>(store);

        services.AddSingleton<IGrimoireConnectionAdmissionGate>(admission);

        await using ServiceProvider provider = services.BuildServiceProvider();

        BlockingAsyncServiceScopeFactory scopes = new(provider);

        A2ASendingLeaseRenewer renewer = ActivatorUtilities.CreateInstance<A2ASendingLeaseRenewer>(
            provider,
            scopes,
            clock,
            NullLogger<A2ASendingLeaseRenewer>.Instance);

        renewer.Track(new A2ASendingLedgerEntry(Guid.NewGuid(), "held-owner"));

        Task<int> renewal = renewer.RenewHeldAsync(CancellationToken.None);

        await scopes.DisposalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Result<IGrimoireClosingOwner> beginning = inner.BeginOrResumeExclusive(
            PeriodicHostMaintenance.Owner());

        Assert.True(beginning.IsSuccess, beginning.Error.Message);

        Task<Result> drain = inner.DrainRequestAndWorkAsync(
            beginning.Value,
            CancellationToken.None).AsTask();

        bool drainedBeforeScopeDisposal = drain.IsCompleted;

        scopes.AllowDisposal.TrySetResult();

        Assert.Equal(1, await renewal.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.False(drainedBeforeScopeDisposal);

        Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        Result<IGrimoireExclusiveClosedLease> closed = await inner.CloseConnectionAdmissionAsync(
            beginning.Value,
            CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        await PeriodicHostMaintenance.ReopenAsync(closed.Value);
    }
}
