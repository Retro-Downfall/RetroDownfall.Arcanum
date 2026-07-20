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
/// </summary>
internal sealed class CommandCenterWardCoordinator
{
    private readonly object _gate = new();
    private Action<WardApprovalRequest>? _onShow;
    private Action? _onHide;
    private TaskCompletionSource<WardApprovalDecision>? _pendingDecision;
    private WardApprovalRequest? _pendingRequest;

    public WardApprovalRequest? PendingRequest
    {
        get
        {
            lock (_gate)
            {
                return _pendingRequest;
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

        TaskCompletionSource<WardApprovalDecision> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<WardApprovalRequest>? onShow;
        Action? onHide;

        lock (_gate)
        {
            // Fail-closed: never overwrite a pending TCS without completing it as Deny.
            _ = TryResolvePendingWardAsDeniedUnlocked();
            _pendingRequest = request;
            _pendingDecision = tcs;
            onShow = _onShow;
            onHide = _onHide;
        }

        try
        {
            onShow?.Invoke(request);

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
            onHide?.Invoke();
            lock (_gate)
            {
                if (ReferenceEquals(_pendingDecision, tcs))
                {
                    _pendingRequest = null;
                    _pendingDecision = null;
                }
            }
        }
    }

    /// <summary>
    /// Completes a pending UI wait (Enter/Esc/slash). Returns false if nothing pending.
    /// </summary>
    public bool TryCompletePending(WardApprovalDecision decision)
    {
        TaskCompletionSource<WardApprovalDecision>? tcs;
        lock (_gate)
        {
            tcs = _pendingDecision;
        }

        return tcs is not null && tcs.TrySetResult(decision);
    }

    /// <summary>
    /// Completes any pending ward wait as <see cref="WardApprovalDecision.Deny"/> before another
    /// overlay steals focus. Idempotent: returns false when nothing pending or already completed.
    /// </summary>
    public bool TryResolvePendingWardAsDenied()
    {
        lock (_gate)
        {
            return TryResolvePendingWardAsDeniedUnlocked();
        }
    }

    private bool TryResolvePendingWardAsDeniedUnlocked()
    {
        TaskCompletionSource<WardApprovalDecision>? tcs = _pendingDecision;
        return tcs is not null && tcs.TrySetResult(WardApprovalDecision.Deny);
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
