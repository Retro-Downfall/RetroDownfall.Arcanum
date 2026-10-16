using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    // The anchors a scan skips without a request are the ones a consolidation already took: a pinned, retired or suppressed memory is never
    // captured, so only the sweep's own earlier decisions can leave a leaf behind that is no longer an anchor. Nine families of sixteen exact
    // duplicates are committed locally by their first member, which leaves 135 skipped anchors ahead of the cursor and nothing paid.
    [Fact]
    public async Task Sparse_no_match_discovery_yields_persistent_pages_without_fabricating_paid_coordinates()
    {
        await using MaintenanceFixture f = await PrepareSparseDiscoveryAsync(twoEligible: false);

        LongRestSweep before = await ReadScanSweepAsync(f);

        Assert.NotNull(before.CommittedItemCursor);

        Assert.NotNull(before.CommittedWorkDigest);

        int sends = f.Handler.SendCount;

        string pageEnd = Assert.IsType<string>(await f.H.ScalarAsync("SELECT MemoryId FROM long_rest_discovery_leaves WHERE MemoryId > (SELECT DiscoveryCursor FROM long_rest_sweeps) ORDER BY MemoryId LIMIT 1 OFFSET 127;"));

        LongRestSweepProgress yielded = LongRestSweepHarness.Require(await f.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        // This behavioral assertion fails before any reference to the proposed private column.
        Assert.Equal(LongRestSweepPhase.Discover, yielded.Phase);

        Assert.True(yielded.Revision > before.Revision);

        // The scan page moves the discovery cursor and nothing else: the committed coordinates are those of the last real unit.
        Assert.Equal(before.CommittedItemCursor, yielded.CommittedItemCursor);

        Assert.Equal(pageEnd, await f.H.ScalarAsync("SELECT DiscoveryCursor FROM long_rest_sweeps;"));

        Assert.Equal(before.CommittedWorkDigest!.Value.Bytes, await f.H.ScalarAsync("SELECT CommittedWorkDigest FROM long_rest_sweeps;"));

        Assert.Equal(SparseFamilies, (int)(long)(await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"))!);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 1;"));

        LongRestSweepStore oldStore = new(f.H.Saga.Context, f.Clock);

        Assert.True((await oldStore.ReadNextWorkAsync(before, null, CancellationToken.None)).IsFailure);

        Assert.Equal(pageEnd, await f.H.ScalarAsync("SELECT DiscoveryCursor FROM long_rest_sweeps;"));

        await using var context = f.H.Saga.CreateSiblingContext();

        LongRestSweepStore restarted = new(context, f.Clock);

        LongRestSweep current = LongRestSweepHarness.Require(await restarted.BeginOrResumeAsync(f.Operation, f.Partition,
            f.Epoch.Capture().Stamp, CancellationToken.None));

        Assert.Null(LongRestSweepHarness.Require(await restarted.ReadNextWorkAsync(current, null, CancellationToken.None)));

        current = LongRestSweepHarness.Require(await restarted.BeginOrResumeAsync(f.Operation, f.Partition,
            f.Epoch.Capture().Stamp, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Discover, current.Phase);

        Assert.Equal(before.CommittedItemCursor, current.CommittedItemCursor);

        Assert.Equal(before.CommittedWorkDigest, current.CommittedWorkDigest);

        Assert.Equal(await f.H.ScalarAsync("SELECT max(MemoryId) FROM long_rest_discovery_leaves;"),
            await f.H.ScalarAsync("SELECT DiscoveryCursor FROM long_rest_sweeps;"));

        Assert.Null(LongRestSweepHarness.Require(await restarted.ReadNextWorkAsync(current, null, CancellationToken.None)));

        LongRestSweep completed = LongRestSweepHarness.Require(await restarted.BeginOrResumeAsync(f.Operation, f.Partition,
            f.Epoch.Capture().Stamp, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, completed.Phase);

        Assert.Equal(before.CommittedItemCursor, completed.CommittedItemCursor);

        Assert.Equal(before.CommittedWorkDigest, completed.CommittedWorkDigest);

        Assert.Equal(sends, f.Handler.SendCount);
    }

    [Fact]
    public async Task Paid_anchor_then_scan_checkpoint_then_later_paid_anchor_survives_restart_without_repeating_effects()
    {
        await using MaintenanceFixture f = await PrepareSparseDiscoveryAsync(twoEligible: true);

        LongRestSweepProgress first = LongRestSweepHarness.Require(await f.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Discover, first.Phase);

        LongRestSweep paid = await ReadScanSweepAsync(f);

        Assert.NotNull(paid.CommittedItemCursor);

        Assert.NotNull(paid.CommittedWorkDigest);

        // The first paid anchor is the first pair's first member, after the nine heads whose exact duplicates were committed locally.
        Assert.Equal(SparseId(SparseFamilies).ToString("D"), paid.CommittedItemCursor);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE PayloadKindCode = 2 AND PaidCount = 1 AND StateCode = 4;"));

        int sends = f.Handler.SendCount;

        // The pair's second member names the same two memories, which are already decided: its group is reused, nothing is sent, and the
        // scan moves past it.
        LongRestSweepProgress reused = LongRestSweepHarness.Require(await f.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Discover, reused.Phase);

        Assert.Equal(sends, f.Handler.SendCount);

        Assert.Equal(SparseId(SparseFamilies + 1).ToString("D"), await f.H.ScalarAsync("SELECT DiscoveryCursor FROM long_rest_sweeps;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE PayloadKindCode = 2 AND PaidCount = 1 AND StateCode = 4;"));

        LongRestSweepProgress page = LongRestSweepHarness.Require(await f.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        // Baseline returns the later paid anchor in this call; it cannot silently satisfy this yield.
        Assert.Equal(sends, f.Handler.SendCount);

        Assert.Equal(LongRestSweepPhase.Discover, page.Phase);

        LongRestSweep scanned = await ReadScanSweepAsync(f);

        Assert.True(scanned.Revision > paid.Revision);

        Assert.Equal(paid.CommittedItemCursor, scanned.CommittedItemCursor);

        Assert.Equal(paid.CommittedWorkDigest, scanned.CommittedWorkDigest);

        Assert.Equal(await f.H.ScalarAsync($"SELECT MemoryId FROM long_rest_discovery_leaves WHERE MemoryId > '{SparseId(SparseFamilies + 1):D}' ORDER BY MemoryId LIMIT 1 OFFSET 127;"),
            await f.H.ScalarAsync("SELECT DiscoveryCursor FROM long_rest_sweeps;"));

        LongRestSweepCheckpoint checkpoint = new(1, f.Operation, f.Partition.Kind, f.Partition.CampaignId,
            scanned.GenerationId, scanned.Phase, scanned.Revision, scanned.CommittedItemCursor, scanned.CommittedWorkDigest);

        Assert.True(await f.Operations.SaveCheckpointAsync(f.Operation, "fixture-worker", 1, 1,
            LongRestSweepHarness.Require(LongRestSweepCheckpointCodec.Encode(checkpoint)), null, "scan yield committed", f.Clock.GetUtcNow()));

        LongRestSweepStore staging = new(f.H.Saga.Context, f.Clock);

        LongRestSweepWork pending = LongRestSweepHarness.Require(await staging.ReadNextWorkAsync(scanned, null, CancellationToken.None))!;

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 1;"));

        await using (var stagingContext = f.H.Saga.CreateSiblingContext())
        {
            LongRestSweepStore pendingRestart = new(stagingContext, f.Clock);

            LongRestSweepWork resumedPending = LongRestSweepHarness.Require(await pendingRestart.ReadNextWorkAsync(scanned, null, CancellationToken.None))!;

            Assert.Equal(pending.Identity, resumedPending.Identity);
        }

        Assert.Equal(sends, f.Handler.SendCount);

        LongRestSweepHarness.Require(await f.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        LongRestSweep later = await ReadScanSweepAsync(f);

        // The later paid anchor is the second pair's first member, one before the last leaf.
        Assert.Equal(SparseId(SparseTotal(twoEligible: true) - 2).ToString("D"), later.CommittedItemCursor);

        Assert.NotEqual(scanned.CommittedWorkDigest, later.CommittedWorkDigest);

        Assert.True(checkpoint.Revision < later.Revision);

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE PayloadKindCode = 2 AND PaidCount = 1 AND StateCode = 4;"));

        Assert.Equal((long)SparseFamilies + 2, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 2;"));

        await using var context = f.H.Saga.CreateSiblingContext();

        await context.Database.OpenConnectionAsync();

        LongRestDiscoveryGeneration generation = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        Assert.True((await LongRestLiveFenceReader.ReadWorkerWithinAsync((SqliteConnection)context.Database.GetDbConnection(), null,
            generation, "fixture-worker", f.Clock.GetUtcNow(), CancellationToken.None)).IsSuccess);

        object effects = (await f.H.ScalarAsync(CleanCheckpointPhysicalEffectSnapshotSql))!;

        object bills = (await f.H.ScalarAsync(CleanBillingSnapshotSql))!;

        sends = f.Handler.SendCount;

        DateTimeOffset crashed = f.Clock.GetUtcNow().AddMinutes(-10);

        Assert.True(await f.Operations.RenewLeaseAsync(f.Operation, "fixture-worker", crashed, crashed.AddMinutes(1)));

        LongRestSweepProgress recovered = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, recovered.Phase);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Equal(sends, f.Handler.SendCount);

        Assert.Equal(effects, await f.H.ScalarAsync(CleanCheckpointPhysicalEffectSnapshotSql));

        Assert.Equal(bills, await f.H.ScalarAsync(CleanBillingSnapshotSql));

        string last = Assert.IsType<string>(await f.H.ScalarAsync("SELECT max(MemoryId) FROM long_rest_discovery_leaves;"));

        _ = await f.H.Saga.Store.SetPinAsync(last, true, f.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(7L, await f.H.ScalarAsync("SELECT PhaseCode FROM long_rest_sweeps;"));

        LongRestSweepStore invalidated = new(f.H.Saga.Context, f.Clock);

        Assert.True((await invalidated.ReadNextWorkAsync(later, null, CancellationToken.None)).IsFailure);

        Assert.Equal(sends, f.Handler.SendCount);
    }

    [Fact]
    public async Task Cancellation_after_scan_write_rolls_back_cursor_revision_and_completion_before_yield_publication()
    {
        await using MaintenanceFixture f = await PrepareSparseDiscoveryAsync(twoEligible: false);

        LongRestSweep before = await ReadScanSweepAsync(f);

        object snapshot = (await f.H.ScalarAsync(DiscoveryScanInvariantSnapshotSql))!;

        using CancellationTokenSource cancellation = new();

        f.H.Connection.CreateFunction("fixture_cancel_scan", () => { cancellation.Cancel(); return 0; });

        await f.H.ExecuteAsync("CREATE TEMP TRIGGER fixture_cancel_scan_write AFTER UPDATE OF Revision ON long_rest_sweeps WHEN NEW.Revision > OLD.Revision BEGIN SELECT fixture_cancel_scan(); END;");

        try
        {
            try
            {
                var refused = await f.ProcessAsync(f.Operation, "fixture-worker", null, cancellation.Token);

                Assert.True(refused.IsFailure);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
        finally
        {
            await f.H.ExecuteAsync("DROP TRIGGER fixture_cancel_scan_write;");
        }

        Assert.True(cancellation.IsCancellationRequested);

        Assert.Equal(snapshot, await f.H.ScalarAsync(DiscoveryScanInvariantSnapshotSql));

        LongRestSweep unchanged = await ReadScanSweepAsync(f);

        Assert.Equal(before, unchanged);

        LongRestSweepProgress retried = LongRestSweepHarness.Require(await f.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        // Baseline drains the corpus and completes here, so RED is a behavior failure, not missing SQL.
        Assert.Equal(LongRestSweepPhase.Discover, retried.Phase);

        Assert.Equal(await f.H.ScalarAsync($"SELECT MemoryId FROM long_rest_discovery_leaves WHERE MemoryId > '{SparseId(SparseFamilies - 1):D}' ORDER BY MemoryId LIMIT 1 OFFSET 127;"),
            await f.H.ScalarAsync("SELECT DiscoveryCursor FROM long_rest_sweeps;"));
    }

    // The corpus, in memory-id order: nine family heads; for twoEligible a pair of related memories; 135 followers, fifteen of each family, whose text
    // is their head's exactly; then a second related pair, or for twoEligible false one unrelated memory. Every family is orthogonal to every other
    // and to the pairs, so a head's whole group is its fifteen exact duplicates, which the sweep commits itself.
    private const int SparseFamilies = 9;

    private const int SparseFollowersPerFamily = 15;

    private static Guid SparseId(int index) => new($"00000000-0000-4000-8000-{index + 1:D12}");

    private static int SparseFirstFollower(bool twoEligible) => SparseFamilies + (twoEligible ? 2 : 0);

    private static int SparseTotal(bool twoEligible) => SparseFirstFollower(twoEligible) + SparseFamilies * SparseFollowersPerFamily + (twoEligible ? 2 : 1);

    // The orthogonal axis of a memory: its family, 9 and 10 for the pairs, 11 for the unrelated memory.
    private static int SparseAxis(int index, bool twoEligible)
    {
        int firstFollower = SparseFirstFollower(twoEligible);

        int tail = firstFollower + SparseFamilies * SparseFollowersPerFamily;

        if (index < SparseFamilies) return index;

        if (twoEligible && index < firstFollower) return SparseFamilies;

        if (index < tail) return (index - firstFollower) / SparseFollowersPerFamily;

        return twoEligible ? SparseFamilies + 1 : SparseFamilies + 2;
    }

    private static string SparseText(int index, bool twoEligible)
    {
        int axis = SparseAxis(index, twoEligible);

        int firstFollower = SparseFirstFollower(twoEligible);

        if (axis < SparseFamilies) return $"family{axis} observation record";

        if (!twoEligible) return "solo observation record";

        // The pair's two members share only the pair's word; the other word is each member's own, so no word links one pair to another.
        bool second = (index < firstFollower ? index - SparseFamilies : index - (SparseTotal(twoEligible) - 2)) == 1;

        return $"pair{axis} unique{axis}{(second ? "b" : "a")} observation record";
    }

    private static async Task<MaintenanceFixture> PrepareSparseDiscoveryAsync(bool twoEligible)
    {
        int total = SparseTotal(twoEligible);

        MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: true, protectedSources: false, sourceCount: total,
            sourceText: index => SparseText(index, twoEligible), sourceId: SparseId, sessionless: true);

        try
        {
            for (int index = 0; index < total; index++)
            {
                float[] vector = new float[64];

                vector[SparseAxis(index, twoEligible)] = 1;

                await f.H.SetEmbeddingAsync(SparseId(index).ToString("D"), vector);
            }

            int summaries = 0;

            // A summary embeds to an axis of its own, away from every leaf's, so no summary resembles another and the hierarchy suggests no neighbour.
            f.Handler.VectorFor = body =>
            {
                float[] vector = new float[64];

                vector[16 + int.Parse(System.Text.RegularExpressions.Regex.Match(body, @"Summary (\d+)").Groups[1].Value) % 48] = 1;

                return vector;
            };

            f.Handler.InsideSend = () =>
            {
                f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                    ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"Summary " + ++summaries + "\"}";

                return Task.CompletedTask;
            };

            for (int step = 0; step < 160; step++)
            {
                LongRestDiscoveryGeneration generation = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

                // The nine heads commit their families' exact duplicates, one unit each and no request, and then the cursor sits before the
                // skipped anchors: the 135 followers are suppressed leaves of this generation.
                if (generation.Phase == LongRestSweepPhase.Discover
                    && (long)(await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 2;"))! == SparseFamilies)
                {
                    Assert.True(generation.Published);

                    Assert.Equal((long)total, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves;"));

                    Assert.Equal((long)SparseFamilies * SparseFollowersPerFamily, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

                    Assert.Equal(SparseId(SparseFamilies - 1).ToString("D"), await f.H.ScalarAsync("SELECT DiscoveryCursor FROM long_rest_sweeps;"));

                    Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE PayloadKindCode = 2;"));

                    return f;
                }

                Assert.True(generation.Phase is LongRestSweepPhase.Capture or LongRestSweepPhase.Hierarchy or LongRestSweepPhase.Publish or LongRestSweepPhase.Discover);

                LongRestSweepHarness.Require(await f.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));
            }

            throw new InvalidOperationException("Fixture did not publish its real hierarchy and commit its families within the bounded setup.");
        }
        catch
        {
            await f.DisposeAsync();

            throw;
        }
    }

    private static async Task<LongRestSweep> ReadScanSweepAsync(MaintenanceFixture f) => LongRestSweepHarness.Require(
        await new LongRestSweepStore(f.H.Saga.Context, f.Clock).BeginOrResumeAsync(f.Operation, f.Partition,
            f.Epoch.Capture().Stamp, CancellationToken.None));

    private const string DiscoveryScanInvariantSnapshotSql = """
        SELECT json_array(
            (SELECT json_array(GenerationId, PhaseCode, Revision, Published, FenceJson, RecipeJson) FROM long_rest_discovery_generations),
            (SELECT json_array(OperationId, GenerationId, PhaseCode, Revision, CommittedItemCursor, hex(CommittedWorkDigest), UpdatedAtUtc) FROM long_rest_sweeps),
            (SELECT count(*) FROM long_rest_work_items), (SELECT count(*) FROM long_rest_work_inputs),
            (SELECT count(*) FROM long_rest_receipts), (SELECT count(*) FROM long_rest_adjudications),
            (SELECT count(*) FROM long_rest_provider_effects), (SELECT count(*) FROM BillableOperations));
        """;
}
