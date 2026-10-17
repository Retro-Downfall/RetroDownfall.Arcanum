using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// What the verification counters promise the tests that assert on them: exact counts for the flow that asked, nothing for anyone else.
/// </summary>
public sealed partial class LongRestVerificationCountersTests
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
    public void A_transaction_is_counted_by_the_lock_it_takes()
    {
        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        LongRestVerificationCounters.Transaction(deferred: false);

        LongRestVerificationCounters.Transaction(deferred: true);

        LongRestVerificationCounters.Transaction(deferred: true);

        Assert.Equal([1L, 2L], [tally.WriterTransactions, tally.ReadTransactions]);
    }

    /// <summary>
    /// "Nothing on the idle path takes the writer lock" is only a fact if every transaction a Long Rest store opens is counted, and counted under
    /// the lock it takes. A store that opens a transaction and does not count it would pass the idle-path assertion whatever it did, and one that
    /// counts it as a read when it begins <c>BEGIN IMMEDIATE</c> would say the idle path is quiet while it is not. So the folder is read: each
    /// <c>BeginTransaction</c> must be followed, before the work inside it starts, by the call that counts it, and the call must say what the
    /// provider does. <c>BeginTransaction(deferred: x)</c> takes the lock exactly when <c>x</c> is false, and a call that names no <c>deferred</c>
    /// (the provider's <c>BeginTransactionAsync(cancellationToken)</c> among them) begins <c>BEGIN IMMEDIATE</c>.
    /// </summary>
    [Fact]
    public void Every_transaction_a_Long_Rest_store_opens_is_counted_under_the_lock_it_takes()
    {
        string folder = Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "src", "RetroDownfall.Arcanum.Infrastructure", "Data", "LongRest");

        List<string> wrong = [];

        int opened = 0;

        foreach (string file in Directory.EnumerateFiles(folder, "*.cs").Order(StringComparer.Ordinal))
        {
            string[] lines = File.ReadAllLines(file);

            for (int index = 0; index < lines.Length; index++)
            {
                Match begin = BeginTransaction().Match(lines[index]);

                if (!begin.Success)
                {
                    continue;
                }

                opened++;

                Match named = Deferred().Match(begin.Groups["arguments"].Value);

                string deferred = named.Success ? named.Groups["value"].Value : "false";

                Match counted = Counted().Match(string.Join('\n', lines.Skip(index + 1).Where(static line => line.Trim().Length > 0).Take(2)));

                if (!counted.Success || counted.Groups["value"].Value != deferred)
                {
                    wrong.Add(Path.GetFileName(file) + ":" + (index + 1) + " begins deferred: " + deferred + " and counts " + (counted.Success ? "deferred: " + counted.Groups["value"].Value : "nothing"));
                }
            }
        }

        // The census is real: a folder that opened nothing would pass vacuously.
        Assert.True(opened >= 19, $"Only {opened} transactions were found.");

        Assert.Empty(wrong);
    }

    [GeneratedRegex(@"\.BeginTransaction(?:Async)?\((?<arguments>[^)]*)\)")]
    private static partial Regex BeginTransaction();

    [GeneratedRegex(@"deferred:\s*(?<value>\w+)")]
    private static partial Regex Deferred();

    [GeneratedRegex(@"LongRestVerificationCounters\.Transaction\(deferred:\s*(?<value>\w+)\)")]
    private static partial Regex Counted();

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
