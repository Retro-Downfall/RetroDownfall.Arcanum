using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GrimoireSchemaTransitionHostedServiceTests
{
    [Fact]
    public async Task SchemaPassDenialCreatesNoScope()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireConnectionAdmissionGate>(admission);

        await using ServiceProvider provider = services.BuildServiceProvider();

        CountingServiceScopeFactory scopes = new(
            provider.GetRequiredService<IServiceScopeFactory>());

        GrimoireSchemaTransitionHostedService service =
            ActivatorUtilities.CreateInstance<GrimoireSchemaTransitionHostedService>(
                provider,
                scopes,
                TimeProvider.System,
                NullLogger<GrimoireSchemaTransitionHostedService>.Instance);

        IGrimoireExclusiveClosedLease closed = await PeriodicHostMaintenance.CloseAsync(inner);

        Assert.False(await service.RunOnceAsync(CancellationToken.None));

        Assert.Equal(0, scopes.Created);

        Assert.Equal([GrimoireWorkKind.GrimoireSchemaTransition], admission.RequestedWorkKinds);

        await PeriodicHostMaintenance.ReopenAsync(closed);
    }

    [Fact]
    public async Task AdmittedSchemaPassDrainsThroughScopeDisposal()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireConnectionAdmissionGate>(admission);

        await using ServiceProvider provider = services.BuildServiceProvider();

        BlockingAsyncServiceScopeFactory scopes = new(provider);

        GrimoireSchemaTransitionHostedService service =
            ActivatorUtilities.CreateInstance<GrimoireSchemaTransitionHostedService>(
                provider,
                scopes,
                TimeProvider.System,
                NullLogger<GrimoireSchemaTransitionHostedService>.Instance);

        Task<bool> pass = service.RunOnceAsync(CancellationToken.None);

        await scopes.DisposalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Result<IGrimoireClosingOwner> beginning = inner.BeginOrResumeExclusive(
            PeriodicHostMaintenance.Owner());

        Assert.True(beginning.IsSuccess, beginning.Error.Message);

        Task<Result> drain = inner.DrainRequestAndWorkAsync(
            beginning.Value,
            CancellationToken.None).AsTask();

        bool drainedBeforeScopeDisposal = drain.IsCompleted;

        scopes.AllowDisposal.TrySetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pass);

        Assert.False(drainedBeforeScopeDisposal);

        Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        Result<IGrimoireExclusiveClosedLease> closed = await inner.CloseConnectionAdmissionAsync(
            beginning.Value,
            CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        await PeriodicHostMaintenance.ReopenAsync(closed.Value);
    }
}
