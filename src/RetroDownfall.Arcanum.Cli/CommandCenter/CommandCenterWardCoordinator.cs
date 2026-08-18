using System.Text.Json;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

internal enum WardApprovalDecision
{
    Allow,
    Deny,
    AllowAlwaysThisTool,
}

internal sealed record WardApprovalRequest(
    string WardId,
    string ToolName,
    string? ArgumentsPreview);

/// <summary>
/// Bridges streaming <c>warded</c> events to the Command Center UI thread.
/// Host wires show/hide callbacks; ChatRunner awaits <see cref="RequestApprovalAsync"/>.
/// Display arbitration goes through <see cref="CommandCenterHardModalArbiter"/>.
/// </summary>
internal sealed class CommandCenterWardCoordinator(CommandCenterHardModalArbiter hardModalArbiter)
{
    private readonly object _gate = new();
    private Action<WardApprovalRequest>? _onShow;
    private Action? _onHide;
    private readonly Dictionary<string, PendingWard> _pendingById = new(StringComparer.Ordinal);
    private string? _displayedWardId;

    private sealed class PendingWard(
        WardApprovalRequest request,
        TaskCompletionSource<WardApprovalDecision> decision)
    {
        public WardApprovalRequest Request { get; } = request;
        public TaskCompletionSource<WardApprovalDecision> Decision { get; } = decision;
        public bool WasShown { get; set; }
    }

    public WardApprovalRequest? PendingRequest
    {
        get
        {
            lock (_gate)
            {
                if (_displayedWardId is not null
                    && _pendingById.TryGetValue(_displayedWardId, out PendingWard? displayed))
                {
                    return displayed.Request;
                }

                return null;
            }
        }
    }

    public void SetUiCallbacks(Action<WardApprovalRequest>? onShow, Action? onHide)
    {
        lock (_gate)
        {
            _onShow = onShow;
            _onHide = onHide;
        }
    }

    public async Task<WardApprovalDecision> RequestApprovalAsync(
        WardApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.WardId))
        {
            throw new ArgumentException("WardId is required.", nameof(request));
        }

        TaskCompletionSource<WardApprovalDecision> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingWard pending = new(request, tcs);
        Action<WardApprovalRequest>? onShow;
        Action? onHide;

        lock (_gate)
        {
            _pendingById[request.WardId] = pending;
            onShow = _onShow;
            onHide = _onHide;
        }

        _ = hardModalArbiter.RequestShow(
            CommandCenterHardModalKind.WardConfirm,
            request.WardId,
            () =>
            {
                lock (_gate)
                {
                    // Promotion hands over the slot before this callback runs, so the wait can already
                    // have completed and torn this entry down. Decline rather than paint a modal for a
                    // ward nobody can answer — the arbiter then promotes the next entry instead.
                    if (!_pendingById.TryGetValue(request.WardId, out PendingWard? live)
                        || !ReferenceEquals(live, pending))
                    {
                        return false;
                    }

                    pending.WasShown = true;
                    _displayedWardId = request.WardId;
                }

                onShow?.Invoke(request);
                return true;
            });

        try
        {
            await using CancellationTokenRegistration registration = cancellationToken.Register(
                static state =>
                {
                    _ = ((TaskCompletionSource<WardApprovalDecision>)state!).TrySetResult(WardApprovalDecision.Deny);
                },
                tcs);

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            bool wasShown;
            bool wasDisplayed;
            lock (_gate)
            {
                wasShown = pending.WasShown;
                wasDisplayed = string.Equals(_displayedWardId, request.WardId, StringComparison.Ordinal);
                if (_pendingById.TryGetValue(request.WardId, out PendingWard? current)
                    && ReferenceEquals(current, pending))
                {
                    _ = _pendingById.Remove(request.WardId);
                }

                if (wasDisplayed)
                {
                    _displayedWardId = null;
                }
            }

            if (wasShown && wasDisplayed)
            {
                onHide?.Invoke();
                _ = hardModalArbiter.TryClose(CommandCenterHardModalKind.WardConfirm, request.WardId);
            }
            else if (!hardModalArbiter.TryRemoveQueued(CommandCenterHardModalKind.WardConfirm, request.WardId))
            {
                // Neither displayed nor still queued: promotion landed in the window between "removed
                // from the queue" and "shown", so this ward silently owns the active slot. Release it
                // (id-matched, so a no-op otherwise) instead of wedging every later hard modal.
                _ = hardModalArbiter.TryClose(CommandCenterHardModalKind.WardConfirm, request.WardId);
            }
        }
    }

    /// <summary>
    /// Completes the currently displayed ward wait. Returns false if nothing displayed.
    /// </summary>
    public bool TryCompletePending(WardApprovalDecision decision)
    {
        TaskCompletionSource<WardApprovalDecision>? tcs;
        lock (_gate)
        {
            if (_displayedWardId is null
                || !_pendingById.TryGetValue(_displayedWardId, out PendingWard? pending))
            {
                return false;
            }

            tcs = pending.Decision;
        }

        return tcs.TrySetResult(decision);
    }

    /// <summary>
    /// Completes a specific ward by id (displayed or queued). Used for correlated stale closes.
    /// </summary>
    public bool TryCompleteByWardId(string wardId, WardApprovalDecision decision)
    {
        if (string.IsNullOrWhiteSpace(wardId))
        {
            return false;
        }

        TaskCompletionSource<WardApprovalDecision>? tcs;
        lock (_gate)
        {
            if (!_pendingById.TryGetValue(wardId, out PendingWard? pending))
            {
                return false;
            }

            tcs = pending.Decision;
        }

        return tcs.TrySetResult(decision);
    }

    /// <summary>
    /// Completes the displayed ward as Deny. Does not deny queued wards (they keep observing timeout).
    /// Idempotent: returns false when nothing displayed or already completed.
    /// </summary>
    public bool TryResolvePendingWardAsDenied()
    {
        return TryCompletePending(WardApprovalDecision.Deny);
    }

    /// <summary>
    /// Denies all pending wards (displayed and queued) — used on quit / session teardown.
    /// </summary>
    public void DenyAllPending()
    {
        List<TaskCompletionSource<WardApprovalDecision>> targets;
        lock (_gate)
        {
            targets = _pendingById.Values.Select(p => p.Decision).ToList();
        }

        foreach (TaskCompletionSource<WardApprovalDecision> tcs in targets)
        {
            _ = tcs.TrySetResult(WardApprovalDecision.Deny);
        }
    }

    public static string FormatArgumentsPreview(JsonElement? arguments, int maxChars = 480)
    {
        if (arguments is null)
        {
            return string.Empty;
        }

        string raw = arguments.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? string.Empty
            : arguments.Value.ToString();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        raw = raw.Replace('\r', ' ').Replace('\n', ' ');
        return raw.Length <= maxChars ? raw : raw[..maxChars] + "…";
    }
}
