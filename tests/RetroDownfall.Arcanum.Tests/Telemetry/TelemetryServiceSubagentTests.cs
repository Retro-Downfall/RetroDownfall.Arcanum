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
}
