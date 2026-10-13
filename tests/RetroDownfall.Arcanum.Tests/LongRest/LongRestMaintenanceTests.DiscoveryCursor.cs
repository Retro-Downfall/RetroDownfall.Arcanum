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
    [Fact]
    public async Task Sparse_no_match_discovery_yields_persistent_pages_without_fabricating_paid_coordinates()
    {
        await using MaintenanceFixture f = await PrepareSparseDiscoveryAsync(twoEligible: false);

        LongRestSweep before = await ReadScanSweepAsync(f);

        int sends = f.Handler.SendCount;

        string pageEnd = Assert.IsType<string>(await f.H.ScalarAsync("SELECT MemoryId FROM long_rest_discovery_leaves ORDER BY MemoryId LIMIT 1 OFFSET 127;"));

        LongRestSweepProgress yielded = LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        // This behavioral assertion fails before any reference to the proposed private column.
        Assert.Equal(LongRestSweepPhase.Discover, yielded.Phase);

        Assert.True(yielded.Revision > before.Revision);

        Assert.Null(yielded.CommittedItemCursor);

        Assert.Equal(pageEnd, await f.H.ScalarAsync("SELECT DiscoveryCursor FROM long_rest_sweeps;"));

        Assert.Equal(DBNull.Value, await f.H.ScalarAsync("SELECT CommittedWorkDigest FROM long_rest_sweeps;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"));

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

        Assert.Null(current.CommittedItemCursor);

        Assert.Null(current.CommittedWorkDigest);

        Assert.Equal(await f.H.ScalarAsync("SELECT max(MemoryId) FROM long_rest_discovery_leaves;"),
            await f.H.ScalarAsync("SELECT DiscoveryCursor FROM long_rest_sweeps;"));

        Assert.Null(LongRestSweepHarness.Require(await restarted.ReadNextWorkAsync(current, null, CancellationToken.None)));

        LongRestSweep completed = LongRestSweepHarness.Require(await restarted.BeginOrResumeAsync(f.Operation, f.Partition,
            f.Epoch.Capture().Stamp, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, completed.Phase);

        Assert.Null(completed.CommittedItemCursor);

        Assert.Null(completed.CommittedWorkDigest);

        Assert.Equal(sends, f.Handler.SendCount);
    }

    [Fact]
    public async Task Paid_anchor_then_scan_checkpoint_then_later_paid_anchor_survives_restart_without_repeating_effects()
    {
        await using MaintenanceFixture f = await PrepareSparseDiscoveryAsync(twoEligible: true);

        LongRestSweepProgress first = LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Discover, first.Phase);

        LongRestSweep paid = await ReadScanSweepAsync(f);

        Assert.NotNull(paid.CommittedItemCursor);

        Assert.NotNull(paid.CommittedWorkDigest);

        Assert.Equal(await f.H.ScalarAsync("SELECT min(MemoryId) FROM long_rest_discovery_leaves;"), paid.CommittedItemCursor);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE PayloadKindCode = 2 AND PaidCount = 1 AND StateCode = 4;"));

        int sends = f.Handler.SendCount;

        LongRestSweepProgress page = LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        // Baseline returns the later paid anchor in this call; it cannot silently satisfy this yield.
        Assert.Equal(sends, f.Handler.SendCount);

        Assert.Equal(LongRestSweepPhase.Discover, page.Phase);

        LongRestSweep scanned = await ReadScanSweepAsync(f);

        Assert.True(scanned.Revision > paid.Revision);

        Assert.Equal(paid.CommittedItemCursor, scanned.CommittedItemCursor);

        Assert.Equal(paid.CommittedWorkDigest, scanned.CommittedWorkDigest);

        Assert.Equal(await f.H.ScalarAsync("SELECT MemoryId FROM long_rest_discovery_leaves ORDER BY MemoryId LIMIT 1 OFFSET 128;"),
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

        LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        LongRestSweep later = await ReadScanSweepAsync(f);

        Assert.Equal(await f.H.ScalarAsync("SELECT max(MemoryId) FROM long_rest_discovery_leaves;"), later.CommittedItemCursor);

        Assert.NotEqual(scanned.CommittedWorkDigest, later.CommittedWorkDigest);

        Assert.True(checkpoint.Revision < later.Revision);

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE PayloadKindCode = 2 AND PaidCount = 1 AND StateCode = 4;"));

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 2;"));

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
                var refused = await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, cancellation.Token);

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

        LongRestSweepProgress retried = LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        // Baseline drains the corpus and completes here, so RED is a behavior failure, not missing SQL.
        Assert.Equal(LongRestSweepPhase.Discover, retried.Phase);

        Assert.Equal(await f.H.ScalarAsync("SELECT MemoryId FROM long_rest_discovery_leaves ORDER BY MemoryId LIMIT 1 OFFSET 127;"),
            await f.H.ScalarAsync("SELECT DiscoveryCursor FROM long_rest_sweeps;"));
    }

    private static async Task<MaintenanceFixture> PrepareSparseDiscoveryAsync(bool twoEligible)
    {
        MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: true, protectedSources: false,
            sourceCount: LongRestDiscoveryLimits.SourcePage + (twoEligible ? 3 : 2));

        try
        {
            using JsonDocument rows = JsonDocument.Parse(Assert.IsType<string>(await f.H.ScalarAsync(
                "SELECT json_group_array(MemoryId) FROM (SELECT MemoryId FROM long_rest_sources ORDER BY MemoryId);")));

            string[] memories = rows.RootElement.EnumerateArray().Select(row => row.GetString()!).ToArray();

            for (int index = twoEligible ? 1 : 0; index < memories.Length - 1; index++)
            {
                _ = await f.H.Saga.Store.SetPinAsync(memories[index], true, f.Clock.GetUtcNow(), CancellationToken.None);
            }

            f.Handler.InsideSend = () =>
            {
                f.Handler.ChatText = f.Handler.LastBody!.Contains("survivorVersionId", StringComparison.Ordinal)
                    ? "{\"kind\":\"NoChange\",\"survivorVersionId\":null}" : "{\"summary\":\"Accepted summary.\"}";

                return Task.CompletedTask;
            };

            for (int step = 0; step < 64; step++)
            {
                LongRestDiscoveryGeneration generation = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

                if (generation.Phase == LongRestSweepPhase.Discover)
                {
                    Assert.True(generation.Published);

                    Assert.Equal((long)memories.Length, (long)(await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves;"))!);

                    Assert.Equal(twoEligible ? 2L : 1L, await f.H.ScalarAsync("SELECT count(*) FROM saga_memories WHERE PinnedAtUtc IS NULL;"));

                    return f;
                }

                Assert.True(generation.Phase is LongRestSweepPhase.Capture or LongRestSweepPhase.Hierarchy or LongRestSweepPhase.Publish);

                LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));
            }

            throw new InvalidOperationException("Fixture did not publish its real hierarchy within the bounded setup.");
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
