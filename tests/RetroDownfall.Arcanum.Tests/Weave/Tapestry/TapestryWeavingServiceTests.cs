using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed class TapestryWeavingServiceTests
{
    [Fact]
    public async Task Closing_wakes_unsent_provider_wait_and_drains_the_sweep()
    {
        await using TapestryAdmissionHarness harness = new();

        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.OnStep = async (step, _) =>
        {
            if (step != "embed-leaves") return;

            CancellationToken token = ProviderWorkWaitAmbient.CancellationToken;

            waiting.TrySetResult();

            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new ProviderWorkDeferredException(token); }
        };

        Task<TapestrySweepOutcome> sweep = harness.Service.RunSweepAsync(TapestryAdmissionHarness.Settings(), CancellationToken.None);

        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using IGrimoireClosingOwner closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

        Assert.Equal(TapestrySweepStatus.DeferredForMaintenance, (await sweep.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.True((await harness.Inner.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);
        Assert.Contains("abandon", harness.Events);
    }

    [Fact]
    public async Task Unsent_provider_deferral_abandons_generation_and_does_not_back_off_retry()
    {
        await using TapestryAdmissionHarness harness = new();

        harness.OnStep = (step, _) => step == "embed-leaves"
            ? Task.FromException(new ProviderWorkDeferredException(CancellationToken.None))
            : Task.CompletedTask;

        TapestrySweepOutcome deferred = await harness.Service.RunSweepAsync(TapestryAdmissionHarness.Settings(), CancellationToken.None);

        Assert.Equal(TapestrySweepStatus.DeferredForMaintenance, deferred.Status);
        Assert.Contains("abandon", harness.Events);
        Assert.Empty(harness.Persistence.Generations);

        harness.OnStep = static (_, _) => Task.CompletedTask;

        TapestrySweepOutcome retry = await harness.Service.RunSweepAsync(TapestryAdmissionHarness.Settings(), CancellationToken.None);

        Assert.Equal(TapestrySweepStatus.Completed, retry.Status);
        Assert.Contains("published", harness.Events);
    }

    /// <summary>
    /// A sweep deferred by maintenance is owed, so it runs when the gate reopens. Returning to the rebuild
    /// cadence instead leaves every tree stale for the whole interval (an hour by default) after a window
    /// that lasted seconds.
    /// </summary>
    [Fact]
    public async Task Deferred_sweep_retries_after_admission_reopens_not_after_the_full_interval()
    {
        await using TapestryAdmissionHarness harness = new();

        harness.Configuration.Features.Tapestry = true;

        harness.Configuration.Integrations.Embeddings.Dimensions = 64;

        IGrimoireClosingOwner closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

        Assert.True((await harness.Inner.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        IGrimoireExclusiveClosedLease closed = (await harness.Inner.CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        await harness.Service.StartAsync(CancellationToken.None);

        try
        {
            await TapestryAdmissionHarness.WaitUntilAsync(() => harness.Gate.RequestedWorkKinds.Count == 1);

            Assert.Equal(0, harness.Scopes.Created);

            Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

            await closed.DisposeAsync();

            // No clock is fired: the retry is driven by the reopen alone.
            await TapestryAdmissionHarness.WaitUntilAsync(() => harness.Events.Contains("published"));

            Assert.Equal(1, harness.Scopes.Created);

            Assert.Equal(1, harness.Persistence.Begins);
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static CovenantExclusiveRecoveryOwner Owner() =>
        new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(new byte[32]));
}
