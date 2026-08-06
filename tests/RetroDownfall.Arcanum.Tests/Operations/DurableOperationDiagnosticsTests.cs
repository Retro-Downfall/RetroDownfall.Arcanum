using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Issue #40, requirement 11 and its acceptance criterion: "operators receive actionable diagnostics
/// for states that cannot be repaired automatically."
/// </summary>
public sealed class DurableOperationDiagnosticsTests
{
    private static DurableOperationDiagnostics Create(
        FakeLongRunningOperationStore store,
        TimeProvider time,
        params ILongRunningOperationRecoveryHandler[] handlers) =>
        new(
            store,
            new LongRunningOperationReconciler(
                store,
                handlers,
                time,
                NullLogger<LongRunningOperationReconciler>.Instance));

    /// <summary>Stands in for the real container, where every registry kind owns a handler.</summary>
    private static ILongRunningOperationRecoveryHandler[] HandlersForEveryKind() =>
    [
        .. LongRunningOperationRecoveryRegistry.Descriptors.Values.Select(
            static descriptor => new RecordingRecoveryHandler(
                descriptor.Kind,
                descriptor.MaxCheckpointVersion)),
    ];

    [Fact]
    public async Task A_quiet_ledger_needs_no_attention()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);

        DurableOperationDiagnosticsReport report = await Create(store, time, HandlersForEveryKind())
            .InspectAsync(time.GetUtcNow());

        Assert.Equal(0, report.StaleOperations);
        Assert.Equal(0, report.OperationsAwaitingRepair);
        Assert.Empty(report.KindsWithoutHandler);
        Assert.False(report.NeedsAttention);
    }

    [Fact]
    public async Task Expired_leases_are_reported_as_stale_operations()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        _ = store.Seed(
            LongRunningOperationKinds.WorkspaceIndex,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            leaseExpiresAt: time.GetUtcNow().AddMinutes(-5));

        DurableOperationDiagnosticsReport report = await Create(store, time).InspectAsync(time.GetUtcNow());

        Assert.Equal(1, report.StaleOperations);
        Assert.True(report.NeedsAttention);
    }

    /// <summary>
    /// The state that recovery deliberately cannot resolve on its own must name the kind, the reason,
    /// and what the operator should actually do about it.
    /// </summary>
    [Fact]
    public async Task Failed_reconciliations_carry_the_registry_repair_guidance()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        LongRunningOperation stuck = store.Seed(
            LongRunningOperationKinds.BackupCreate,
            LongRunningOperationRecoveryPolicy.AbandonSafely);

        _ = await store.TryTransitionAsync(
            stuck.Id,
            stuck.Revision,
            ownerId: null,
            LongRunningOperationState.ReconciliationRequired,
            time.GetUtcNow(),
            LongRunningOperationErrorCodes.UnsupportedCheckpointVersion);

        DurableOperationDiagnosticsReport report = await Create(store, time).InspectAsync(time.GetUtcNow());

        DurableOperationRepairItem item = Assert.Single(report.RepairItems);

        Assert.Equal(LongRunningOperationKinds.BackupCreate, item.Kind);
        Assert.Equal(LongRunningOperationErrorCodes.UnsupportedCheckpointVersion, item.TerminalErrorCode);
        Assert.Equal(
            LongRunningOperationRecoveryRegistry.Descriptors[LongRunningOperationKinds.BackupCreate]
                .ManualRepairGuidance,
            item.Guidance);
        Assert.Equal(1, report.OperationsAwaitingRepair);
    }

    /// <summary>
    /// A kind with no owning handler is a build-time registration bug; the doctor names it rather
    /// than waiting for an operation of that kind to strand in production.
    /// </summary>
    [Fact]
    public async Task Kinds_without_a_handler_are_reported_as_a_registration_gap()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);

        DurableOperationDiagnosticsReport report = await Create(store, time).InspectAsync(time.GetUtcNow());

        Assert.Contains(LongRunningOperationKinds.InferenceRun, report.KindsWithoutHandler);
        Assert.True(report.NeedsAttention);
    }

    /// <summary>
    /// The detail string reaches <c>arcanum doctor</c> and <c>GET /api/health</c>, so it must stay
    /// inside the closed kind/state/error-code vocabularies — never an operation id or user content.
    /// </summary>
    [Fact]
    public async Task The_public_detail_exposes_only_closed_vocabulary()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        LongRunningOperation stuck = store.Seed(
            LongRunningOperationKinds.BackupCreate,
            LongRunningOperationRecoveryPolicy.AbandonSafely);

        _ = await store.TryTransitionAsync(
            stuck.Id,
            stuck.Revision,
            ownerId: null,
            LongRunningOperationState.ReconciliationRequired,
            time.GetUtcNow(),
            LongRunningOperationErrorCodes.CorruptCheckpoint);

        DurableOperationDiagnosticsReport report = await Create(store, time).InspectAsync(time.GetUtcNow());
        string detail = report.Describe();

        Assert.DoesNotContain(stuck.Id.ToString(), detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(stuck.PublicSummary, detail, StringComparison.Ordinal);
        Assert.Contains(LongRunningOperationKinds.BackupCreate, detail, StringComparison.Ordinal);
        Assert.Contains(LongRunningOperationErrorCodes.CorruptCheckpoint, detail, StringComparison.Ordinal);
    }
}
