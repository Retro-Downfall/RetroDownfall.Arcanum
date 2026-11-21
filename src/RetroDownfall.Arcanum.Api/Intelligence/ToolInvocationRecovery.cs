using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>The application-side continuation of one durable, already authorized tool slot.</summary>
internal sealed class ToolInvocationRecovery(IToolInvocationStore store, GrimoireTurnWriter writer,
    ToolInvocationKey key, ToolInvocationIdentity identity, SessionTurnClaimLease? owner, string arguments, string model)
{
    private ToolInvocationEvidence? _prepared;

    private Error? _preparationFailure;

    private Guid? _dispatchId;

    internal bool AdmittedForDispatch => _dispatchId is not null;

    internal bool ReusedReceipt { get; private set; }

    internal bool CompletionRecorded { get; private set; }

    internal Guid ReceiptId => key.Receipt.Id;

    internal DateTimeOffset? CreatedAt => _prepared?.CreatedAt;

    internal async Task PrepareAsync(CancellationToken ct)
    {
        var prepared = await store.PrepareAsync(key, owner, ct).ConfigureAwait(false);

        if (prepared.IsSuccess) _prepared = prepared.Value;
        else _preparationFailure = prepared.Error;
    }

    internal async Task<Result<string?>> AdmitOrRecoverAsync(CancellationToken ct)
    {
        if (_prepared is null) return _preparationFailure ?? PreDispatchUnavailable;

        var decision = await store.AdmitAsync(key, owner, ct).ConfigureAwait(false);

        if (decision.IsFailure) return decision.Error;

        if (decision.Value.DispatchId is { } dispatch)
        {
            _dispatchId = dispatch;

            return Result<string?>.Success(null);
        }

        CompletionRecorded = decision.Value.Evidence.State == ToolInvocationState.Completed;

        var probe = await writer.ProbeToolInvocationReceiptAsync(new(key.SessionId, key.Receipt, identity.ProviderToolCallId,
            identity.ToolName, arguments, model, _prepared.CreatedAt), ct).ConfigureAwait(false);

        if (probe is not { Outcome: MandatoryToolInteractionProbeOutcome.Replayed, Result: { } result })
            return CompletionRecorded ? RecordedResultUnavailable : Unresolved;

        ReusedReceipt = true;

        _ = await store.ConfirmAsync(key, key.Receipt.ResultEntryId, ct).ConfigureAwait(false);

        return result;
    }

    internal async Task ReleaseUndispatchedAsync(CancellationToken ct)
    {
        if (_dispatchId is not { } dispatch) return;

        var released = await store.ReleaseUndispatchedAsync(key, dispatch, owner, ct).ConfigureAwait(false);

        if (released.IsSuccess) _dispatchId = null;
    }

    internal async Task<bool> RecordResultAsync(string result, bool ownerReceiptHandled, CancellationToken ct)
    {
        if (!AdmittedForDispatch || _prepared is null) return ReusedReceipt;

        if (!ownerReceiptHandled)
        {
            var commit = await writer.PersistToolInvocationReceiptAsync(new(key.SessionId, key.Receipt, identity.ProviderToolCallId,
                identity.ToolName, arguments, result, model, _prepared.CreatedAt), ct).ConfigureAwait(false);

            if (commit.Outcome is not (MandatoryToolInteractionAppendOutcome.NewlyCommitted or MandatoryToolInteractionAppendOutcome.RecoveredCommitted))
                return false;
        }

        _ = await store.ConfirmAsync(key, key.Receipt.ResultEntryId, ct).ConfigureAwait(false);

        return true;
    }

    private static Error Unresolved => new(ErrorCodes.Hub.ToolReconciliationRequired, "The tool effect is awaiting reconciliation. Preserve the invocation ID and inspect its owning Session or tool receipt before retrying.");

    private static Error RecordedResultUnavailable => new(ErrorCodes.Hub.ToolReconciliationRequired, "The tool invocation completed. Its saved result is unavailable through the current Session.");

    private static Error PreDispatchUnavailable => new(ErrorCodes.Hub.Error, "The Session tool invocation could not be prepared for dispatch.");
}
