using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Cohort_discovery_advances_empty_phases_and_retains_open_work_after_feature_disable()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var (owner, _, _, cohorts, cohort, _) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 0);

        f.Settings.CurrentValue.Features.Guild = false;

        var first = await cohorts.ReadWorkPageAsync(owner, null, default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Contains(cohort.Id, first.Value.CohortIds);

        Assert.Equal(2, first.Value.NextAfter!.Phase);

        var second = await cohorts.ReadWorkPageAsync(owner, first.Value.NextAfter, default);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Empty(second.Value.CohortIds);

        Assert.Equal(3, second.Value.NextAfter!.Phase);

        var third = await cohorts.ReadWorkPageAsync(owner, second.Value.NextAfter, default);

        Assert.True(third.IsSuccess, third.Error.Message);

        Assert.Equal(4, third.Value.NextAfter!.Phase);

        var fourth = await cohorts.ReadWorkPageAsync(owner, third.Value.NextAfter, default);

        Assert.True(fourth.IsSuccess, fourth.Error.Message);

        Assert.Null(fourth.Value.NextAfter);
    }
}
