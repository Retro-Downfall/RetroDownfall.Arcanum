using System.Diagnostics;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

[Collection("ChildProcess")]
public sealed class MacOsDescendantSupervisorTests
{

    /// <summary>
    /// The full process-table scan must run on every monitor tick. macOS delivers NOTE_FORK without
    /// the child's pid, so the kqueue watcher can report that a tracked process forked but never
    /// which pid to track: the scan is the only way to learn a descendant's identity. It has to do so
    /// before that descendant escapes its process group and its parent exits, because after
    /// reparenting to launchd no ancestry walk can attribute it to this root and containment is lost
    /// permanently. Throttling the scan is therefore not a performance trade — it silently widens the
    /// escape window. The scan is expensive (two proc_pidinfo syscalls per live pid); the way to pay
    /// less is to make each scan cheaper, never to scan less often.
    /// </summary>
    [SkippableFact]
    public async Task Monitor_loop_scans_the_process_table_on_every_tick()
    {

        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        using System.Diagnostics.Process child = new();

        child.StartInfo = new ProcessStartInfo("/bin/sleep", "5")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _ = child.Start();

        MacOsDescendantSupervisor? supervisor = MacOsDescendantSupervisor.TryStart(child.Id);

        Skip.If(supervisor is null, "The supervisor could not attach to the child on this host.");

        try
        {

            await Task.Delay(TimeSpan.FromSeconds(1));

            // Assert the invariant itself — one scan per tick — rather than a scans-per-second rate.
            // A rate threshold has to be calibrated against wall clock, and the honest loop slows
            // down by an order of magnitude under coverage instrumentation on a loaded parallel
            // suite, so any floor is either too tight (false failures) or too loose to catch the
            // regression. Comparing the two counters is exact and load-independent: a throttled
            // cadence makes scans fall behind ticks immediately, however slowly the host is running.
            long ticks = supervisor!.MonitorTickCount;

            long scans = supervisor.FullScanCount;

            Assert.True(ticks > 0, "The monitor loop did not run.");

            // The counters are read without a lock, so the loop may sit between its tick increment
            // and its scan: one outstanding tick is expected, more is a throttle.
            Assert.True(
                ticks - scans <= 1,
                $"The monitor loop ticked {ticks} times but scanned only {scans} times. The scan must "
                + "run on every tick: NOTE_FORK carries no child pid, so the scan is the only way to "
                + "identify a descendant before it escapes its process group and is reparented.");

        }
        finally
        {

            await supervisor!.DisposeAsync();

            if (!child.HasExited)
            {

                child.Kill(entireProcessTree: true);

            }

        }

    }

    /// <summary>
    /// Disposal releases the kqueue descriptor and the unmanaged kevent buffer. The monitor loop
    /// hands that same buffer to <c>kevent(2)</c> on every tick, so the kernel writes into it after
    /// the free unless disposal first proves the loop has stopped. The runner always calls
    /// <c>StopKillAndVerifyAsync</c> before <c>DisposeAsync</c>, which trips the <c>_stopped</c>
    /// short-circuit and leaves disposal with no wait at all — its own bounded wait is best effort
    /// and is skipped entirely on that path. Disposal must therefore wait for the loop itself.
    /// </summary>
    [SkippableFact]
    public async Task Disposal_waits_for_an_in_flight_monitor_tick_before_releasing_the_kqueue_buffer()
    {

        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        using System.Diagnostics.Process child = new();

        child.StartInfo = new ProcessStartInfo("/bin/sleep", "30")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _ = child.Start();

        TaskCompletionSource parked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        MacOsDescendantSupervisor? supervisor = MacOsDescendantSupervisor.TryStart(
            child.Id,
            () =>
            {

                _ = parked.TrySetResult();

                return release.Task;

            });

        Skip.If(supervisor is null, "The supervisor could not attach to the child on this host.");

        Task? disposal = null;

        try
        {

            // The loop is now inside a tick, exactly where it would be when a loaded host delays its
            // resumption past the bounded wait.
            await parked.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Production order: the runner stops the supervisor first, then disposes it.
            _ = await supervisor!.StopKillAndVerifyAsync(
                TimeSpan.FromMilliseconds(50));

            disposal = supervisor.DisposeAsync().AsTask();

            Task first = await Task.WhenAny(
                disposal,
                Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.False(
                ReferenceEquals(first, disposal),
                "DisposeAsync returned while a monitor tick was still in flight. It then frees the "
                + "kevent buffer and closes the kqueue, so the next kevent(2) call in that tick "
                + "writes kernel records into freed heap.");

        }
        finally
        {

            _ = release.TrySetResult();

            if (disposal is not null)
            {

                await disposal.WaitAsync(TimeSpan.FromSeconds(30));

            }
            else
            {

                await supervisor!.DisposeAsync();

            }

            if (!child.HasExited)
            {

                child.Kill(entireProcessTree: true);

            }

        }

    }

}
