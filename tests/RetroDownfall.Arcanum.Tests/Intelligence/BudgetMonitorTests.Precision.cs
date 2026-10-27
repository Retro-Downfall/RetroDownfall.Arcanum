using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class BudgetMonitorTests
{
    [Fact]
    public async Task Ceiling_refusal_preserves_subcent_limit_and_spend_with_utc_reset_guidance()
    {
        BudgetMonitor monitor = new(
            CreateScopeFactory(new TrackingGrimoireRepository { TodaySpend = 4.9899m }, new FakeBudgetAlertRepository()),
            new FakeCommLinkDispatcher(),
            new TestOptionsMonitor<ArcanumSettings>(Settings(new() { Enabled = true, DailyLimitUsd = 4.9898m })),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync();

        Assert.True(result.IsFailure);

        Assert.Contains("limit of $4.9898", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("current spend: $4.9899", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("00:00 UTC", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("Arcanum:Cost:Budget:DailyLimitUsd", result.Error.Message, StringComparison.Ordinal);
    }
}
