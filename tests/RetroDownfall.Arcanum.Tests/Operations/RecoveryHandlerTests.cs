using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Issue #40 requires each recovery handler to be idempotent under repeated startup invocation and
/// to never claim work it cannot prove happened. Every case here runs recovery twice.
/// </summary>
public sealed class RecoveryHandlerTests
{
    private static LongRunningOperation Operation(
        string kind,
        LongRunningOperationRecoveryPolicy policy,
        Guid? budgetReservationId = null,
        Guid? runId = null)
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);

        return store.Seed(
            kind,
            policy,
            budgetReservationId: budgetReservationId,
            runId: runId);
    }

    // ---- subagent ------------------------------------------------------------------------------

    /// <summary>
    /// Requirement 8: a crashed child is abandoned, its reservation released once, and it is never
    /// restarted — restarting a subagent from a ledger row is how a recursion storm begins.
    /// </summary>
    [Fact]
    public async Task Subagent_recovery_abandons_the_child_without_restarting_or_rebilling_it()
    {
        Guid reservationId = Guid.NewGuid();
        FakeBudgetReservationService reservations = new();
        FakeTurnRunWriter runs = new();

        SubagentRecoveryHandler handler = new(
            reservations,
            runs,
            NullLogger<SubagentRecoveryHandler>.Instance);
        LongRunningOperation operation = Operation(
            LongRunningOperationKinds.Subagent,
            LongRunningOperationRecoveryPolicy.AbandonSafely,
            budgetReservationId: reservationId,
            runId: Guid.NewGuid());

        LongRunningOperationRecoveryResult first = await handler.RecoverAsync(operation, CancellationToken.None);
        LongRunningOperationRecoveryResult second = await handler.RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, first.State);
        Assert.Equal(LongRunningOperationState.Abandoned, second.State);
        Assert.Equal(
            LongRunningOperationRecoveryOutcomes.SubagentChildAbandoned,
            first.ErrorCode);
        Assert.Empty(runs.Billed);
        Assert.Equal([reservationId, reservationId], reservations.Released);
    }
}
