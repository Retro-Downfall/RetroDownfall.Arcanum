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

        var after = first.Value.NextAfter;

        for (int phase = 2; phase <= 7; phase++)
        {
            Assert.NotNull(after);

            Assert.Equal(phase, after.Phase);

            Assert.Null(after.LastId);

            Assert.Null(after.LastFrontierId);

            var page = await cohorts.ReadWorkPageAsync(owner, after, default);

            Assert.True(page.IsSuccess, page.Error.Message);

            Assert.Empty(page.Value.CohortIds);

            after = page.Value.NextAfter;
        }

        Assert.Null(after);
    }
}
