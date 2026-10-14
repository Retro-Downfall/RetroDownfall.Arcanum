using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSweepStoreTests
{
    // Operation identities are chosen so that the order the lookup used to take (by operation) puts every candidate that cannot win first
    // and the one that can last, which is the worst case for it; the order it takes now (newest generation first) reaches the winner at once.
    private static readonly Guid Valid = new("ffffffff-ffff-ffff-ffff-ffffffffff01");

    private static readonly Guid ForeignPartition = new("00000000-0000-0000-0002-0000000000a1");

    private static Guid StaleOperation(int number) => new("00000000-0000-0000-0000-" + number.ToString("D12", System.Globalization.CultureInfo.InvariantCulture));

    private static Guid JunkOperation(int number) => new("00000000-0000-0000-0001-" + number.ToString("D12", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// A scope that retains five completed generations: three that the corpus has outgrown, one that belongs to another partition, and the
    /// one that still covers it. The supersession that completion normally performs is switched off, because it is exactly what keeps
    /// this shape from forming in a healthy installation, and the lookup must still be right when something has left it behind.
    /// </summary>
    private static async Task<(LongRestSweepHarness H, LongRestSweepStore Sweeps, LongRestDiscoveryGeneration Valid)> RetainFiveAsync(int staleCount = 3)
    {
        LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        h.UseDistinctSummaries();

        for (int number = 1; number <= 3; number++)
        {
            await h.SeedDistinctAsync(new Guid("10000000-0000-0000-0000-" + number.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)).ToString("D"), number);
        }

        await h.ExecuteAsync("DROP TRIGGER long_rest_generation_complete;");

        // Completion still ends the generation's search index; only the supersession of earlier generations is left out.
        await h.ExecuteAsync("""
            CREATE TRIGGER long_rest_generation_complete AFTER UPDATE OF PhaseCode ON long_rest_discovery_generations
            WHEN NEW.PhaseCode = 6 AND OLD.PhaseCode <> 6
            BEGIN
                DELETE FROM long_rest_clean_fts WHERE GenerationId = NEW.GenerationId;
                DELETE FROM long_rest_protected_fts WHERE GenerationId = NEW.GenerationId;
            END;
            """);

        LongRestSweepStore sweeps = new(h.Saga.Context, TimeProvider.System);

        for (int number = 1; number <= staleCount; number++)
        {
            _ = await CompleteCleanAsync(h, sweeps, StaleOperation(number));
        }

        // The other partition's generation is a copy of a completed clean one, re-keyed to a partition no source carries.
        await CloneCompletedAsync(h, StaleOperation(1), ForeignPartition, Enumerable.Repeat((byte)0xAB, 32).ToArray(), watermark: null);

        // New debt: an eligible source that none of the retained generations has seen.
        await h.SeedDistinctAsync("10000000-0000-0000-0000-000000000004", 4);

        LongRestDiscoveryGeneration valid = await CompleteCleanAsync(h, sweeps, Valid);

        await h.ExecuteAsync("UPDATE LongRunningOperations SET State = $completed WHERE Kind = 'long-rest-sweep';", ("$completed", (int)LongRunningOperationState.Completed));

        return (h, sweeps, valid);
    }

    private static async Task<LongRestDiscoveryGeneration> CompleteCleanAsync(LongRestSweepHarness h, LongRestSweepStore sweeps, Guid operation)
    {
        LongRestDiscoveryGeneration published = await h.BuildAsync(await h.CaptureAsync(operationId: operation));

        LongRestSweep sweep = LongRestSweepHarness.Require(await sweeps.BeginOrResumeAsync(operation, published.Scope, published.Fence.Configuration, CancellationToken.None));

        for (int page = 0; page < 8 && sweep.Phase != LongRestSweepPhase.Completed; page++)
        {
            Assert.Null(LongRestSweepHarness.Require(await sweeps.ReadNextWorkAsync(sweep, null, CancellationToken.None)));

            sweep = LongRestSweepHarness.Require(await sweeps.BeginOrResumeAsync(operation, published.Scope, published.Fence.Configuration, CancellationToken.None));
        }

        Assert.Equal(LongRestSweepPhase.Completed, sweep.Phase);

        return published;
    }

    /// <summary>Copies one completed sweep and its generation under a new operation, optionally re-keyed to another partition and watermark.</summary>
    private static async Task CloneCompletedAsync(LongRestSweepHarness h, Guid source, Guid clone, byte[]? partition, long? watermark)
    {
        Guid generation = Guid.NewGuid();

        (string Name, object? Value)[] values =
        [
            ("$source", source.ToString("N")),
            ("$clone", clone.ToString("N")),
            ("$generation", generation.ToString("D").ToUpperInvariant()),
            ("$partition", partition),
            ("$watermark", watermark),
        ];

        await h.ExecuteAsync("""
            INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary)
            SELECT $clone, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary FROM LongRunningOperations WHERE Id = $source;
            """, values);

        await h.ExecuteAsync("""
            INSERT INTO long_rest_discovery_generations (GenerationId, OperationId, ScopeKindCode, CampaignId, PartitionDigest, InsertionHighWatermark,
                FenceJson, RecipeJson, RecipeDigest, PhaseCode, Revision, CaptureCursor, FoldLayer, FoldGroup, Published, CreatedAtUtc)
            SELECT $generation, $clone, ScopeKindCode, CampaignId, COALESCE($partition, PartitionDigest), COALESCE($watermark, InsertionHighWatermark),
                FenceJson, RecipeJson, RecipeDigest, PhaseCode, Revision, CaptureCursor, FoldLayer, FoldGroup, Published, CreatedAtUtc
            FROM long_rest_discovery_generations WHERE OperationId = $source;
            """, values);

        await h.ExecuteAsync("""
            INSERT INTO long_rest_sweeps (OperationId, GenerationId, ScopeKindCode, CampaignId, PartitionDigest, ConfigurationJson, PhaseCode, Revision,
                CommittedItemCursor, DiscoveryCursor, CommittedWorkDigest, ReadmitCount, ParkedAt, UpdatedAtUtc)
            SELECT $clone, $generation, ScopeKindCode, CampaignId, COALESCE($partition, PartitionDigest), ConfigurationJson, PhaseCode, Revision,
                CommittedItemCursor, DiscoveryCursor, CommittedWorkDigest, ReadmitCount, ParkedAt, UpdatedAtUtc
            FROM long_rest_sweeps WHERE OperationId = $source;
            """, values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_lookup_among_five_retained_generations_pays_at_most_one_corpus_pass(bool request)
    {
        (LongRestSweepHarness h, LongRestSweepStore sweeps, LongRestDiscoveryGeneration valid) = await RetainFiveAsync();

        await using LongRestSweepHarness harness = h;

        Assert.Equal(5L, await h.ScalarAsync("SELECT count(*) FROM long_rest_sweeps WHERE PhaseCode = 6;"));

        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        Guid? found = LongRestSweepHarness.Require(request
            ? await sweeps.FindCompletedRequestReplayAsync(valid.Fence.CanonicalScope, valid.Scope, valid.Recipe, CancellationToken.None)
            : await sweeps.FindCompletedReplayAsync(valid.Fence.CanonicalScope, valid.Scope, valid.Recipe, CancellationToken.None));

        Assert.Equal(Valid, found);

        // The winner is the newest generation, so it is the first to be proved, and the one pass it costs is the only one: the generations
        // that cannot win are never reached, and the one that belongs to another partition is not even read.
        Assert.Equal(1, tally.CorpusPasses);

        Assert.Equal(4, tally.MetadataLeafReads);

        Assert.Equal(0, tally.FullLeafReads);

        Assert.Equal(1, tally.ReadTransactions);

        Assert.Equal(0, tally.WriterTransactions);
    }

    [Fact]
    public async Task Completed_lookup_refuses_every_stale_generation_before_it_reads_a_single_leaf()
    {
        (LongRestSweepHarness h, LongRestSweepStore sweeps, LongRestDiscoveryGeneration valid) = await RetainFiveAsync();

        await using LongRestSweepHarness harness = h;

        // The winner stops covering the corpus too: now every retained generation has new debt, and none may win.
        await h.SeedDistinctAsync("10000000-0000-0000-0000-000000000005", 5);

        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        Assert.Null(LongRestSweepHarness.Require(await sweeps.FindCompletedReplayAsync(valid.Fence.CanonicalScope, valid.Scope, valid.Recipe, CancellationToken.None)));

        // Four generations of this partition, four set-based refusals, and not one leaf read to reach them: the corpus pass is the expensive
        // proof, and a generation that fails the cheap one never pays for it.
        Assert.Equal(0, tally.CorpusPasses);

        Assert.Equal(0, tally.MetadataLeafReads);

        Assert.Equal(4, tally.AntiJoinPasses);
    }

    [Fact]
    public async Task Completed_lookup_pushes_the_partition_into_the_query_and_keeps_the_whole_scope_form_conservative()
    {
        (LongRestSweepHarness h, LongRestSweepStore sweeps, LongRestDiscoveryGeneration valid) = await RetainFiveAsync();

        await using LongRestSweepHarness harness = h;

        // A protected partition has no completed generation here, so asking for it must not be answered by the clean one.
        LongRestSweepScope other = LongRestSweepScope.Create(new(valid.Scope.Kind, valid.Scope.CampaignId), new CovenantDigest(Enumerable.Repeat((byte)0xCD, 32).ToArray())).Value;

        using (LongRestVerificationTally tally = LongRestVerificationCounters.Measure())
        {
            Assert.Null(LongRestSweepHarness.Require(await sweeps.FindCompletedReplayAsync(valid.Fence.CanonicalScope, other, valid.Recipe, CancellationToken.None)));

            Assert.Equal(0, tally.AntiJoinPasses);
        }

        // The partition the clone carries is found by its own digest, and by nothing else: the clean generations never answer for it.
        LongRestSweepScope cloned = LongRestSweepScope.Create(new(valid.Scope.Kind, valid.Scope.CampaignId), new CovenantDigest(Enumerable.Repeat((byte)0xAB, 32).ToArray())).Value;

        Assert.Equal(ForeignPartition, LongRestSweepHarness.Require(await sweeps.FindCompletedReplayAsync(valid.Fence.CanonicalScope, cloned, valid.Recipe, CancellationToken.None)));

        // And the clean lookup, which names no digest, never lands on it.
        Assert.Equal(Valid, LongRestSweepHarness.Require(await sweeps.FindCompletedReplayAsync(valid.Fence.CanonicalScope, valid.Scope, valid.Recipe, CancellationToken.None)));
    }

    [Fact]
    public async Task Completed_lookup_pages_past_a_hundred_and_twenty_eight_newer_candidates_rather_than_missing_the_one_that_covers_the_corpus()
    {
        (LongRestSweepHarness h, LongRestSweepStore sweeps, LongRestDiscoveryGeneration valid) = await RetainFiveAsync(staleCount: 1);

        await using LongRestSweepHarness harness = h;

        // A hundred and thirty copies of the stale generation, each newer than the winner and each unreadable as a generation because its
        // watermark no longer matches its fence. A cap on the candidates examined would stop inside them and report no completed sweep, and
        // no completed sweep is a paid re-sweep of the whole corpus.
        for (int number = 1; number <= 130; number++)
        {
            await CloneCompletedAsync(h, StaleOperation(1), JunkOperation(number), partition: null, watermark: 1_000_000 + number);
        }

        Assert.Equal(133L, await h.ScalarAsync("SELECT count(*) FROM long_rest_sweeps WHERE PhaseCode = 6;"));

        Guid? found = LongRestSweepHarness.Require(await sweeps.FindCompletedReplayAsync(valid.Fence.CanonicalScope, valid.Scope, valid.Recipe, CancellationToken.None));

        Assert.Equal(Valid, found);
    }
}
