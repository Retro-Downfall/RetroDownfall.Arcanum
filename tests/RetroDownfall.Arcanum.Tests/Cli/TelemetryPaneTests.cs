using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Core.Telemetry;
using Xunit;

namespace RetroDownfall.Arcanum.Cli.Tests;

public class TelemetryPaneTests
{
    [Fact]
    public void Pane_IsNonFocusable()
    {
        TelemetryPane pane = new();
        Assert.False(pane.CanFocus);
    }

    [Fact]
    public void Snapshot_HoldsAggregates()
    {
        TelemetrySnapshot snap = new(
            InputTokens: 100, InputCacheHits: 30, InputCacheMisses: 70,
            OutputTokens: 50, OutputReasoningTokens: 10, OutputStandardTokens: 40,
            EstimatedCostUsd: 0.001m, CumulativeLatency: TimeSpan.FromMilliseconds(120),
            TimeToFirstToken: TimeSpan.FromMilliseconds(45));

        Assert.Equal(100, snap.InputTokens);
        Assert.Equal(30, snap.InputCacheHits);
    }
}
