using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations;

namespace RetroDownfall.Arcanum.Tests.Api;

// The last reconciliation pass reports what it did, not what is true now. A sweep that pass parked for the Long Rest worker is readmitted and
// finished within a minute, but the pass that parked it stays the last pass until the next one runs, so reading its attention count as a standing
// condition kept DurableOperations Degraded for as long as that took, after the crash was recovered and after an operator's cancel alike. The live
// ledger is what is true now, so it decides when it can be read.
public sealed class ArcanumHealthCheckerDurableOperationsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static LongRunningOperationReconciliationSnapshot Snapshot(int attention, bool startupCompleted = true, bool deferred = false) => new(
        startupCompleted, deferred, Now, new LongRunningOperationReconciliationSummary(1, 1, 0, 0, 0, attention, 0), "pass");

    private static DurableOperationDiagnosticsReport Live(int stale = 0, int awaiting = 0) => new(stale, awaiting, [], awaiting == 0 ? [] : [new("long-rest-sweep", awaiting, "long_rest.interrupted", "guidance")]);

    [Fact]
    public void A_last_pass_that_parked_a_sweep_that_is_no_longer_parked_leaves_the_component_healthy()
    {
        HealthComponentDto component = ArcanumHealthChecker.BuildDurableOperationsComponent(Snapshot(attention: 1), Live());

        Assert.Equal("DurableOperations", component.Name);

        Assert.Equal(HealthStatus.Healthy, component.Status);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void A_stale_lease_or_an_operation_awaiting_repair_in_the_live_ledger_degrades_the_component(int stale, int awaiting)
    {
        HealthComponentDto component = ArcanumHealthChecker.BuildDurableOperationsComponent(Snapshot(attention: 0), Live(stale, awaiting));

        Assert.Equal(HealthStatus.Degraded, component.Status);
    }

    [Fact]
    public void A_ledger_that_cannot_be_read_falls_back_to_what_the_last_pass_reported()
    {
        Assert.Equal(HealthStatus.Degraded, ArcanumHealthChecker.BuildDurableOperationsComponent(Snapshot(attention: 1), null).Status);

        Assert.Equal(HealthStatus.Healthy, ArcanumHealthChecker.BuildDurableOperationsComponent(Snapshot(attention: 0), null).Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void An_unfinished_startup_or_a_deferred_pass_degrades_the_component_whatever_the_ledger_says(bool startupCompleted, bool deferred)
    {
        Assert.Equal(HealthStatus.Degraded,
            ArcanumHealthChecker.BuildDurableOperationsComponent(Snapshot(attention: 0, startupCompleted, deferred), Live()).Status);
    }
}
