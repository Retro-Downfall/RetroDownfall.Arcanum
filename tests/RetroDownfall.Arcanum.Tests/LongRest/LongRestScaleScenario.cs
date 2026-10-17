using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>How far the scenario's clean generation is taken before it is handed over.</summary>
internal enum LongRestScaleCompletion
{
    /// <summary>The generation is published and its sweep begun; nothing has been swept.</summary>
    Open,

    /// <summary>The generation is marked complete, which is the state a sweep ends in, without paying for the sweep.</summary>
    Marked,

    /// <summary>The real sweep runs, one unit for each memory that has nothing to pay for.</summary>
    Swept,
}

/// <summary>
/// An installation of a chosen size, built once and measured through the same store calls a Session turn makes: one completed clean
/// generation over the clean memories, and a protected partition for each of the rest.
/// </summary>
/// <remarks>
/// Every memory is its own - its own words, its own vector, its own summary - so a sweep over the corpus finds nothing to pay for and the
/// only cost left to count is the cost of verification itself, which is what the scale tests are about.
/// </remarks>
internal sealed class LongRestScaleScenario : IAsyncDisposable
{
    internal static readonly Guid CleanOperation = new("00000000-0000-0000-0000-00000000c1ea");

    private LongRestScaleScenario(LongRestSweepHarness harness, LongRestSweepStore sweeps, LongRestDiscoveryGeneration clean, int cleanSources, int partitions)
    {
        H = harness;

        Sweeps = sweeps;

        Clean = clean;

        CleanSources = cleanSources;

        ProtectedPartitions = partitions;
    }

    internal LongRestSweepHarness H { get; }

    internal LongRestSweepStore Sweeps { get; }

    internal LongRestDiscoveryGeneration Clean { get; }

    internal int CleanSources { get; }

    internal int ProtectedPartitions { get; }

    internal static string MemoryId(int number) => new Guid("a0000000-0000-0000-0000-" + number.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)).ToString("D");

    /// <param name="completion">
    /// How far to take the clean generation. A real sweep is one unit for each memory and every unit re-verifies the corpus, so it is
    /// quadratic until the selector split lands, and a corpus too large to sweep inside a test is marked complete instead.
    /// </param>
    internal static async Task<LongRestScaleScenario> CreateAsync(int cleanSources, int protectedPartitions, LongRestScaleCompletion completion)
    {
        LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        h.UseDistinctSummaries();

        for (int number = 1; number <= cleanSources + protectedPartitions; number++)
        {
            await h.SeedDistinctAsync(MemoryId(number), number);

            if (number > cleanSources)
            {
                _ = await h.ProtectAsync(new Guid(MemoryId(number)), GenerationProvenance.CreateExact([Guid.NewGuid()]));
            }
        }

        LongRestSweepStore sweeps = new(h.Saga.Context, TimeProvider.System);

        LongRestDiscoveryGeneration published = await h.BuildAsync(await h.CaptureAsync(operationId: CleanOperation));

        LongRestScaleScenario scenario = new(h, sweeps, published, cleanSources, protectedPartitions);

        LongRestSweep sweep = await scenario.BeginAsync();

        if (completion == LongRestScaleCompletion.Swept)
        {
            for (int page = 0; page < cleanSources + 8 && sweep.Phase != LongRestSweepPhase.Completed; page++)
            {
                Assert.Null(LongRestSweepHarness.Require(await sweeps.ReadNextWorkAsync(sweep, null, CancellationToken.None)));

                sweep = await scenario.BeginAsync();
            }

            Assert.Equal(LongRestSweepPhase.Completed, sweep.Phase);

            await scenario.CompleteOperationAsync();
        }
        else if (completion == LongRestScaleCompletion.Marked)
        {
            await scenario.MarkCompleteAsync();
        }

        return scenario;
    }

    internal async Task<LongRestSweep> BeginAsync() => LongRestSweepHarness.Require(await Sweeps.BeginOrResumeAsync(CleanOperation, Clean.Scope, Clean.Fence.Configuration, CancellationToken.None));

    /// <summary>Marks the published generation and its sweep complete, which is the state the sweep ends in.</summary>
    internal async Task MarkCompleteAsync()
    {
        await H.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 6, Revision = Revision + 1 WHERE OperationId = $operation AND Published = 1;",
            ("$operation", CleanOperation.ToString("N")));

        await H.ExecuteAsync("""
            UPDATE long_rest_sweeps SET PhaseCode = 6, Revision = Revision + 1,
                DiscoveryCursor = (SELECT max(MemoryId) FROM long_rest_discovery_leaves WHERE GenerationId = long_rest_sweeps.GenerationId)
            WHERE OperationId = $operation;
            """, ("$operation", CleanOperation.ToString("N")));

        await CompleteOperationAsync();

        Assert.Equal(CleanSources, (int)(long)(await H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves;"))!);
    }

    private Task CompleteOperationAsync() => H.ExecuteAsync("UPDATE LongRunningOperations SET State = $completed WHERE Id = $operation;",
        ("$completed", (int)LongRunningOperationState.Completed), ("$operation", CleanOperation.ToString("N")));

    /// <summary>One unit of the open sweep - what the hosted worker pays each time it asks for the next work - and what that cost.</summary>
    internal async Task<ScanUnit> RunScanUnitAsync()
    {
        LongRestSweep sweep = await BeginAsync();

        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        Assert.Null(LongRestSweepHarness.Require(await Sweeps.ReadNextWorkAsync(sweep, null, CancellationToken.None)));

        return new(tally.FullLeafReads, tally.MetadataLeafReads, tally.CorpusPasses, tally.AntiJoinPasses, tally.WriterTransactions);
    }

    /// <summary>What one idle Session turn asks of the stores, in the order the turn preparer asks it, and what that cost.</summary>
    internal async Task<IdleTurn> RunIdleTurnAsync()
    {
        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        using SqliteStatementTrace trace = new(H.Connection);

        CanonicalCampaignContext scope = Clean.Fence.CanonicalScope;

        // The clean scope first: its completed sweep answers the request, and nothing is queued.
        Guid? completed = LongRestSweepHarness.Require(await Sweeps.FindCompletedRequestReplayAsync(scope, Clean.Scope, Clean.Recipe, CancellationToken.None));

        Assert.Equal(CleanOperation, completed);

        // Then each protected partition, until the probe has none left to name.
        List<CovenantDigest> partitions = [];

        CovenantDigest? after = null;

        while (true)
        {
            CovenantDigest? next = LongRestSweepHarness.Require(await H.Store.ReadNextProtectedPartitionAsync(scope, after, CancellationToken.None));

            if (next is not { } digest)
            {
                break;
            }

            partitions.Add(digest);

            LongRestSweepScope partition = LongRestSweepScope.Create(new(Clean.Scope.Kind, Clean.Scope.CampaignId), digest).Value;

            // The partition has not been swept here, so there is no completed answer for it: the lookup finds nothing, which is its whole cost.
            Assert.Null(LongRestSweepHarness.Require(await Sweeps.FindCompletedRequestReplayAsync(scope, partition, Clean.Recipe, CancellationToken.None)));

            after = digest;
        }

        Assert.Equal(ProtectedPartitions, partitions.Count);

        return new(trace.ByText(), trace.Count, tally.CorpusPasses, tally.MetadataLeafReads, tally.FullLeafReads, tally.PartitionPasses,
            tally.ReadTransactions, tally.WriterTransactions, tally.AntiJoinPasses, trace.ImmediateTransactions);
    }

    public ValueTask DisposeAsync() => H.DisposeAsync();
}

/// <summary>The verification one unit of an open sweep performed.</summary>
internal sealed record ScanUnit(long FullLeafReads, long MetadataLeafReads, long CorpusPasses, long AntiJoinPasses, long WriterTransactions);

/// <summary>
/// The cost of one idle turn: every statement text with the number of times it ran, and the verification counts. The immediate
/// transactions are what SQLite itself saw begin with the writer lock, which no counter in the stores can under-report.
/// </summary>
internal sealed record IdleTurn(Dictionary<string, int> Statements, int StatementCount, long CorpusPasses, long MetadataLeafReads, long FullLeafReads,
    long PartitionPasses, long ReadTransactions, long WriterTransactions, long AntiJoinPasses, int ImmediateTransactions);
