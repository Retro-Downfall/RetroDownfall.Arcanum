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
            // Without a kernel event source the scan never backs off, by design; that is the host's
            // condition, not a regression this test is here to catch.
            Skip.IfNot(
                supervisor!.KernelEventsAvailable,
                "The kernel event queue could not watch the child on this host, so the scan never backs off.");

            // Count ticks, not seconds: the loop slows under coverage instrumentation on a loaded suite,
            // and the schedule is defined in ticks, so the ratio is load-independent.
            await WaitUntilAsync(
                () => supervisor!.MonitorTickCount >= 250,
                TimeSpan.FromSeconds(60),
                "The monitor loop did not reach 250 ticks.");

            Skip.If(
                supervisor.WatcherGap,
                "A watcher could not be registered on this host, so the scan never backs off.");

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
    /// <para>
    /// The safety scan is stretched far beyond the test's lifetime, because at the real cadence it (and
    /// the window a scan that finds a new process reopens) resumes scanning with no kqueue event at all,
    /// which is how a test of this wiring once passed with <c>TrackKernelEvents</c> returning false. With
    /// it out of the way the kernel event is the only thing that can end the quiet, so the scan count
    /// rising is proof of the event-to-schedule wiring. The fork is released by the test, not by a timer,
    /// so the quiet baseline is taken before it with no race.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Fork_event_resumes_per_tick_scanning()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        using System.Diagnostics.Process child = new();

        // `read` is a builtin, so the shell does nothing (no fork, no exec) until the test writes a line;
        // then it forks `sleep 30`, which is the event that has to wake the scan.
        child.StartInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-c", "read go; sleep 30 & wait" },
        };

        _ = child.Start();

        MacOsDescendantSupervisor? supervisor = MacOsDescendantSupervisor.TryStart(
            child.Id,
            idleScanEveryTicks: int.MaxValue);

        Skip.If(supervisor is null, "The supervisor could not attach to the child on this host.");

        try
        {
            // The premise is a scan that has backed off, and with no kernel event source or a watcher that
            // could not be registered the scan never does: such a host cannot run this test, which is not
            // the wiring failing.
            Skip.IfNot(
                supervisor!.KernelEventsAvailable,
                "The kernel event queue could not watch the child on this host, so the scan never backs off.");

            // Well past the initial active window (DescendantScanSchedule.ActiveWindowTicks).
            await WaitUntilAsync(
                () => supervisor!.MonitorTickCount >= 200,
                TimeSpan.FromSeconds(60),
                "The monitor loop did not reach 200 ticks.");

            Skip.If(
                supervisor.WatcherGap,
                "A watcher could not be registered on this host, so the scan never backs off.");

            long scansWhileQuiet = supervisor!.FullScanCount;

            await WaitUntilAsync(
                () => supervisor.MonitorTickCount >= 300,
                TimeSpan.FromSeconds(60),
                "The monitor loop did not reach 300 ticks.");

            Assert.True(
                scansWhileQuiet == supervisor.FullScanCount,
                "The quiet baseline is not quiet: with the safety scan stretched and nothing forking, the "
                + $"supervisor still scanned ({scansWhileQuiet} then {supervisor.FullScanCount}), so this test "
                + "could not tell a kernel event from the cadence.");

            await child.StandardInput.WriteLineAsync("go");

            await child.StandardInput.FlushAsync();

            // A full active window of per-tick scans, and nothing else can have started it.
            await WaitUntilAsync(
                () => supervisor.FullScanCount >= scansWhileQuiet + DescendantScanSchedule.ActiveWindowTicks - 5,
                TimeSpan.FromSeconds(30),
                "A fork in the tracked tree did not resume per-tick scanning: the kqueue event never reached "
                + "the scan schedule.");
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
    /// The fault that stops the loop can be the process-table scan itself. Ending the tree must not depend
    /// on that same scan: a kill path that rescans first throws again, the descendants already tracked
    /// survive the root, and nothing is enforcing the ceiling over them any more. Stopping the supervisor
    /// afterwards must likewise neither throw nor leave them running.
    /// </summary>
    [SkippableFact]
    public async Task Scan_fault_with_a_memory_ceiling_still_kills_the_tracked_descendants()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        using System.Diagnostics.Process child = new();

        child.StartInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-c", "sleep 60 & echo $!; wait" },
        };

        _ = child.Start();

        int descendantPid = int.Parse(
            (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)))!.Trim(),
            System.Globalization.CultureInfo.InvariantCulture);

        using ManualResetEventSlim scanFaultArmed = new(false);

        MacOsDescendantSupervisor? supervisor = MacOsDescendantSupervisor.TryStart(
            child.Id,
            memoryLimitBytes: 64L * 1024 * 1024 * 1024,
            processScanHook: () =>
            {
                if (scanFaultArmed.IsSet)
                {
                    throw new InvalidOperationException("injected scan fault");
                }
            });

        Skip.If(supervisor is null, "The supervisor could not attach to the child on this host.");

        try
        {
            await WaitUntilAsync(
                () => supervisor!.TrackedCount >= 2,
                TimeSpan.FromSeconds(30),
                "The supervisor never tracked the backgrounded descendant.");

            scanFaultArmed.Set();

            await WaitUntilAsync(
                () => supervisor!.MonitorFaulted,
                TimeSpan.FromSeconds(30),
                "The injected scan fault never stopped the monitor loop.");

            Assert.True(
                child.WaitForExit(TimeSpan.FromSeconds(30)),
                "The faulted monitor left the root running.");

            Assert.True(
                await WaitForReapedAsync(descendantPid, TimeSpan.FromSeconds(5)),
                $"The faulted monitor killed the root but left tracked descendant {descendantPid} running.");

            // Production order: stop, then dispose. The scan still throws; neither may.
            Assert.True(await supervisor!.StopKillAndVerifyAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            KillIfStillRunning(descendantPid);

            await supervisor!.DisposeAsync();

            KillIfRunning(child);
        }
    }

    /// <summary>
    /// Only a live process can be a hole in the ceiling. A descendant that exits between the identity read
    /// and the footprint read is gone, not unmeasured, and counting it turned every short-lived build step
    /// into a coverage warning.
    /// </summary>
    [SkippableFact]
    public async Task Descendant_that_exits_before_its_footprint_is_read_is_not_counted_as_unreadable()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        using System.Diagnostics.Process child = new();

        child.StartInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-c", "sleep 60 & echo $!; wait; exec sleep 60" },
        };

        _ = child.Start();

        int rootPid = child.Id;

        int descendantPid = int.Parse(
            (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)))!.Trim(),
            System.Globalization.CultureInfo.InvariantCulture);

        using ManualResetEventSlim descendantReadFailed = new(false);

        // The reader stands in for the race: by the time the footprint read fails, the descendant has been
        // killed and reaped by its parent's `wait`, so it no longer exists at all.
        MacOsDescendantSupervisor? supervisor = MacOsDescendantSupervisor.TryStart(
            rootPid,
            memoryLimitBytes: 64L * 1024 * 1024 * 1024,
            footprintReader: pid =>
            {
                if (pid != descendantPid)
                {
                    return 1L;
                }

                KillIfStillRunning(descendantPid);

                _ = WaitForReapedAsync(descendantPid, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

                descendantReadFailed.Set();

                return null;
            });

        Skip.If(supervisor is null, "The supervisor could not attach to the child on this host.");

        try
        {
            Assert.True(
                descendantReadFailed.Wait(TimeSpan.FromSeconds(30)),
                "The descendant's footprint was never read.");

            long ticksAfterRace = supervisor!.MonitorTickCount;

            await WaitUntilAsync(
                () => supervisor.MonitorTickCount >= ticksAfterRace + 10,
                TimeSpan.FromSeconds(30),
                "The monitor loop stopped ticking.");

            Assert.Equal(0, supervisor.UnreadableFootprintCount);
        }
        finally
        {
            KillIfStillRunning(descendantPid);

            await supervisor!.DisposeAsync();

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

    /// <summary>
    /// Waits until <paramref name="processId"/> no longer exists at all — exited and reaped, so not even a
    /// zombie answers for it.
    /// </summary>
    private static async Task<bool> WaitForReapedAsync(int processId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        do
        {
            try
            {
                using System.Diagnostics.Process _ = System.Diagnostics.Process.GetProcessById(processId);
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(10);
        }

        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static void KillIfStillRunning(int processId)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId);

            process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, which is what every caller wants.
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
