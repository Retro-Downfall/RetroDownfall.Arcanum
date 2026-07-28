using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Supplied by the inference pipeline only after it has a persisted session/assistant-turn handle,
/// an exact call snapshot, and a deterministic logical invocation identity. There is deliberately
/// no stateless fallback.
/// </summary>
internal sealed record ApplyPatchInvocationContext(
    Guid SessionId,
    Guid AssistantEntryId,
    ToolInvocationIdentity Identity,
    string SerializedArguments,
    string ModelUsed,
    DateTimeOffset CreatedAt,
    IApplyPatchPendingReceiptSink Sink)
{
    private int _handoffOutcome = -1;

    private int _cancellationClassified;

    private int _dispatched;

    private int _requiresTurnFailure;

    internal MandatoryToolInteractionAppendOutcome? HandoffOutcome =>
        Volatile.Read(ref _handoffOutcome) is int value && value >= 0
            ? (MandatoryToolInteractionAppendOutcome)value
            : null;

    internal bool ReceiptHandled =>
        HandoffOutcome is MandatoryToolInteractionAppendOutcome.NewlyCommitted
            or MandatoryToolInteractionAppendOutcome.RecoveredCommitted;

    internal bool RequiresTurnFailure =>
        Volatile.Read(ref _requiresTurnFailure) != 0
        || HandoffOutcome == MandatoryToolInteractionAppendOutcome.Ambiguous
        || (Volatile.Read(ref _dispatched) != 0
            && HandoffOutcome is null);

    internal bool CancellationClassified =>
        Volatile.Read(ref _cancellationClassified) != 0;

    internal bool WasDispatched =>
        Volatile.Read(ref _dispatched) != 0;

    internal void MarkDispatched() =>
        Interlocked.Exchange(ref _dispatched, 1);

    internal void RecordHandoffOutcome(
        MandatoryToolInteractionAppendOutcome outcome) =>
        Interlocked.Exchange(ref _handoffOutcome, (int)outcome);

    internal void RecordCancellationOutcome(
        MandatoryToolInteractionAppendOutcome outcome)
    {
        RecordHandoffOutcome(outcome);
        Interlocked.Exchange(ref _cancellationClassified, 1);
    }

    internal void RequireTurnFailure() =>
        Interlocked.Exchange(ref _requiresTurnFailure, 1);
}

internal static class ApplyPatchInvocationAmbient
{
    private static readonly AsyncLocal<ApplyPatchInvocationContext?> CurrentValue =
        new();

    internal static ApplyPatchInvocationContext? Current
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = value;
    }

    internal static IDisposable Begin(
        ApplyPatchInvocationContext context)
    {

        ArgumentNullException.ThrowIfNull(context);

        ApplyPatchInvocationContext? previous = Current;

        Current = context;

        return new Scope(previous);

    }

    private sealed class Scope(
        ApplyPatchInvocationContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {

            if (_disposed)
            {
                return;
            }

            _disposed = true;

            Current = previous;

        }
    }
}

internal static class ApplyPatchInvocationBinding
{
    private static readonly TimeSpan BindingTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private static readonly ExpiringRequestBindingStore<ApplyPatchInvocationContext>
        ByRequest = new(
            BindingTtl.Ticks,
            SweepInterval.Ticks,
            static () => DateTime.UtcNow.Ticks);

    internal static void BindRequest(
        string connectionKey,
        string requestId,
        ApplyPatchInvocationContext context)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);

        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        ArgumentNullException.ThrowIfNull(context);

        ByRequest.Bind(connectionKey, requestId, context);

    }

    internal static bool TryResolveRequest(
        string connectionKey,
        string requestId,
        out ApplyPatchInvocationContext? context)
    {

        context = null;

        if (string.IsNullOrWhiteSpace(connectionKey)
            || string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        if (!ByRequest.TryResolve(
                connectionKey,
                requestId,
                out ApplyPatchInvocationContext resolved))
        {
            return false;
        }

        context = resolved;

        return true;

    }

    internal static void UnbindRequest(
        string connectionKey,
        string requestId)
    {

        if (string.IsNullOrWhiteSpace(connectionKey)
            || string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        ByRequest.Unbind(connectionKey, requestId);
    }
}

/// <summary>
/// The patch handler transfers ownership here after reversible commit and before its MCP response
/// can continue toward the model. The production sink classifies mandatory append before making
/// the filesystem transaction irreversible.
/// </summary>
internal enum ApplyPatchReceiptProbeOutcome
{
    NotFound,
    Replayed,
    Mismatched,
    Unavailable,
}

internal sealed record ApplyPatchReceiptProbe(
    Guid SessionId,
    ToolInteractionReceipt Receipt,
    string? ToolCallId,
    string ToolName,
    string SerializedArguments,
    string ModelUsed,
    DateTimeOffset CreatedAt);

internal readonly record struct ApplyPatchReceiptProbeResult(
    ApplyPatchReceiptProbeOutcome Outcome,
    string? SerializedResult);

internal enum ApplyPatchReceiptPreflightOutcome
{
    Admitted,
    Replayed,
    Rejected,
    Mismatched,
    Unavailable,
}

internal sealed record ApplyPatchReceiptPreflight(
    Guid SessionId,
    ToolInteractionReceipt Receipt,
    string? ToolCallId,
    string ToolName,
    string SerializedArguments,
    string SerializedResult,
    string ModelUsed,
    DateTimeOffset CreatedAt);

internal readonly record struct ApplyPatchReceiptPreflightResult(
    ApplyPatchReceiptPreflightOutcome Outcome,
    string? SerializedResult);

internal sealed record ApplyPatchRecoveryReceipt(
    Guid SessionId,
    ToolInteractionReceipt Receipt,
    string? ToolCallId,
    string ToolName,
    string SerializedArguments,
    string SerializedResult,
    string ModelUsed,
    DateTimeOffset CreatedAt);

internal interface IApplyPatchPendingReceiptSink
{
    ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
        ApplyPatchReceiptProbe probe,
        CancellationToken cancellationToken);

    ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
        ApplyPatchReceiptPreflight preflight,
        CancellationToken cancellationToken);

    ValueTask<MandatoryToolInteractionAppendOutcome>
        PersistRecoveryReceiptAsync(
            ApplyPatchRecoveryReceipt receipt,
            CancellationToken cancellationToken);

    ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
        PendingApplyPatchReceipt receipt,
        CancellationToken cancellationToken);
}

internal readonly record struct ApplyPatchPendingReceiptHandoffResult(
    MandatoryToolInteractionAppendOutcome Outcome,
    WorkspaceArtifactCleanupResult? Cleanup,
    WorkspaceRollbackResult? Rollback);

internal sealed class ApplyPatchReceiptHandoffException
    : InvalidOperationException
{
    internal ApplyPatchReceiptHandoffException(
        MandatoryToolInteractionAppendOutcome outcome,
        Exception? innerException = null,
        WorkspaceCommitRecovery? recovery = null)
        : base(
            $"The apply_patch receipt handoff ended as {outcome}.",
            innerException)
    {
        Outcome = outcome;
        AffectedPaths = WorkspaceRelativePath.NormalizeDistinctOrdered(
            recovery?.AffectedPaths);
        RecoveryArtifactPaths =
            WorkspaceRelativePath.NormalizeDistinctOrdered(
                recovery?.ArtifactPaths);
    }

    internal MandatoryToolInteractionAppendOutcome Outcome { get; }

    internal IReadOnlyList<string> AffectedPaths { get; }

    internal IReadOnlyList<string> RecoveryArtifactPaths { get; }
}

internal sealed record PendingApplyPatchReceipt(
    Guid SessionId,
    ToolInteractionReceipt Receipt,
    string? ToolCallId,
    string ToolName,
    string SerializedArguments,
    string SerializedResult,
    string ModelUsed,
    DateTimeOffset CreatedAt,
    WorkspaceCommitRecovery? Recovery,
    ReversibleWorkspaceCommit Transaction)
{
    internal Task<WorkspaceRollbackResult> RollbackAsync(
        CancellationToken cancellationToken) =>
        Transaction.RollbackAsync(cancellationToken);

    internal Task<WorkspaceArtifactCleanupResult> MarkIrreversibleAsync(
        CancellationToken cancellationToken) =>
        Transaction.MarkIrreversibleAsync(cancellationToken);
}
