using System.Diagnostics;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

public sealed class MacOsDescendantSupervisorTests
{

    /// <summary>
    /// The supervisor runs for the full lifetime of every macOS tool child (execute_command,
    /// run_spell_script, workspace_check, the SDK resolver). Its full process-table scan issues two
    /// proc_pidinfo syscalls per live PID, so running it on every 10 ms tick cost tens of percent of
    /// a core plus steady GC pressure per concurrent child. Steady-state tracking belongs to the
    /// already-registered kqueue NOTE_FORK/NOTE_EXIT watcher; the scan is only the reconciliation
    /// safety net.
    /// </summary>
    [SkippableFact]
    public async Task Monitor_loop_reconciles_the_process_table_far_less_often_than_it_ticks()
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

            // The full scan must run on every ~10 ms tick. macOS delivers NOTE_FORK without the
            // child's pid, so the scan is the only way to learn a descendant's identity, and it has
            // to win the race against that descendant escaping its process group and being
            // reparented to launchd. Throttling the scan silently widens that window and loses
            // containment, so assert a floor rather than a ceiling: a one-second observation at a
            // 10 ms cadence is ~100 scans, and anything below 25 means the cadence regressed.
            Assert.True(
                supervisor!.FullScanCount >= 25,
                $"The reconciliation scan ran only {supervisor.FullScanCount} times in one second; "
                + "the containment window depends on it running every tick.");

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

}
