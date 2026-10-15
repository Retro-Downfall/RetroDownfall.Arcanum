using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// What the verification counters promise the tests that assert on them: exact counts for the flow that asked, nothing for anyone else.
/// </summary>
public sealed class LongRestVerificationCountersTests
{
    [Fact]
    public void Nothing_is_counted_outside_a_measurement_scope()
    {
        LongRestVerificationCounters.FullLeafRead();

        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        Assert.Equal(0, tally.FullLeafReads);
    }

    [Fact]
    public void Each_kind_of_work_is_counted_under_its_own_name()
    {
        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        LongRestVerificationCounters.FullLeafRead();

        LongRestVerificationCounters.MetadataLeafRead();

        LongRestVerificationCounters.MetadataLeafRead();

        LongRestVerificationCounters.CorpusPass();

        LongRestVerificationCounters.AntiJoinPass();

        LongRestVerificationCounters.FtsHitVerification();

        LongRestVerificationCounters.PartitionPass();

        LongRestVerificationCounters.WriterTransaction();

        LongRestVerificationCounters.ReadTransaction();

        LongRestVerificationCounters.ReadTransaction();

        Assert.Equal(
            [1L, 2L, 1L, 1L, 1L, 1L, 1L, 2L],
            [tally.FullLeafReads, tally.MetadataLeafReads, tally.CorpusPasses, tally.AntiJoinPasses, tally.FtsHitVerifications,
                tally.PartitionPasses, tally.WriterTransactions, tally.ReadTransactions]);
    }

    [Fact]
    public void A_scope_inside_a_scope_adds_to_both_and_ending_it_restores_the_outer_one()
    {
        using LongRestVerificationTally outer = LongRestVerificationCounters.Measure();

        LongRestVerificationCounters.CorpusPass();

        using (LongRestVerificationTally inner = LongRestVerificationCounters.Measure())
        {
            LongRestVerificationCounters.CorpusPass();

            Assert.Equal(1, inner.CorpusPasses);
        }

        LongRestVerificationCounters.CorpusPass();

        Assert.Equal(3, outer.CorpusPasses);
    }

    [Fact]
    public async Task Two_flows_measured_at_the_same_time_each_see_only_their_own_work()
    {
        async Task<long> FlowAsync(int reads)
        {
            using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

            for (int index = 0; index < reads; index++)
            {
                LongRestVerificationCounters.FullLeafRead();

                await Task.Yield();
            }

            return tally.FullLeafReads;
        }

        long[] seen = await Task.WhenAll(Task.Run(() => FlowAsync(40)), Task.Run(() => FlowAsync(70)));

        Assert.Equal([40L, 70L], seen);
    }

    [Fact]
    public void Work_done_on_many_threads_inside_one_scope_is_all_counted()
    {
        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        _ = Parallel.For(0, 1000, static _ => LongRestVerificationCounters.MetadataLeafRead());

        Assert.Equal(1000, tally.MetadataLeafReads);
    }
}
