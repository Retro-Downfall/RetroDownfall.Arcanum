using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Fact]
    public async Task Paid_recovery_registered_handler_owns_one_group_for_summary_and_yields_without_terminal_failure()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z';");

        using CancellationTokenSource stopping = new();

        LongRunningOperationStartupHostedService service = new(new PaidRecoveryScopes(f), TimeProvider.System,
            new LongRunningOperationReconciliationStatus(), f.RecordedAdmission,
            NullLogger<LongRunningOperationStartupHostedService>.Instance);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IGrimoireClosingOwner? closing = null;

        int groupsAtSend = 0;

        f.Handler.InsideSendWithCancellation = async token =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            if (f.Handler.SendCount != 1) return;

            groupsAtSend = f.RecordedAdmission.EffectGroupAttempts;

            entered.TrySetResult();

            await release.Task.WaitAsync(token);
        };

        Task<Exception?> run = Record.ExceptionAsync(() => service.RunBackgroundPassAsync(
            DateTimeOffset.UtcNow, "fixture-runtime-recovery", stopping.Token));

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            closing = f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()).Value;

            Task<RetroDownfall.Arcanum.Core.Primitives.Result> drain = f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            Assert.False(drain.IsCompleted);

            release.TrySetResult();

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        }
        finally
        {
            release.TrySetResult();

            await stopping.CancelAsync();

            await run.WaitAsync(TimeSpan.FromSeconds(10));

            await service.StopAsync(CancellationToken.None);

            if (closing is not null) await closing.DisposeAsync();
        }

        Assert.Equal(1, groupsAtSend);

        Assert.Equal(1, f.RecordedAdmission.EffectGroupAttempts);

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_embeddings;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT sum(PaidCount) FROM long_rest_provider_effects;"));

        Assert.Equal(LongRunningOperationState.Running, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Null((await f.Operations.GetAsync(f.Operation))!.TerminalErrorCode);
    }

    // Only scope routing is adapted to the existing native fixture. Discovery, classified lease
    // acquisition, reconciler, registered handler, coordinator, gate and all paid work are real.
    private sealed class PaidRecoveryScopes(MaintenanceFixture fixture) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new PaidRecoveryScope(fixture,
            fixture.Services.GetRequiredService<IServiceScopeFactory>().CreateScope());
    }

    private sealed class PaidRecoveryScope(MaintenanceFixture fixture, IServiceScope inner) : IServiceScope, IServiceProvider, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ILongRunningOperationGenericRecoveryDiscovery)) return fixture.Operations;

            if (serviceType == typeof(LongRunningOperationReconciler))
            {
                return new LongRunningOperationReconciler(fixture.Operations, [new LongRestSweepRecoveryHandler(fixture.Coordinator)],
                    TimeProvider.System, NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());
            }

            return inner.ServiceProvider.GetService(serviceType);
        }

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner is IAsyncDisposable asynchronous ? asynchronous.DisposeAsync() : DisposeSynchronously();

        private ValueTask DisposeSynchronously()
        {
            inner.Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
