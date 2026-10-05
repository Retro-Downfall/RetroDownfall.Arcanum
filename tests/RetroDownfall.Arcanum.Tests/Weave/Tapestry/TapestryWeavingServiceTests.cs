using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed class TapestryWeavingServiceTests
{
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
