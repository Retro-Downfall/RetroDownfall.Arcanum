using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Tests.Telemetry;

public sealed class TelemetryServiceSubagentTests
{
    [Fact]
    public void RecordSubagentRun_RollsUpOneUnifiedSnapshot()
    {
        using TelemetryService telemetry = new();
        List<TelemetrySnapshot> updates = [];
        telemetry.SnapshotUpdated += (_, snapshot) => updates.Add(snapshot);

        telemetry.RecordSubagentRun(
            new SubagentTelemetryEvent(
                Tokens: 750,
                CostUsd: 0.02m,
                Latency: TimeSpan.FromSeconds(2),
                Outcome: SubagentRunOutcome.Completed));

        TelemetrySnapshot snapshot = Assert.Single(updates);
        Assert.Equal(1, snapshot.Subagents.Runs);
        Assert.Equal(1, snapshot.Subagents.Completed);
        Assert.Equal(0, snapshot.Subagents.Failed);
        Assert.Equal(750, snapshot.Subagents.Tokens);
        Assert.Equal(0.02m, snapshot.Subagents.CostUsd);
        Assert.Equal(TimeSpan.FromSeconds(2), snapshot.Subagents.CumulativeLatency);
    }

    [Fact]
    public void Instrument_the_service_does_not_roll_up_publishes_no_snapshot()
    {

        // The MeterListener attaches to the process-wide ArcanumMetrics meter, so it observes every
        // instrument any other component (or any test running in parallel) records. An instrument
        // this service does not aggregate must not publish an identical, unchanged snapshot.
        using TelemetryService telemetry = new();

        List<TelemetrySnapshot> updates = [];

        telemetry.SnapshotUpdated += (_, snapshot) => updates.Add(snapshot);

        using System.Diagnostics.Metrics.Meter meter = new(ArcanumMetrics.Meter.Name);

        System.Diagnostics.Metrics.Counter<long> unrelated =
            meter.CreateCounter<long>("arcanum_tests_unrelated_instrument_total");

        unrelated.Add(7);

        System.Diagnostics.Metrics.Histogram<double> unrelatedDouble =
            meter.CreateHistogram<double>("arcanum_tests_unrelated_duration_seconds");

        unrelatedDouble.Record(1.5);

        Assert.Empty(updates);
    }
}
