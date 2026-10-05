using System.Diagnostics;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

[Collection("ChildProcess")]
public sealed class MacOsDescendantSupervisorTests
{
    /// <summary>
    /// The full process-table scan is expensive (two proc_pidinfo syscalls per live pid), so it runs on
    /// every monitor tick only while the tracked set is changing or a kqueue event says a tracked process
    /// forked or exited; a quiescent child is rescanned on a slow safety cadence instead. NOTE_FORK carries
    /// no child pid, so the scan stays the only way to learn a descendant's identity — which is why a fork
    /// event wakes it on the very next tick rather than leaving it to the slow cadence (the policy itself
    /// is pinned by <see cref="DescendantScanScheduleTests"/>).
    /// </summary>
    [SkippableFact]
    public async Task Quiescent_child_backs_off_full_scans()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        using System.Diagnostics.Process child = StartSleepingChild();

        MacOsDescendantSupervisor? supervisor = MacOsDescendantSupervisor.TryStart(child.Id);

        Skip.If(supervisor is null, "The supervisor could not attach to the child on this host.");

        try
        {
            // Count ticks, not seconds: the loop slows under coverage instrumentation on a loaded suite,
            // and the schedule is defined in ticks, so the ratio is load-independent.
            await WaitUntilAsync(
                () => supervisor!.MonitorTickCount >= 250,
                TimeSpan.FromSeconds(60),
                "The monitor loop did not reach 250 ticks.");

            long ticks = supervisor!.MonitorTickCount;

            long scans = supervisor.FullScanCount;

            // The initial active window plus a safety scan every few dozen ticks, not one scan per tick.
            Assert.True(
                scans <= ticks / 3,
                $"A quiescent child was scanned {scans} times over {ticks} ticks; the scan must back off "
                + "once the tracked set stops changing.");

            Assert.True(scans > 0, "The monitor never scanned.");
        }
        finally
        {
            await supervisor!.DisposeAsync();

            KillIfRunning(child);
        }
    }

    /// <summary>
    /// Backing off must not blind the supervisor to a descendant that appears later: the root's kqueue
    /// fork event has to bring the per-tick scan back so the new process is tracked immediately.
    /// </summary>
    [SkippableFact]
    public async Task Fork_event_resumes_per_tick_scanning()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        using System.Diagnostics.Process child = new();

        // Quiet for long enough to back off, then forks a descendant and keeps running.
        child.StartInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-c", "sleep 3; sleep 30 & wait" },
        };

        _ = child.Start();

        MacOsDescendantSupervisor? supervisor = MacOsDescendantSupervisor.TryStart(child.Id);

        Skip.If(supervisor is null, "The supervisor could not attach to the child on this host.");

        try
        {
            await WaitUntilAsync(
                () => supervisor!.MonitorTickCount >= 200,
                TimeSpan.FromSeconds(60),
                "The monitor loop did not reach 200 ticks.");

            long scansWhileQuiet = supervisor!.FullScanCount;

            // The shell forks `sleep 30` once the first sleep ends; that fork is what must wake the scan.
            await WaitUntilAsync(
                () => supervisor.FullScanCount >= scansWhileQuiet + DescendantScanSchedule.ActiveWindowTicks - 5,
                TimeSpan.FromSeconds(60),
                "A fork in the tracked tree did not resume per-tick scanning.");
        }
        finally
        {
            await supervisor!.DisposeAsync();

            KillIfRunning(child);
        }
    }

    /// <summary>
    /// A fault in the monitor loop is the supervisor's own failure, not an exception for its caller:
    /// the runner stops and disposes the supervisor on every path, and one that rethrew the loop's
    /// exception skipped the rest of the runner's teardown.
    /// </summary>
    [SkippableFact]
    public async Task Monitor_fault_does_not_escape_dispose()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        using System.Diagnostics.Process child = StartSleepingChild();

        MacOsDescendantSupervisor? supervisor = MacOsDescendantSupervisor.TryStart(
            child.Id,
            () => throw new InvalidOperationException("injected monitor fault"));

        Skip.If(supervisor is null, "The supervisor could not attach to the child on this host.");

        try
        {
            await WaitUntilAsync(
                () => supervisor!.MonitorFaulted,
                TimeSpan.FromSeconds(30),
                "The injected fault was never recorded.");

            // Production order: stop, then dispose. Neither may throw.
            _ = await supervisor!.StopKillAndVerifyAsync(TimeSpan.FromMilliseconds(200));

            await supervisor.DisposeAsync();

            Assert.NotNull(supervisor.MonitorFault);
        }
        finally
        {
            KillIfRunning(child);
        }
    }

    /// <summary>
    /// When a memory ceiling is set the monitor loop is the only thing enforcing it, so a faulted loop
    /// must end the tree rather than leave an unmonitored child running.
    /// </summary>
    [SkippableFact]
    public async Task Monitor_fault_with_a_memory_ceiling_kills_the_tree()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        using System.Diagnostics.Process child = StartSleepingChild();

        MacOsDescendantSupervisor? supervisor = MacOsDescendantSupervisor.TryStart(
            child.Id,
            () => throw new InvalidOperationException("injected monitor fault"),
            memoryLimitBytes: 64L * 1024 * 1024 * 1024);

        Skip.If(supervisor is null, "The supervisor could not attach to the child on this host.");

        try
        {
            Assert.True(
                child.WaitForExit(TimeSpan.FromSeconds(30)),
                "The memory monitor faulted and left the child running with nothing enforcing its ceiling.");

            Assert.True(supervisor!.MonitorFaulted);

            await supervisor.DisposeAsync();
        }
        finally
        {
            KillIfRunning(child);
        }
    }

    /// <summary>
    /// A descendant whose footprint cannot be read is left out of the sum. That has to be visible — a
    /// silent drop is a hole in the ceiling nobody can see.
    /// </summary>
    [SkippableFact]
    public async Task Unreadable_descendant_footprint_is_counted_not_dropped_silently()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        using System.Diagnostics.Process child = new();

        child.StartInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-c", "sleep 30 & wait" },
        };

        _ = child.Start();

        int rootPid = child.Id;

        MacOsDescendantSupervisor? supervisor = MacOsDescendantSupervisor.TryStart(
            rootPid,
            memoryLimitBytes: 64L * 1024 * 1024 * 1024,
            footprintReader: pid => pid == rootPid ? 1L : null);

        Skip.If(supervisor is null, "The supervisor could not attach to the child on this host.");

        try
        {
            await WaitUntilAsync(
                () => supervisor!.UnreadableFootprintCount >= 1,
                TimeSpan.FromSeconds(30),
                "A descendant whose footprint could not be read was never reported.");
        }
        finally
        {
            await supervisor!.DisposeAsync();

            KillIfRunning(child);
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

            KillIfRunning(child);
        }
    }

    private static System.Diagnostics.Process StartSleepingChild()
    {
        System.Diagnostics.Process child = new();

        child.StartInfo = new ProcessStartInfo("/bin/sleep", "60")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _ = child.Start();

        return child;
    }

    private static void KillIfRunning(System.Diagnostics.Process child)
    {
        if (!child.HasExited)
        {
            child.Kill(entireProcessTree: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failure)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);

            await Task.Delay(20);
        }
    }
}
