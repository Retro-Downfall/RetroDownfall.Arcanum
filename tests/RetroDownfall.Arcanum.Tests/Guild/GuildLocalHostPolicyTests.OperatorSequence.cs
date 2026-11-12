using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Quest_admission_uses_global_watermark_after_a_cohort_is_created()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, _, _, cohort, _) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 0);

        long watermark = await f.ScalarAsync("SELECT seq FROM sqlite_sequence WHERE name='guild_operator_sequences'");

        var next = await f.IssueAsync(owner, f.Request with { IdempotencyKey = Guid.NewGuid() });

        Assert.True(next.IsSuccess, next.Error.Message);

        Assert.True(next.Value.Quest.IssuedSequence > watermark);

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_operator_sequences WHERE KindCode=1 AND ObjectId='{cohort.Id:D}' COLLATE NOCASE"));
    }
}
