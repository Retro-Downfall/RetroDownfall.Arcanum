using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Memory;

public sealed partial class CovenantMemoryReviewServiceTests
{
    [Fact]
    public async Task The_first_successful_apply_establishes_one_marker_and_advances_its_revision_atomically()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        (CovenantReviewBulkPrepareRequest request, MemoryReviewBulkPlanDto plan) = await PrepareConfirmAsync(runtime);

        Assert.Equal(0L, await ScalarAsync(runtime.Fixture.Connection, "SELECT COUNT(*) FROM covenant_review_markers;"));

        await using CovenantWriteLease lease = runtime.WriteLease();

        Result<MemoryReviewBulkResultDto> applied = await runtime.Service.ApplyAsync(new(request, plan.PreparedPlanToken), lease, Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT COUNT(*) FROM covenant_review_markers;"));

        Assert.Equal(2L, await ScalarAsync(runtime.Fixture.Connection, "SELECT Revision FROM covenant_review_markers;"));

        Assert.Equal(applied.Value.ReviewedThroughEventSequence,
            await ScalarAsync(runtime.Fixture.Connection, "SELECT ReviewedThroughSequence FROM covenant_review_markers;"));

        Result<MemoryReviewBulkResultDto> replay = await runtime.Service.ApplyAsync(new(request, plan.PreparedPlanToken), lease, Token);

        Assert.True(replay.IsSuccess);

        Assert.True(replay.Value.Replayed);

        Assert.Equal(2L, await ScalarAsync(runtime.Fixture.Connection, "SELECT Revision FROM covenant_review_markers;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_review_read_snapshot_does_not_reserve_the_writer_at_lease_revalidation(bool prepare)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        (CovenantReviewBulkPrepareRequest request, _) = await PrepareConfirmAsync(runtime);

        await using SqliteConnection writer = await runtime.Fixture.OpenAdditionalConnectionAsync(Token);

        writer.DefaultTimeout = 1;

        await ExecuteAsync(writer, "PRAGMA busy_timeout = 50;");

        await using CovenantReadLease owned = runtime.ReadLease();

        ProbeReadLease probe = new(owned, async () =>
        {
            await ExecuteAsync(writer, "BEGIN IMMEDIATE;");

            await ExecuteAsync(writer, "ROLLBACK;");
        });

        if (prepare)
        {
            Result<MemoryReviewBulkPlanDto> result = await runtime.Service.PrepareAsync(request, probe, Token);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }
        else
        {
            Result<CovenantReviewPageDto> result = await runtime.Service.ListAsync(
                new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), probe, Token);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }

        Assert.Equal(2, probe.Revalidations);
    }

    [Fact]
    public async Task Global_review_reads_and_first_apply_preserve_the_exact_global_scope()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        _ = await runtime.Fixture.SeedHeadAsync(CovenantScope.Global, null, "global.key",
            CovenantLane.Confirmed, CovenantOperation.Set, "content", Token);

        Guid generation = await runtime.Fixture.ReadDatasetGenerationAsync(Token);

        await using CovenantReadLease read = new(new LeaseRegistration(generation, CovenantLeaseKind.Read, CovenantOperationScope.Global));

        CovenantReviewPageDto page = (await runtime.Service.ListAsync(
            new(CovenantScope.Global, null, CovenantLane.Confirmed, 50, null), read, Token)).Value;

        Assert.Equal("global.key", Assert.Single(page.Items).Key);

        CovenantReviewBulkPrepareRequest request = new(Guid.CreateVersion7(), CovenantScope.Global, null,
            CovenantLane.Confirmed, MemoryReviewAction.Confirm, [new(page.Items[0].ObservationToken, null)]);

        MemoryReviewBulkPlanDto plan = (await runtime.Service.PrepareAsync(request, read, Token)).Value;

        await using CovenantWriteLease write = new(new LeaseRegistration(generation, CovenantLeaseKind.Write, CovenantOperationScope.Global));

        Result<MemoryReviewBulkResultDto> applied = await runtime.Service.ApplyAsync(new(request, plan.PreparedPlanToken), write, Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT COUNT(*) FROM covenant_review_markers WHERE CampaignId IS NULL;"));
    }

    [Fact]
    public async Task A_deleted_campaign_read_cannot_recreate_a_cleaned_queue_marker()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        await using (SqliteCommand marker = runtime.Fixture.Connection.CreateCommand())
        {
            marker.CommandText = """
                INSERT INTO covenant_review_markers
                    (DatasetGeneration, ScopeCode, CampaignId, LaneCode, ReviewedThroughSequence, Revision)
                SELECT DatasetGeneration, 2, $campaign, 1, 0, 1 FROM covenant_state WHERE StateKey = 1;
                """;

            marker.Parameters.AddWithValue("$campaign", CampaignOne.ToString("D"));

            Assert.Equal(1, await marker.ExecuteNonQueryAsync(Token));
        }

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_markers;"));

        await ExecuteAsync(runtime.Fixture.Connection, "DELETE FROM covenant_review_markers;");

        await using (SqliteCommand campaign = runtime.Fixture.Connection.CreateCommand())
        {
            campaign.CommandText = "DELETE FROM Campaigns WHERE Id = $campaign;";

            campaign.Parameters.AddWithValue("$campaign", CampaignOne);

            Assert.Equal(1, await campaign.ExecuteNonQueryAsync(Token));
        }

        // The existing fixture lease deliberately exercises a stale selector reaching the service;
        // production admission retains its own independent Campaign authority checks.
        await using CovenantReadLease lease = runtime.ReadLease();

        Result<CovenantReviewPageDto> listed = await runtime.Service.ListAsync(
            new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), lease, Token);

        Assert.True(listed.IsSuccess, listed.IsFailure ? listed.Error.Message : null);

        Assert.Empty(listed.Value.Items);

        Assert.Equal(0L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_markers;"));
    }

    private sealed class ProbeReadLease(ICovenantSnapshotReadLease inner, Func<Task> atFinalRevalidation) : ICovenantSnapshotReadLease
    {
        internal int Revalidations { get; private set; }

        public CovenantOperationLeaseSnapshot Snapshot => inner.Snapshot;

        public CancellationToken Revocation => inner.Revocation;

        public async ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
        {
            Result result = await inner.RevalidateAsync(cancellationToken);

            if (result.IsSuccess && ++Revalidations == 2)
            {
                await atFinalRevalidation();
            }

            return result;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
