using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class UnseenServantJobTrackerTests
{

    [Fact]
    public void New_tracker_has_no_run_result_or_due_time()
    {

        UnseenServantJobTracker tracker = new();

        UnseenServantJob enabled = CreateJob("Watcher", "patrol", enabled: true);

        UnseenServantJob disabled = CreateJob("Watcher", "patrol", enabled: false);

        Assert.Null(tracker.GetLastRunAt(enabled));

        Assert.Null(tracker.GetLastResult(enabled));

        Assert.Null(tracker.GetNextDueAt(enabled, effectiveIntervalMinutes: 15));

        Assert.Null(tracker.GetNextDueAt(disabled, effectiveIntervalMinutes: 15));

    }

    [Fact]
    public void RecordCompletion_uses_default_results_and_keeps_full_job_keys_distinct()
    {

        UnseenServantJobTracker tracker = new();

        UnseenServantJob success = CreateJob("ab", "c");

        UnseenServantJob failure = CreateJob("a", "bc");

        UnseenServantJob custom = CreateJob("Watcher", "patrol");

        DateTimeOffset before = DateTimeOffset.UtcNow;

        tracker.RecordCompletion(success, success: true, resultSummary: null);

        tracker.RecordCompletion(failure, success: false, resultSummary: null);

        tracker.RecordCompletion(custom, success: false, resultSummary: "Spell returned no changes");

        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.Equal("Success", tracker.GetLastResult(success));

        Assert.Equal("Failed", tracker.GetLastResult(failure));

        Assert.Equal("Spell returned no changes", tracker.GetLastResult(custom));

        DateTimeOffset successRun = Assert.IsType<DateTimeOffset>(tracker.GetLastRunAt(success));

        DateTimeOffset failureRun = Assert.IsType<DateTimeOffset>(tracker.GetLastRunAt(failure));

        Assert.InRange(successRun, before, after);

        Assert.InRange(failureRun, before, after);

        Assert.Equal(successRun.AddMinutes(15), tracker.GetNextDueAt(success, effectiveIntervalMinutes: 15));

        Assert.Null(tracker.GetLastResult(CreateJob("AB", "c")));

    }

    [Fact]
    public async Task HydrateAsync_marks_only_positive_interval_overdue_and_preserves_watermark_times()
    {

        UnseenServantJobTracker tracker = new();

        UnseenServantJob overdueJob = CreateJob("overdue", "patrol");

        UnseenServantJob futureJob = CreateJob("future", "patrol");

        UnseenServantJob noIntervalJob = CreateJob("no-interval", "patrol");

        DateTimeOffset overdueRun = DateTimeOffset.UtcNow.AddHours(-2);

        DateTimeOffset futureRun = DateTimeOffset.UtcNow.AddHours(1);

        DateTimeOffset oldRunWithoutInterval = DateTimeOffset.UtcNow.AddDays(-2);

        await tracker.HydrateAsync(
        [
            new UnseenServantWatermark(
                UnseenServantJobTracker.JobTrackingKey(overdueJob),
                overdueRun,
                EffectiveIntervalMinutes: 30),
            new UnseenServantWatermark(
                UnseenServantJobTracker.JobTrackingKey(futureJob),
                futureRun,
                EffectiveIntervalMinutes: 30),
            new UnseenServantWatermark(
                UnseenServantJobTracker.JobTrackingKey(noIntervalJob),
                oldRunWithoutInterval,
                EffectiveIntervalMinutes: 0),
        ]);

        Assert.Equal(overdueRun, tracker.GetLastRunAt(overdueJob));

        Assert.Equal("Overdue (host was down)", tracker.GetLastResult(overdueJob));

        Assert.Equal(overdueRun.AddMinutes(30), tracker.GetNextDueAt(overdueJob, 30));

        Assert.Equal(futureRun, tracker.GetLastRunAt(futureJob));

        Assert.Equal("Restored from Grimoire", tracker.GetLastResult(futureJob));

        Assert.Equal("Restored from Grimoire", tracker.GetLastResult(noIntervalJob));

        Assert.Equal(oldRunWithoutInterval, tracker.GetLastRunAt(noIntervalJob));

    }

    [Fact]
    public void Disabled_job_has_no_due_time_even_after_completion()
    {

        UnseenServantJobTracker tracker = new();

        UnseenServantJob job = CreateJob("disabled", "patrol", enabled: false);

        tracker.RecordCompletion(job, success: true, resultSummary: "Completed before disable");

        Assert.NotNull(tracker.GetLastRunAt(job));

        Assert.Equal("Completed before disable", tracker.GetLastResult(job));

        Assert.Null(tracker.GetNextDueAt(job, effectiveIntervalMinutes: -1));

    }

    private static UnseenServantJob CreateJob(
        string name,
        string targetSpell,
        bool enabled = true) =>
        new()
        {
            Name = name,
            TargetSpell = targetSpell,
            Enabled = enabled,
        };

}
