using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// A canonical scratch Grimoire with the accelerator tier installed, plus the synchronization pass
/// that makes FTS eligible.
/// </summary>
internal static class CovenantSearchFixture
{

    internal static Task<CovenantCanonicalFixture> CreateAsync(CancellationToken cancellationToken) =>
        CovenantCanonicalFixture.CreateAsync(
            cancellationToken,
            withAccelerator: true,
            coreObjects: ["owner_deletion_events"]);

    /// <summary>
    /// Runs the outbox worker until the accelerator has caught up, exactly as the background pass
    /// does after a canonical commit.
    /// </summary>
    internal static async Task<CovenantOutboxSyncOutcome> SynchronizeAsync(
        CovenantCanonicalFixture fixture,
        CancellationToken cancellationToken,
        int maxRows = CovenantSearchOutboxWorker.DefaultBatchRows)
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            await LiveAvailabilityAsync(fixture, cancellationToken));

        await using CovenantAcceleratorLease lease = (await gate.AcquireAcceleratorAsync(cancellationToken)).Value;

        Result<CovenantOutboxSyncOutcome> outcome = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantSearchOutboxWorker()
                .SynchronizeAsync(lease, transaction, cancellationToken, maxRows)
                .AsTask(),
            cancellationToken);

        Assert.True(outcome.IsSuccess, outcome.IsFailure ? outcome.Error.Message : null);

        return outcome.Value;

    }

    internal static async Task<Result<CovenantOutboxSyncOutcome>> TrySynchronizeAsync(
        CovenantCanonicalFixture fixture,
        CancellationToken cancellationToken,
        int maxRows = CovenantSearchOutboxWorker.DefaultBatchRows,
        FakeCovenantAvailability? availability = null)
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            availability ?? await LiveAvailabilityAsync(fixture, cancellationToken));

        await using CovenantAcceleratorLease lease = (await gate.AcquireAcceleratorAsync(cancellationToken)).Value;

        return await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantSearchOutboxWorker()
                .SynchronizeAsync(lease, transaction, cancellationToken, maxRows)
                .AsTask(),
            cancellationToken);

    }

    /// <summary>
    /// A published snapshot carrying the dataset generation and accelerator epoch this scratch tier
    /// actually installed, so an accelerator lease is not stale before it is used.
    /// </summary>
    internal static async Task<FakeCovenantAvailability> LiveAvailabilityAsync(
        CovenantCanonicalFixture fixture,
        CancellationToken cancellationToken)
    {

        Guid generation = await fixture.ReadDatasetGenerationAsync(cancellationToken);

        long epoch = await CovenantCapacityFixture.ScalarAsync(
            fixture,
            "SELECT AcceleratorEpoch FROM covenant_state WHERE StateKey = 1;",
            cancellationToken);

        FakeCovenantAvailability availability = new();

        availability.Mutate(current => current with
        {

            DatasetGeneration = generation,

            AcceleratorEpoch = checked((ulong)epoch),

        });

        return availability;

    }

}
