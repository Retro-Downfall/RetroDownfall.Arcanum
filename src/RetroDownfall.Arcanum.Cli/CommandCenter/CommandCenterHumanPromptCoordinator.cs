using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

internal sealed record HumanPromptRequest(
    string CallId,
    string PromptId,
    string Question);

internal enum HumanPromptCloseReason
{
    Submitted,
    Expired,
    Cancelled,
}

internal enum HumanPromptSubmitOutcome
{
    Accepted,
    NotFound,
    TransientFailure,
    RejectedEmpty,
    NotActive,
    AlreadyInFlight,
}

/// <summary>
/// Bridges streamed <c>ask_human</c> ToolCall events to the Command Center UI thread.
/// Host wires show/hide/status callbacks; ChatRunner opens and expires; Host submits.
/// Does not block the stream pump — timeout/ToolError frames must still be readable.
/// Display arbitration goes through <see cref="CommandCenterHardModalArbiter"/>.
/// </summary>
internal sealed class CommandCenterHumanPromptCoordinator(
    ArcanumApiClient apiClient,
    CommandCenterHardModalArbiter hardModalArbiter)
{
    private readonly object _gate = new();
    private Action<HumanPromptRequest, string?>? _onShow;
    private Action<HumanPromptCloseReason, string?>? _onHide;
    private Action<string?>? _onStatus;
    private HumanPromptRequest? _pending;
    private bool _submitInFlight;
    private string? _statusMessage;
    private readonly List<HumanPromptRequest> _queued = [];

    public HumanPromptRequest? Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    public bool IsSubmitInFlight
    {
        get
        {
            lock (_gate)
            {
                return _submitInFlight;
            }
        }
    }

    public string? StatusMessage
    {
        get
        {
            lock (_gate)
            {
                return _statusMessage;
            }
        }
    }

    public void SetUiCallbacks(
        Action<HumanPromptRequest, string?>? onShow,
        Action<HumanPromptCloseReason, string?>? onHide,
        Action<string?>? onStatus)
    {
        lock (_gate)
        {
            _onShow = onShow;
            _onHide = onHide;
            _onStatus = onStatus;
        }
    }

    /// <summary>
    /// Opens the prompt when the hard-modal slot is free; otherwise queues it.
    /// Never preempts an active hard modal.
    /// </summary>
    public void BeginPrompt(HumanPromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PromptId))
        {
            throw new ArgumentException("promptId is required.", nameof(request));
        }

        Action<HumanPromptRequest, string?>? onShow;
        lock (_gate)
        {
            onShow = _onShow;
            // If this prompt is already pending (retry), refresh display only when active.
            if (_pending is not null
                && string.Equals(_pending.PromptId, request.PromptId, StringComparison.Ordinal))
            {
                _pending = request;
                _submitInFlight = false;
                _statusMessage = null;
            }
            else if (_pending is null
                && !hardModalArbiter.HasActiveHardModal
                && !hardModalArbiter.IsQueued(CommandCenterHardModalKind.HumanPrompt, request.PromptId))
            {
                _pending = request;
                _submitInFlight = false;
                _statusMessage = null;
            }
            else if (!_queued.Exists(q =>
                         string.Equals(q.PromptId, request.PromptId, StringComparison.Ordinal)))
            {
                _queued.Add(request);
            }
        }

        _ = hardModalArbiter.RequestShow(
            CommandCenterHardModalKind.HumanPrompt,
            request.PromptId,
            () => ShowQueuedOrPending(request, onShow));
    }

    private bool ShowQueuedOrPending(
        HumanPromptRequest request,
        Action<HumanPromptRequest, string?>? onShow)
    {
        HumanPromptRequest toShow;
        lock (_gate)
        {
            // Promote from queue if this show is for a queued id, else use request.
            int queuedIndex = _queued.FindIndex(q =>
                string.Equals(q.PromptId, request.PromptId, StringComparison.Ordinal));
            if (queuedIndex >= 0)
            {
                toShow = _queued[queuedIndex];
                _queued.RemoveAt(queuedIndex);
            }
            else if (_pending is not null
                && string.Equals(_pending.PromptId, request.PromptId, StringComparison.Ordinal))
            {
                // Opened directly and only then queued by the arbiter — still this prompt's turn.
                toShow = request;
            }
            else
            {
                // Closed (submitted, expired, cancelled) while it waited for the slot. Decline it so
                // the arbiter promotes the next entry instead of displaying a dead prompt.
                return false;
            }

            _pending = toShow;
            _submitInFlight = false;
            _statusMessage = null;
            onShow ??= _onShow;
        }

        onShow?.Invoke(toShow, null);
        return true;
    }

    /// <summary>
    /// True when the active prompt matches both CallId and promptId.
    /// Empty event CallId never matches a non-empty pending CallId.
    /// </summary>
    public bool Matches(string? callId, string? promptId)
    {
        lock (_gate)
        {
            if (_pending is null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(callId)
                || !string.Equals(_pending.CallId, callId, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(promptId)
                || !string.Equals(_pending.PromptId, promptId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Matches the active prompt by CallId alone (ToolResult / ToolError frames).
    /// </summary>
    public bool MatchesCallId(string? callId)
    {
        lock (_gate)
        {
            return _pending is not null
                && !string.IsNullOrWhiteSpace(callId)
                && string.Equals(_pending.CallId, callId, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Closes only when <paramref name="promptId"/> matches the active pending prompt.
    /// Stale closes for a previous prompt are ignored.
    /// </summary>
    public bool TryClose(
        HumanPromptCloseReason reason,
        string? notice = null,
        string? promptId = null)
    {
        Action<HumanPromptCloseReason, string?>? onHide;
        string? closedId;
        lock (_gate)
        {
            if (_pending is null)
            {
                // May still be queued — drop without display.
                if (!string.IsNullOrWhiteSpace(promptId))
                {
                    _ = _queued.RemoveAll(q =>
                        string.Equals(q.PromptId, promptId, StringComparison.Ordinal));
                    _ = hardModalArbiter.TryRemoveQueued(
                        CommandCenterHardModalKind.HumanPrompt,
                        promptId!);
                }

                return false;
            }

            if (!string.IsNullOrWhiteSpace(promptId)
                && !string.Equals(_pending.PromptId, promptId, StringComparison.Ordinal))
            {
                // Stale close for a different prompt — also drop if that id is only queued.
                _ = _queued.RemoveAll(q =>
                    string.Equals(q.PromptId, promptId, StringComparison.Ordinal));
                _ = hardModalArbiter.TryRemoveQueued(
                    CommandCenterHardModalKind.HumanPrompt,
                    promptId!);
                return false;
            }

            closedId = _pending.PromptId;
            _pending = null;
            _submitInFlight = false;
            _statusMessage = null;
            onHide = _onHide;
        }

        onHide?.Invoke(reason, notice);

        // A prompt can be pending here while the arbiter only has it queued (it opened, then a hard
        // modal took the slot before RequestShow ran), so release whichever slot it actually holds.
        if (!hardModalArbiter.TryClose(CommandCenterHardModalKind.HumanPrompt, closedId))
        {
            _ = hardModalArbiter.TryRemoveQueued(CommandCenterHardModalKind.HumanPrompt, closedId);
        }

        return true;
    }

    /// <summary>
    /// Closes the active prompt (if any), correlating by its PromptId.
    /// </summary>
    public bool TryCloseActive(HumanPromptCloseReason reason, string? notice = null)
    {
        string? id;
        lock (_gate)
        {
            id = _pending?.PromptId;
        }

        return id is not null && TryClose(reason, notice, id);
    }

    public void SetStatus(string? message)
    {
        Action<string?>? onStatus;
        HumanPromptRequest? pending;
        lock (_gate)
        {
            if (_pending is null)
            {
                return;
            }

            _statusMessage = message;
            pending = _pending;
            onStatus = _onStatus;
        }

        onStatus?.Invoke(message);
        _ = pending;
    }

    public async Task<HumanPromptSubmitOutcome> SubmitAnswerAsync(
        string answer,
        CancellationToken cancellationToken)
    {
        string trimmed = answer?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            SetStatus("Answer cannot be empty.");
            return HumanPromptSubmitOutcome.RejectedEmpty;
        }

        HumanPromptRequest request;
        lock (_gate)
        {
            if (_pending is null)
            {
                return HumanPromptSubmitOutcome.NotActive;
            }

            if (_submitInFlight)
            {
                return HumanPromptSubmitOutcome.AlreadyInFlight;
            }

            _submitInFlight = true;
            request = _pending;
            _statusMessage = null;
        }

        Action<string?>? onStatus;
        lock (_gate)
        {
            onStatus = _onStatus;
        }

        onStatus?.Invoke(null);

        try
        {
            Result<bool> result = await apiClient
                .SubmitHumanResponseAsync(request.PromptId, trimmed, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                // Correlate by PromptId — stale submit for A must not close B.
                _ = TryClose(HumanPromptCloseReason.Submitted, notice: null, promptId: request.PromptId);
                return HumanPromptSubmitOutcome.Accepted;
            }

            if (string.Equals(
                    result.Error.Code,
                    ErrorCodes.Intelligence.HumanPromptNotFound,
                    StringComparison.Ordinal))
            {
                _ = TryClose(
                    HumanPromptCloseReason.Expired,
                    "Human prompt expired (no longer waiting).",
                    promptId: request.PromptId);
                return HumanPromptSubmitOutcome.NotFound;
            }

            // Transient / other HTTP failure while still active — allow retry.
            string message = string.IsNullOrWhiteSpace(result.Error.Message)
                ? $"Submit failed ({result.Error.Code})."
                : result.Error.Message;

            bool stillActive;
            lock (_gate)
            {
                _submitInFlight = false;
                stillActive = _pending is not null
                    && string.Equals(_pending.PromptId, request.PromptId, StringComparison.Ordinal);
                if (stillActive)
                {
                    _statusMessage = message;
                }
            }

            if (stillActive)
            {
                onStatus?.Invoke(message);
                return HumanPromptSubmitOutcome.TransientFailure;
            }

            return HumanPromptSubmitOutcome.NotActive;
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                // Only clear in-flight if this submit's prompt is still the active one.
                if (_pending is not null
                    && string.Equals(_pending.PromptId, request.PromptId, StringComparison.Ordinal))
                {
                    _submitInFlight = false;
                }
            }

            throw;
        }
        catch (Exception ex)
        {
            string message = string.IsNullOrWhiteSpace(ex.Message)
                ? "Submit failed."
                : ex.Message;

            bool stillActive;
            lock (_gate)
            {
                _submitInFlight = false;
                stillActive = _pending is not null
                    && string.Equals(_pending.PromptId, request.PromptId, StringComparison.Ordinal);
                if (stillActive)
                {
                    _statusMessage = message;
                }
            }

            if (stillActive)
            {
                onStatus?.Invoke(message);
                return HumanPromptSubmitOutcome.TransientFailure;
            }

            return HumanPromptSubmitOutcome.NotActive;
        }
    }

    /// <summary>
    /// True when text is the locked HITL timeout message (ToolResult / ToolError).
    /// </summary>
    public static bool IsTimeoutText(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Contains(HumanPromptTimeoutException.DefaultMessage, StringComparison.Ordinal);
}
