using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// The tick-based policy that decides when the macOS descendant supervisor pays for a full
/// process-table scan. Pure logic, so it runs on every host and never depends on wall-clock time.
/// </summary>
public sealed class DescendantScanScheduleTests
{
    [Fact]
    public void A_fresh_schedule_scans_every_tick_for_the_active_window_then_backs_off()
    {
        DescendantScanSchedule schedule = new(eventDriven: true);

        for (int tick = 0; tick < DescendantScanSchedule.ActiveWindowTicks; tick++)
        {
            Assert.True(schedule.ShouldScan(kernelEventObserved: false), $"tick {tick} should scan");

            schedule.RecordScan(trackedSetChanged: false);
        }

        // Once the window has passed with nothing changing, only the slow safety cadence remains.
        int scans = 0;

        int ticks = DescendantScanSchedule.IdleScanEveryTicks * 4;

        for (int tick = 0; tick < ticks; tick++)
        {
            if (schedule.ShouldScan(kernelEventObserved: false))
            {
                scans++;

                schedule.RecordScan(trackedSetChanged: false);
            }
        }

        Assert.Equal(4, scans);
    }

    [Fact]
    public void A_kernel_event_brings_per_tick_scanning_back_on_that_tick()
    {
        DescendantScanSchedule schedule = QuietSchedule();

        Assert.False(schedule.ShouldScan(kernelEventObserved: false));

        // The event tick itself scans, and so does every tick of the window that follows.
        for (int tick = 0; tick < DescendantScanSchedule.ActiveWindowTicks; tick++)
        {
            Assert.True(schedule.ShouldScan(kernelEventObserved: tick == 0), $"tick {tick} should scan");

            schedule.RecordScan(trackedSetChanged: false);
        }

        Assert.False(schedule.ShouldScan(kernelEventObserved: false));
    }

    [Fact]
    public void A_scan_that_finds_a_new_process_extends_the_active_window()
    {
        DescendantScanSchedule schedule = QuietSchedule();

        // Idle scan wakes up, finds a descendant nobody was told about.
        for (int tick = 0; tick < DescendantScanSchedule.IdleScanEveryTicks; tick++)
        {
            if (schedule.ShouldScan(kernelEventObserved: false))
            {
                schedule.RecordScan(trackedSetChanged: true);

                break;
            }
        }

        // The tracked set is changing, so the next window of ticks all scan: a grandchild forked between
        // that scan and the new process's watcher registration has no event to announce it.
        for (int tick = 0; tick < DescendantScanSchedule.ActiveWindowTicks; tick++)
        {
            Assert.True(schedule.ShouldScan(kernelEventObserved: false), $"tick {tick} should scan");

            schedule.RecordScan(trackedSetChanged: false);
        }
    }

    [Fact]
    public void Without_an_event_source_every_tick_scans()
    {
        // No kqueue means nothing announces a fork, so the scan is the only detector and cannot back off.
        DescendantScanSchedule schedule = new(eventDriven: false);

        for (int tick = 0; tick < DescendantScanSchedule.ActiveWindowTicks * 10; tick++)
        {
            Assert.True(schedule.ShouldScan(kernelEventObserved: false));

            schedule.RecordScan(trackedSetChanged: false);
        }
    }

    [Fact]
    public void A_watcher_that_could_not_be_registered_disables_the_back_off()
    {
        DescendantScanSchedule schedule = QuietSchedule();

        // A live process whose fork events cannot be heard is a hole in the event-driven design.
        schedule.RequireContinuousScanning();

        for (int tick = 0; tick < DescendantScanSchedule.IdleScanEveryTicks * 4; tick++)
        {
            Assert.True(schedule.ShouldScan(kernelEventObserved: false));

            schedule.RecordScan(trackedSetChanged: false);
        }
    }

    private static DescendantScanSchedule QuietSchedule()
    {
        DescendantScanSchedule schedule = new(eventDriven: true);

        for (int tick = 0; tick < DescendantScanSchedule.ActiveWindowTicks; tick++)
        {
            _ = schedule.ShouldScan(kernelEventObserved: false);

            schedule.RecordScan(trackedSetChanged: false);
        }

        return schedule;
    }
}
