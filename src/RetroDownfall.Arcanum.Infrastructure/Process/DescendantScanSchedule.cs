namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Decides, one monitor tick at a time, whether the macOS descendant supervisor pays for a full
/// process-table scan on that tick.
/// </summary>
/// <remarks>
/// <para>
/// The scan is the only way to learn a descendant's identity: <c>NOTE_FORK</c> says a tracked process
/// forked but never which pid it created, and a descendant that escapes its process group and outlives
/// its parent can no longer be attributed to the root at all. So the scan runs on <em>every</em> tick
/// whenever something could have changed — a kernel event from a watched process, or a scan that just
/// found a new process (whose own watcher was registered only after that scan, so a fork it makes in
/// between has no event to announce it) — and backs off to a slow safety cadence once a full window of
/// scans has found nothing new.
/// </para>
/// <para>
/// Counted in ticks rather than time so the behaviour does not depend on how fast the host is running
/// the monitor loop. Not thread-safe: only the monitor loop calls it.
/// </para>
/// </remarks>
internal sealed class DescendantScanSchedule(bool eventDriven)
{
    /// <summary>
    /// Ticks scanned back to back after a kernel event or a scan that found a new process: a quarter of
    /// a second at the nominal 10 ms tick.
    /// </summary>
    internal const int ActiveWindowTicks = 25;

    /// <summary>
    /// Once quiescent, one scan every this many ticks. The kqueue watchers are the primary detector; this
    /// is the bounded-latency net under a missed event.
    /// </summary>
    internal const int IdleScanEveryTicks = 25;

    private int _activeTicksRemaining = ActiveWindowTicks;

    private int _idleTicks;

    private bool _continuous;

    /// <summary>
    /// Forces a scan on every tick from now on. Called when a live process's watcher could not be
    /// registered, because its forks can then no longer be heard and only the scan can see them.
    /// </summary>
    internal void RequireContinuousScanning() => _continuous = true;

    internal bool ShouldScan(bool kernelEventObserved)
    {
        if (!eventDriven || _continuous)
        {
            return true;
        }

        if (kernelEventObserved)
        {
            _activeTicksRemaining = ActiveWindowTicks;
        }

        if (_activeTicksRemaining > 0)
        {
            _activeTicksRemaining--;

            _idleTicks = 0;

            return true;
        }

        if (++_idleTicks >= IdleScanEveryTicks)
        {
            _idleTicks = 0;

            return true;
        }

        return false;
    }

    /// <summary>Reports what the scan found, so a change keeps the active window open.</summary>
    internal void RecordScan(bool trackedSetChanged)
    {
        if (trackedSetChanged)
        {
            _activeTicksRemaining = ActiveWindowTicks;
        }
    }
}
