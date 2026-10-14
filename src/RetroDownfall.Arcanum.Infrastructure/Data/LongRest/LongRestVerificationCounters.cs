namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>
/// What the Long Rest verified, counted so that a regression to quadratic work is a number a test can assert instead of a slow install.
/// </summary>
/// <remarks>
/// Nothing counts unless a test has opened a <see cref="Measure"/> scope, and a scope sees only the work of its own async flow, so a
/// count is exact even while other tests drive the same stores in parallel. A scope also adds to the scope that encloses it. The
/// counters are complexity facts, never timings: they are what a scale test asserts, and wall time is only ever a diagnostic.
/// </remarks>
internal static class LongRestVerificationCounters
{
    private static readonly AsyncLocal<LongRestVerificationTally?> Current = new();

    /// <summary>Starts counting in the current async flow until the returned scope is disposed.</summary>
    internal static LongRestVerificationTally Measure()
    {
        LongRestVerificationTally tally = new(Current.Value);

        Current.Value = tally;

        return tally;
    }

    /// <summary>Ends a scope: the enclosing one, if any, becomes current again.</summary>
    internal static void Release(LongRestVerificationTally tally, LongRestVerificationTally? parent)
    {
        if (ReferenceEquals(Current.Value, tally))
        {
            Current.Value = parent;
        }
    }

    /// <summary>One <c>ReadFrozenLeafAsync</c>: the leaf's text, vector and label verified in full.</summary>
    internal static void FullLeafRead() => Current.Value?.Add(LongRestVerificationMetric.FullLeafRead);

    /// <summary>One <c>ReadFrozenMetadataLeafAsync</c>: a leaf's snapshot and label re-proved without its text or vector.</summary>
    internal static void MetadataLeafRead() => Current.Value?.Add(LongRestVerificationMetric.MetadataLeafRead);

    /// <summary>One pass over every leaf of a generation, whether it re-reads them in full or by metadata.</summary>
    internal static void CorpusPass() => Current.Value?.Add(LongRestVerificationMetric.CorpusPass);

    /// <summary>One set-based statement that joins a generation or the eligible sources against the live corpus.</summary>
    internal static void AntiJoinPass() => Current.Value?.Add(LongRestVerificationMetric.AntiJoinPass);

    /// <summary>One rotating window of leaves re-proved in full by a step, which is all of a step's corpus work.</summary>
    internal static void ScrubWindow() => Current.Value?.Add(LongRestVerificationMetric.ScrubWindow);

    /// <summary>One lexical-lane hit read back in full to prove it eligible.</summary>
    internal static void FtsHitVerification() => Current.Value?.Add(LongRestVerificationMetric.FtsHitVerification);

    /// <summary>One build of the protected partition projection.</summary>
    internal static void PartitionPass() => Current.Value?.Add(LongRestVerificationMetric.PartitionPass);

    /// <summary>One transaction that took the writer lock up front.</summary>
    internal static void WriterTransaction() => Current.Value?.Add(LongRestVerificationMetric.WriterTransaction);

    /// <summary>One transaction that only reads, so it takes no writer lock.</summary>
    internal static void ReadTransaction() => Current.Value?.Add(LongRestVerificationMetric.ReadTransaction);
}

/// <summary>The counted facts; <see cref="ReadTransaction"/> stays last because it sizes a tally.</summary>
internal enum LongRestVerificationMetric
{
    FullLeafRead,
    MetadataLeafRead,
    CorpusPass,
    AntiJoinPass,
    ScrubWindow,
    FtsHitVerification,
    PartitionPass,
    WriterTransaction,
    ReadTransaction,
}

/// <summary>One measurement scope: its counts so far, and the scope it restores when disposed.</summary>
internal sealed class LongRestVerificationTally : IDisposable
{
    private readonly long[] _counts = new long[(int)LongRestVerificationMetric.ReadTransaction + 1];

    private readonly LongRestVerificationTally? _parent;

    internal LongRestVerificationTally(LongRestVerificationTally? parent) => _parent = parent;

    internal long FullLeafReads => Read(LongRestVerificationMetric.FullLeafRead);

    internal long MetadataLeafReads => Read(LongRestVerificationMetric.MetadataLeafRead);

    internal long CorpusPasses => Read(LongRestVerificationMetric.CorpusPass);

    internal long AntiJoinPasses => Read(LongRestVerificationMetric.AntiJoinPass);

    internal long ScrubWindows => Read(LongRestVerificationMetric.ScrubWindow);

    internal long FtsHitVerifications => Read(LongRestVerificationMetric.FtsHitVerification);

    internal long PartitionPasses => Read(LongRestVerificationMetric.PartitionPass);

    internal long WriterTransactions => Read(LongRestVerificationMetric.WriterTransaction);

    internal long ReadTransactions => Read(LongRestVerificationMetric.ReadTransaction);

    internal void Add(LongRestVerificationMetric metric)
    {
        for (LongRestVerificationTally? tally = this; tally is not null; tally = tally._parent)
        {
            _ = Interlocked.Increment(ref tally._counts[(int)metric]);
        }
    }

    public void Dispose() => LongRestVerificationCounters.Release(this, _parent);

    private long Read(LongRestVerificationMetric metric) => Interlocked.Read(ref _counts[(int)metric]);
}
