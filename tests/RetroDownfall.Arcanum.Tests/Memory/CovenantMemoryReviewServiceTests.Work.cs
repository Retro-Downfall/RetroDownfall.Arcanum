using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Memory;

public sealed partial class CovenantMemoryReviewServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_review_page_work_is_independent_of_unrelated_campaign_events(bool emptyQueue)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        if (!emptyQueue)
        {
            _ = await runtime.Fixture.SeedHeadAsync(CovenantScope.Campaign, CampaignOne, "target.work",
                CovenantLane.Confirmed, CovenantOperation.Set, "content", Token);
        }

        long small = await ReviewListVmStepsAsync(runtime, emptyQueue ? 0 : 1);

        for (int campaignIndex = 0; campaignIndex < 8; campaignIndex++)
        {
            Guid campaign = Guid.CreateVersion7();

            await runtime.Fixture.AddCampaignAsync(campaign, $"Other{campaignIndex}", Token);

            for (int index = 0; index < 128; index++)
            {
                _ = await runtime.Fixture.SeedHeadAsync(CovenantScope.Campaign, campaign, $"unrelated.{index}",
                    CovenantLane.Confirmed, CovenantOperation.Set, "unrelated", Token);
            }
        }

        long large = await ReviewListVmStepsAsync(runtime, emptyQueue ? 0 : 1);

        output.WriteLine($"Native Review VM instructions: empty={emptyQueue}; unrelated events0→1024; small={small}; large={large}.");

        Assert.True(small > 0);

        Assert.True(large <= small + 500,
            $"Unrelated Campaign events changed native Review work from {small} to {large} instructions.");
    }

    [Fact]
    public async Task Review_head_and_source_hashes_match_the_canonical_lower_case_native_bytes()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        _ = await runtime.Fixture.SeedHeadAsync(CovenantScope.Campaign, CampaignOne, "hash.key",
            CovenantLane.Confirmed, CovenantOperation.Set, "content", Token,
            sources:
            [
                new MaterializationSourceDigestInput(Guid.CreateVersion7(), Guid.CreateVersion7(), "private-source.md",
                    Digest(0xAB), CovenantMaterializationSourceRange.WholeSource, null, null,
                    ImmutableArray<MaterializationOccurrenceDigestInput>.Empty),
            ]);

        await using CovenantReadLease lease = runtime.ReadLease();

        CovenantReviewItemDto item = Assert.Single((await runtime.Service.ListAsync(
            new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), lease, Token)).Value.Items);

        await using SqliteCommand command = runtime.Fixture.Connection.CreateCommand();

        command.CommandText = """
            SELECT lower(hex(AuthoredHash)), lower(hex(RenderedHash)), lower(hex(AttachmentProvenanceDigest))
            FROM covenant_versions WHERE VersionId = $version;
            """;

        command.Parameters.AddWithValue("$version", item.VersionId.ToString("D"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);

        Assert.True(await reader.ReadAsync(Token));

        Assert.NotNull(item.Current);

        Assert.Equal(reader.GetString(0), item.Current.AuthoredHash);

        Assert.Equal(reader.GetString(1), item.Current.RenderedHash);

        Assert.Equal(reader.GetString(2), item.Current.ProvenanceDigest);

        Assert.Equal(Convert.ToHexStringLower(Digest(0xAB).Bytes), Assert.Single(item.Sources).ContentHash);
    }

    private static async Task<long> ReviewListVmStepsAsync(ReviewRuntime runtime, int expectedItems)
    {
        long instructions = 0;

        raw.sqlite3_progress_handler(runtime.Fixture.Connection.Handle, 1, (object _) =>
        {
            instructions++;

            return 0;
        }, null);

        try
        {
            await using CovenantReadLease lease = runtime.ReadLease();

            Result<CovenantReviewPageDto> result = await runtime.Service.ListAsync(
                new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), lease, Token);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            Assert.Equal(expectedItems, result.Value.Items.Length);
        }
        finally
        {
            raw.sqlite3_progress_handler(runtime.Fixture.Connection.Handle, 0, null, null);
        }

        return instructions;
    }
}
