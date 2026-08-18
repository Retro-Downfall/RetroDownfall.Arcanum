namespace RetroDownfall.Arcanum.Cli.CommandCenter;

internal enum CommandCenterHardModalKind
{
    WardConfirm,
    HumanPrompt,
}

/// <summary>
/// Deterministic hard-modal arbitration for Command Center:
/// active never preempted; subsequent queued; Ward priority when promoting;
/// auxiliary overlays blocked while any hard modal is active or queued.
/// </summary>
internal sealed class CommandCenterHardModalArbiter
{
    private readonly object _gate = new();
    private ActiveHardModal? _active;
    private readonly List<QueuedHardModal> _queue = [];

    private sealed record ActiveHardModal(CommandCenterHardModalKind Kind, string Id);

    private sealed record QueuedHardModal(CommandCenterHardModalKind Kind, string Id, Func<bool> Show);

    public bool HasActiveHardModal
    {
        get
        {
            lock (_gate)
            {
                return _active is not null;
            }
        }
    }

    public bool HasQueuedHardModal
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count > 0;
            }
        }
    }

    /// <summary>True while a hard modal is active or any hard modal is queued.</summary>
    public bool BlocksAuxiliary
    {
        get
        {
            lock (_gate)
            {
                return _active is not null || _queue.Count > 0;
            }
        }
    }

    public CommandCenterHardModalKind? ActiveKind
    {
        get
        {
            lock (_gate)
            {
                return _active?.Kind;
            }
        }
    }

    public string? ActiveId
    {
        get
        {
            lock (_gate)
            {
                return _active?.Id;
            }
        }
    }

    /// <summary>
    /// Shows immediately when idle; otherwise queues. Returns <c>true</c> when shown now.
    /// The callback returns <c>false</c> to decline the slot it was handed — the owner resolved
    /// before the display reached it — and the arbiter then releases the slot and promotes the next
    /// entry instead of staying active forever.
    /// </summary>
    public bool RequestShow(CommandCenterHardModalKind kind, string id, Func<bool> show)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(show);

        Func<bool>? showNow;
        lock (_gate)
        {
            if (_active is null)
            {
                _active = new ActiveHardModal(kind, id);
                showNow = show;
            }
            else
            {
                _queue.Add(new QueuedHardModal(kind, id, show));
                showNow = null;
            }
        }

        return showNow is not null && ShowOrRelease(kind, id, showNow);
    }

    /// <summary>
    /// Closes the active hard modal only when kind+id match, then promotes the next queued
    /// entry (Wards before HumanPrompt).
    /// </summary>
    public bool TryClose(CommandCenterHardModalKind kind, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        QueuedHardModal? next;
        lock (_gate)
        {
            if (_active is null
                || _active.Kind != kind
                || !string.Equals(_active.Id, id, StringComparison.Ordinal))
            {
                return false;
            }

            _active = null;
            next = PromoteNextUnlocked();
        }

        if (next is not null)
        {
            _ = ShowOrRelease(next.Kind, next.Id, next.Show);
        }

        return true;
    }

    /// <summary>
    /// Removes a queued hard modal that resolved/denied/timed out before display.
    /// </summary>
    public bool TryRemoveQueued(CommandCenterHardModalKind kind, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_gate)
        {
            int index = _queue.FindIndex(q =>
                q.Kind == kind && string.Equals(q.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            _queue.RemoveAt(index);
            return true;
        }
    }

    public bool IsActive(CommandCenterHardModalKind kind, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_gate)
        {
            return _active is not null
                && _active.Kind == kind
                && string.Equals(_active.Id, id, StringComparison.Ordinal);
        }
    }

    public bool IsQueued(CommandCenterHardModalKind kind, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_gate)
        {
            return _queue.Exists(q =>
                q.Kind == kind && string.Equals(q.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Invokes a show callback that already owns the active slot, outside <c>_gate</c> (the callbacks
    /// take the coordinators' own locks, so holding this one across them would invert the lock order).
    /// A callback that declines — its owner resolved in the window between being dequeued and being
    /// shown — releases the slot and promotes the next entry, so an abandoned modal can never leave
    /// <c>_active</c> set with nothing on screen to close it.
    /// </summary>
    private bool ShowOrRelease(CommandCenterHardModalKind kind, string id, Func<bool> show)
    {
        CommandCenterHardModalKind currentKind = kind;
        string currentId = id;
        Func<bool> currentShow = show;
        bool firstShown = false;
        bool first = true;

        while (true)
        {
            bool shown = currentShow();
            if (first)
            {
                firstShown = shown;
                first = false;
            }

            if (shown)
            {
                return firstShown;
            }

            QueuedHardModal? next;
            lock (_gate)
            {
                if (_active is null
                    || _active.Kind != currentKind
                    || !string.Equals(_active.Id, currentId, StringComparison.Ordinal))
                {
                    // Someone else already re-owned the slot; leave it alone.
                    return firstShown;
                }

                _active = null;
                next = PromoteNextUnlocked();
            }

            if (next is null)
            {
                return firstShown;
            }

            currentKind = next.Kind;
            currentId = next.Id;
            currentShow = next.Show;
        }
    }

    private QueuedHardModal? PromoteNextUnlocked()
    {
        if (_queue.Count == 0)
        {
            return null;
        }

        // Wards have priority over HumanPrompt when choosing the next queued modal.
        int wardIndex = _queue.FindIndex(static q => q.Kind == CommandCenterHardModalKind.WardConfirm);
        int index = wardIndex >= 0 ? wardIndex : 0;
        QueuedHardModal next = _queue[index];
        _queue.RemoveAt(index);
        _active = new ActiveHardModal(next.Kind, next.Id);
        return next;
    }
}
