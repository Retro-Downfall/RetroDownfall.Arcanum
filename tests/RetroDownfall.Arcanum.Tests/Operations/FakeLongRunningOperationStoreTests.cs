using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// The in-memory store stands in for the SQL one in every reconciler and ledger test, so what it answers
/// to a query has to be what the SQL store answers: the filters narrow, the paging pages.
/// </summary>
public sealed class FakeLongRunningOperationStoreTests
{
    [Fact]
    public async Task ListAsync_applies_the_kind_state_and_checkpoint_reference_filters()
    {
        FakeLongRunningOperationStore store = new(TimeProvider.System);

        LongRunningOperation outboundRunning = store.Seed(
            LongRunningOperationKinds.A2AOutboundSending,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            LongRunningOperationState.Running);

        LongRunningOperation outboundWaiting = store.Seed(
            LongRunningOperationKinds.A2AOutboundSending,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            LongRunningOperationState.Waiting);

        LongRunningOperation inbound = store.Seed(
            LongRunningOperationKinds.A2AInboundSending,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            LongRunningOperationState.Running);

        store.Add(outboundWaiting with { CheckpointReference = "a2a-callback:wanted" });

        Assert.Equivalent(
            new[] { outboundRunning.Id, outboundWaiting.Id },
            Ids(await store.ListAsync(new LongRunningOperationQuery(LongRunningOperationKinds.A2AOutboundSending))));

        Assert.Equivalent(
            new[] { outboundRunning.Id, inbound.Id },
            Ids(await store.ListAsync(new LongRunningOperationQuery(State: LongRunningOperationState.Running))));

        Assert.Equivalent(
            new[] { outboundWaiting.Id },
            Ids(await store.ListAsync(new LongRunningOperationQuery(
                LongRunningOperationKinds.A2AOutboundSending,
                LongRunningOperationState.Waiting,
                CheckpointReference: "a2a-callback:wanted"))));

        Assert.Empty(await store.ListAsync(new LongRunningOperationQuery(CheckpointReference: "a2a-callback:other")));

        // No filter at all is every row, and an empty reference is no filter, as in the SQL store.
        Assert.Equal(3, (await store.ListAsync(new LongRunningOperationQuery(CheckpointReference: string.Empty))).Count);
    }

    [Fact]
    public async Task ListAsync_pages_by_limit_and_offset_newest_first()
    {
        TimeProvider clock = TimeProvider.System;

        FakeLongRunningOperationStore store = new(clock);

        LongRunningOperation oldest = store.Seed(
            LongRunningOperationKinds.A2AOutboundSending,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete);

        LongRunningOperation middle = store.Seed(
            LongRunningOperationKinds.A2AOutboundSending,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete);

        LongRunningOperation newest = store.Seed(
            LongRunningOperationKinds.A2AOutboundSending,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete);

        store.Add(oldest with { CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        store.Add(middle with { CreatedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero) });

        store.Add(newest with { CreatedAt = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero) });

        Assert.Equal(
            [newest.Id, middle.Id],
            Ids(await store.ListAsync(new LongRunningOperationQuery(Limit: 2))));

        Assert.Equal(
            [oldest.Id],
            Ids(await store.ListAsync(new LongRunningOperationQuery(Limit: 2, Offset: 2))));

        Assert.Empty(await store.ListAsync(new LongRunningOperationQuery(Limit: 2, Offset: 3)));
    }

    /// <summary>
    /// The trap this fake used to set: with 200 or more open Sendings every query answered every row, so a
    /// page never came back short and the ledger's lookup asked for the next page for as long as it was
    /// allowed to. A well-formed id nothing minted has to come back as "not found".
    /// </summary>
    [Fact]
    public async Task A_ledger_lookup_over_more_than_a_page_of_open_sendings_terminates()
    {
        FakeLongRunningOperationStore store = new(TimeProvider.System);

        for (int index = 0; index < 450; index++)
        {
            _ = store.Seed(
                LongRunningOperationKinds.A2AOutboundSending,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                LongRunningOperationState.Running);
        }

        IA2ASendingLedger ledger = new A2ASendingLedger(
            store,
            TimeProvider.System,
            NullLogger<A2ASendingLedger>.Instance);

        // On a worker thread, because every call on this store completes synchronously: a lookup that never
        // sees a short page would otherwise spin on the test's own thread and the time limit could not fire.
        A2ASendingLedgerEntry found = await Task
            .Run(() => ledger.FindOpenOutboundAsync("remote-task-nobody-opened"))
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.False(found.IsRecorded);

        // 450 rows at 200 a page is three round-trips for the one state that holds them, and one for each of
        // the four open states that hold none.
        Assert.Equal(3 + 4, store.ListCallCount);
    }

    private static Guid[] Ids(IReadOnlyList<LongRunningOperation> operations) =>
        [.. operations.Select(static operation => operation.Id)];
}
