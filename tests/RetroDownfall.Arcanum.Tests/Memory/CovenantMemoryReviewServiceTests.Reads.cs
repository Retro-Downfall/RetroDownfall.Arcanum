using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Memory;

public sealed partial class CovenantMemoryReviewServiceTests
{
    [Fact]
    public async Task Listing_unknown_campaigns_does_not_create_durable_queue_markers()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        for (int index = 0; index < 8; index++)
        {
            Guid campaign = Guid.CreateVersion7();

            await using CovenantReadLease lease = runtime.ReadLease(campaign);

            Result<CovenantReviewPageDto> page = await runtime.Service.ListAsync(
                new(CovenantScope.Campaign, campaign, CovenantLane.Proposed, 50, null), lease, Token);

            Assert.True(page.IsSuccess, page.IsFailure ? page.Error.Message : null);

            Assert.Empty(page.Value.Items);
        }

        Assert.Equal(0L, await ScalarAsync(runtime.Fixture.Connection, "SELECT COUNT(*) FROM covenant_review_markers;"));

        Assert.Equal(0L, await ScalarAsync(runtime.Fixture.Connection, "SELECT COUNT(*) FROM covenant_review_decision_receipts;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_reads_while_another_native_connection_holds_the_writer(bool existingMarker)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        _ = await runtime.Fixture.SeedHeadAsync(CovenantScope.Campaign, CampaignOne, "read.key",
            CovenantLane.Confirmed, CovenantOperation.Set, "content", Token);

        if (existingMarker)
        {
            await using SqliteCommand marker = runtime.Fixture.Connection.CreateCommand();

            marker.CommandText = """
                INSERT INTO covenant_review_markers
                    (DatasetGeneration, ScopeCode, CampaignId, LaneCode, ReviewedThroughSequence, Revision)
                SELECT DatasetGeneration, 2, $campaign, 1, 0, 1 FROM covenant_state WHERE StateKey = 1;
                """;

            marker.Parameters.AddWithValue("$campaign", CampaignOne.ToString("D"));

            Assert.Equal(1, await marker.ExecuteNonQueryAsync(Token));
        }

        runtime.Fixture.Connection.DefaultTimeout = 1;

        await ExecuteAsync(runtime.Fixture.Connection, "PRAGMA busy_timeout = 50;");

        await using SqliteConnection writer = await runtime.Fixture.OpenAdditionalConnectionAsync(Token);

        await ExecuteAsync(writer, "BEGIN IMMEDIATE;");

        try
        {
            await using CovenantReadLease lease = runtime.ReadLease();

            Result<CovenantReviewPageDto>? page = null;

            Exception? failure = await Record.ExceptionAsync(async () => page = await runtime.Service.ListAsync(
                new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), lease, Token));

            Assert.Null(failure);

            Assert.NotNull(page);

            Assert.True(page.IsSuccess, page.IsFailure ? page.Error.Message : null);

            Assert.Equal("read.key", Assert.Single(page.Value.Items).Key);
        }
        finally
        {
            await ExecuteAsync(writer, "ROLLBACK;");
        }
    }

    [Fact]
    public async Task Prepare_reads_while_another_native_connection_holds_the_writer()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        _ = await runtime.Fixture.SeedHeadAsync(CovenantScope.Campaign, CampaignOne, "prepare.key",
            CovenantLane.Confirmed, CovenantOperation.Set, "content", Token);

        await using CovenantReadLease lease = runtime.ReadLease();

        CovenantReviewPageDto page = (await runtime.Service.ListAsync(
            new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), lease, Token)).Value;

        CovenantReviewBulkPrepareRequest request = new(Guid.CreateVersion7(), CovenantScope.Campaign,
            CampaignOne, CovenantLane.Confirmed, MemoryReviewAction.Confirm,
            [new(page.Items[0].ObservationToken, null)]);

        runtime.Fixture.Connection.DefaultTimeout = 1;

        await ExecuteAsync(runtime.Fixture.Connection, "PRAGMA busy_timeout = 50;");

        await using SqliteConnection writer = await runtime.Fixture.OpenAdditionalConnectionAsync(Token);

        await ExecuteAsync(writer, "BEGIN IMMEDIATE;");

        try
        {
            Result<MemoryReviewBulkPlanDto>? plan = null;

            Exception? failure = await Record.ExceptionAsync(async () => plan = await runtime.Service.PrepareAsync(request, lease, Token));

            Assert.Null(failure);

            Assert.NotNull(plan);

            Assert.True(plan.IsSuccess, plan.IsFailure ? plan.Error.Message : null);
        }
        finally
        {
            await ExecuteAsync(writer, "ROLLBACK;");
        }
    }

    [Fact]
    public async Task Prepared_target_exposes_the_normalized_key_as_a_distinct_serialized_label()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        _ = await runtime.Fixture.SeedHeadAsync(CovenantScope.Campaign, CampaignOne, "target.key",
            CovenantLane.Confirmed, CovenantOperation.Set, "content", Token);

        await using CovenantReadLease lease = runtime.ReadLease();

        CovenantReviewPageDto page = (await runtime.Service.ListAsync(
            new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), lease, Token)).Value;

        CovenantReviewBulkPrepareRequest request = new(Guid.CreateVersion7(), CovenantScope.Campaign,
            CampaignOne, CovenantLane.Confirmed, MemoryReviewAction.Confirm,
            [new(page.Items[0].ObservationToken, null)]);

        MemoryReviewBulkPlanDto plan = (await runtime.Service.PrepareAsync(request, lease, Token)).Value;

        JsonElement json = JsonSerializer.SerializeToElement(plan, ArcanumJsonContext.Default.MemoryReviewBulkPlanDto);

        Assert.True(json.GetProperty("items")[0].TryGetProperty("label", out JsonElement label));

        Assert.Equal("target.key", label.GetString());

        Assert.Equal(page.Items[0].EntryId.ToString("D"), plan.Items[0].SubjectId);
    }
}
