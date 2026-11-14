using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Operations;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// The in-memory store stands in for the SQL one in every reconciler and ledger test, so what it answers
/// to a query has to be what the SQL store answers: the filters narrow, the paging pages.
/// </summary>
public sealed class FakeLongRunningOperationStoreTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(501, 0)]
    [InlineData(100, -1)]
    [InlineData(100, int.MaxValue)]
    public async Task ListAsync_mirrors_native_refusal_of_invalid_page_bounds(int limit, int offset)
    {
        FakeLongRunningOperationStore store = new(TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ListAsync(new LongRunningOperationQuery(Limit: limit, Offset: offset)));
    }

    [Theory]
    [InlineData(100, false, 100)]
    [InlineData(100, true, 101)]
    [InlineData(500, false, 500)]
    [InlineData(500, true, 501)]
    public async Task ListAsync_mirrors_explicit_native_lookahead_without_changing_ordinary_budgets(int limit, bool lookahead, int count)
    {
        FakeLongRunningOperationStore store = new(TimeProvider.System);

        for (int index = 0; index < 520; index++)
        {
            store.Seed(LongRunningOperationKinds.A2AOutboundSending, LongRunningOperationRecoveryPolicy.ReconcileAndComplete);
        }

        Assert.Equal(count, (await store.ListAsync(new LongRunningOperationQuery(Limit: limit, IncludeLookahead: lookahead))).Count);
    }

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

    [Theory]
    [InlineData(LongRunningOperationState.Running, LongRunningOperationState.Completed, LongRunningOperationState.Completed)]
    [InlineData(LongRunningOperationState.Waiting, LongRunningOperationState.ReconciliationRequired, LongRunningOperationState.ReconciliationRequired)]
    [InlineData(LongRunningOperationState.Running, LongRunningOperationState.Abandoned, LongRunningOperationState.Abandoned)]
    [InlineData(LongRunningOperationState.Cancelling, LongRunningOperationState.ReconciliationRequired, LongRunningOperationState.Abandoned)]
    [InlineData(LongRunningOperationState.Cancelling, LongRunningOperationState.Completed, LongRunningOperationState.Completed)]
    public async Task Refresh_owned_settlement_mirrors_live_writer_and_releases_its_lease_once(
        LongRunningOperationState source, LongRunningOperationState proposal, LongRunningOperationState expected)
    {
        DateTimeOffset now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

        FakeLongRunningOperationStore store = new(new SettlementClock(now.AddDays(-1)));

        LongRunningOperation row = store.Seed(LongRunningOperationKinds.GuildSpendRefresh,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete, source) with
        {
            LeaseOwner = new string('o', 200),
            LeaseExpiresAt = now.AddMinutes(1),
        };

        store.Add(row);

        IGuildRefreshOwnedSettlement settlement = Assert.IsAssignableFrom<IGuildRefreshOwnedSettlement>(store);

        string error = "  " + new string('e', 220) + "  ";

        Assert.Equal(expected, await settlement.TrySettleGuildRefreshAsync(row.Id, row.Revision,
            "  " + new string('o', 220) + "  ", proposal, error, new SettlementClock(now), CancellationToken.None));

        LongRunningOperation changed = Assert.Single(store.Operations);

        Assert.Equal(row with
        {
            State = expected,
            CompletedAt = source == LongRunningOperationState.Cancelling
                || proposal is LongRunningOperationState.Completed or LongRunningOperationState.Abandoned ? now : null,
            LeaseOwner = null,
            LeaseExpiresAt = null,
            TerminalErrorCode = source == LongRunningOperationState.Cancelling && proposal != LongRunningOperationState.Completed
                ? ErrorCodes.Guild.RefreshCancelled : new string('e', 200),
            Revision = row.Revision + 1,
        }, changed);

        Assert.Null(await settlement.TrySettleGuildRefreshAsync(row.Id, changed.Revision,
            new string('o', 200), proposal, null, new SettlementClock(now), CancellationToken.None));

        Assert.Equal(changed, Assert.Single(store.Operations));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("revision")]
    [InlineData("maximum-revision")]
    [InlineData("expired")]
    [InlineData("expiry-equals-now")]
    [InlineData("missing-expiry")]
    [InlineData("kind")]
    [InlineData("state")]
    [InlineData("missing-id")]
    public async Task Refresh_owned_settlement_refuses_unowned_or_stale_rows_without_mutation(string refusal)
    {
        DateTimeOffset now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

        FakeLongRunningOperationStore store = new(new SettlementClock(now));

        LongRunningOperation row = store.Seed(LongRunningOperationKinds.GuildSpendRefresh,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete) with { LeaseOwner = "owner", LeaseExpiresAt = now.AddMinutes(1) };

        row = refusal switch
        {
            "maximum-revision" => row with { Revision = long.MaxValue },
            "expired" => row with { LeaseExpiresAt = now.AddTicks(-1) },
            "expiry-equals-now" => row with { LeaseExpiresAt = now },
            "missing-expiry" => row with { LeaseExpiresAt = null },
            "kind" => row with { Kind = LongRunningOperationKinds.A2AOutboundSending },
            "state" => row with { State = LongRunningOperationState.Completed },
            _ => row,
        };

        store.Add(row);

        IGuildRefreshOwnedSettlement settlement = Assert.IsAssignableFrom<IGuildRefreshOwnedSettlement>(store);

        Assert.Null(await settlement.TrySettleGuildRefreshAsync(refusal == "missing-id" ? Guid.NewGuid() : row.Id,
            refusal == "revision" ? row.Revision + 1 : row.Revision, refusal == "owner" ? "other" : "owner",
            LongRunningOperationState.Completed, null, new SettlementClock(now), CancellationToken.None));

        Assert.Equal(row, Assert.Single(store.Operations));
    }

    [Fact]
    public async Task Refresh_owned_settlement_refuses_cancellation_and_invalid_proposals_before_mutation()
    {
        FakeLongRunningOperationStore store = new(TimeProvider.System);

        LongRunningOperation row = store.Seed(LongRunningOperationKinds.GuildSpendRefresh,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete, leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1));

        IGuildRefreshOwnedSettlement settlement = Assert.IsAssignableFrom<IGuildRefreshOwnedSettlement>(store);

        using CancellationTokenSource cancelled = new();

        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settlement.TrySettleGuildRefreshAsync(row.Id,
            row.Revision, "dead-process", LongRunningOperationState.Completed, null, TimeProvider.System, cancelled.Token));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => settlement.TrySettleGuildRefreshAsync(row.Id,
            row.Revision, "dead-process", LongRunningOperationState.Running, null, TimeProvider.System, CancellationToken.None));

        Assert.Equal(row, Assert.Single(store.Operations));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refresh_owned_settlement_rejects_missing_owner_without_mutation(string? owner)
    {
        FakeLongRunningOperationStore store = new(TimeProvider.System);

        LongRunningOperation row = store.Seed(LongRunningOperationKinds.GuildSpendRefresh,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete);

        IGuildRefreshOwnedSettlement settlement = Assert.IsAssignableFrom<IGuildRefreshOwnedSettlement>(store);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => settlement.TrySettleGuildRefreshAsync(row.Id,
            row.Revision, owner!, LongRunningOperationState.Completed, null, TimeProvider.System, CancellationToken.None));

        Assert.Equal(row, Assert.Single(store.Operations));
    }

    private sealed class SettlementClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Guid[] Ids(IReadOnlyList<LongRunningOperation> operations) =>
        [.. operations.Select(static operation => operation.Id)];
}
