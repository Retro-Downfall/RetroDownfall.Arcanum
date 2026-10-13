using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSweepStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_duplicate_kernel_commits_atomically_and_replays_without_adjudication_attempt(bool protect)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, duplicates: true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        string effects = (string)(await fixture.H.ScalarAsync("SELECT COALESCE(group_concat(hex(WorkDigest) || ':' || PaidCount || ':' || SentCount), '') FROM long_rest_provider_effects;"))!;

        await fixture.H.ExecuteAsync("CREATE TEMP TRIGGER fail_exact_cursor BEFORE UPDATE OF CommittedItemCursor ON long_rest_sweeps BEGIN SELECT RAISE(ABORT, 'cursor failure'); END;");

        Assert.True((await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, fixture.Capability, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

        await fixture.H.ExecuteAsync("DROP TRIGGER fail_exact_cursor;");

        LongRestSweep completed = LongRestSweepHarness.Require(await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, fixture.Capability, CancellationToken.None));

        Assert.NotNull(completed.CommittedItemCursor);

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts WHERE KindCode = 1 AND OutcomeCode = 1;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_adjudications;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE PayloadKindCode = 2;"));

        Assert.Equal(effects, await fixture.H.ScalarAsync("SELECT COALESCE(group_concat(hex(WorkDigest) || ':' || PaidCount || ':' || SentCount), '') FROM long_rest_provider_effects;"));

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        Assert.Equal(completed, LongRestSweepHarness.Require(await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, fixture.Capability, CancellationToken.None)));

        Assert.Equal(changes, await fixture.H.ScalarAsync("SELECT total_changes();"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Exact_duplicate_kernel_refuses_mixed_content_or_stale_work_without_writes(bool protect, bool stale)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, duplicates: stale);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        if (stale)
        {
            await fixture.H.Saga.Store.SetPinAsync(work.Inputs[0].Target.MemoryId, true, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, fixture.Capability, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(changes, await fixture.H.ScalarAsync("SELECT total_changes();"));
    }
}
