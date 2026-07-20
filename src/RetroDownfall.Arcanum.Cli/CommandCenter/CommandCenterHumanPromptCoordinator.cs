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
/// </summary>
internal sealed class CommandCenterHumanPromptCoordinator(ArcanumApiClient apiClient)
{
    private readonly object _gate = new();
    private Action<HumanPromptRequest, string?>? _onShow;
    private Action<HumanPromptCloseReason, string?>? _onHide;
    private Action<string?>? _onStatus;
    private HumanPromptRequest? _pending;
    private bool _submitInFlight;
    private string? _statusMessage;

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
    /// Opens (or replaces) the pending prompt. Correlates by CallId + promptId.
    /// </summary>
    public void BeginPrompt(HumanPromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PromptId))
        {
            throw new ArgumentException("promptId is required.", nameof(request));
        }

        Action<HumanPromptRequest, string?>? onShow;
        Action<HumanPromptCloseReason, string?>? onHideReplace;
        lock (_gate)
        {
            if (_pending is not null)
            {
                onHideReplace = _onHide;
                _pending = null;
                _submitInFlight = false;
                _statusMessage = null;
            }
            else
            {
                onHideReplace = null;
            }

            _pending = request;
            _submitInFlight = false;
            _statusMessage = null;
            onShow = _onShow;
        }

        // Close any previous overlay before showing the replacement.
        onHideReplace?.Invoke(HumanPromptCloseReason.Cancelled, null);
        onShow?.Invoke(request, null);
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

    public bool TryClose(HumanPromptCloseReason reason, string? notice = null)
    {
        Action<HumanPromptCloseReason, string?>? onHide;
        lock (_gate)
        {
            if (_pending is null)
            {
                return false;
            }

            _pending = null;
            _submitInFlight = false;
            _statusMessage = null;
            onHide = _onHide;
        }

        onHide?.Invoke(reason, notice);
        return true;
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
        // Keep show callback optional refresh path when status changes mid-flight.
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
                _ = TryClose(HumanPromptCloseReason.Submitted);
                return HumanPromptSubmitOutcome.Accepted;
            }

            if (string.Equals(
                    result.Error.Code,
                    ErrorCodes.Intelligence.HumanPromptNotFound,
                    StringComparison.Ordinal))
            {
                _ = TryClose(
                    HumanPromptCloseReason.Expired,
                    "Human prompt expired (no longer waiting).");
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
                _submitInFlight = false;
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
