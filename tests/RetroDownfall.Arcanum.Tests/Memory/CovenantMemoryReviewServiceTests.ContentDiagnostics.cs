using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Memory;

public sealed partial class CovenantMemoryReviewServiceTests
{
    [Theory]
    [InlineData(false, "U+200B")]
    [InlineData(true, "U+0000")]
    public async Task A_review_correction_locates_the_invalid_replacement_scalar_without_exposing_content(bool control, string codePoint)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        _ = await runtime.Fixture.SeedHeadAsync(CovenantScope.Campaign, CampaignOne, "response.style",
            CovenantLane.Confirmed, CovenantOperation.Set, "A current preference.", Token);

        CovenantReviewItemDto observed;

        await using (CovenantReadLease list = runtime.ReadLease())
        {
            Result<CovenantReviewPageDto> page = await runtime.Service.ListAsync(
                new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), list, Token);

            Assert.True(page.IsSuccess);

            observed = Assert.Single(page.Value.Items);
        }

        string replacement = control ? "😀A\0PRIVATE_REPLACEMENT_CANARY" : "😀A\u200BPRIVATE_REPLACEMENT_CANARY";

        CovenantReviewBulkPrepareRequest request = new(Guid.CreateVersion7(), CovenantScope.Campaign, CampaignOne,
            CovenantLane.Confirmed, MemoryReviewAction.Correct, [new(observed.ObservationToken, replacement)]);

        await using CovenantReadLease prepare = runtime.ReadLease();

        Result<MemoryReviewBulkPlanDto> result = await runtime.Service.PrepareAsync(request, prepare, Token);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Validation.InvalidBody, result.Error.Code);

        Assert.Contains("decision", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("replacementContent", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains(codePoint, result.Error.Message, StringComparison.Ordinal);

        Assert.Matches("(?i)scalar(?: position)? 3", result.Error.Message);

        Assert.DoesNotContain("PRIVATE_REPLACEMENT_CANARY", result.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("(Parameter", result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(1, await ScalarAsync(runtime.Fixture.Connection, "SELECT COUNT(*) FROM covenant_versions;"));

        Assert.Equal(0, await ScalarAsync(runtime.Fixture.Connection, "SELECT COUNT(*) FROM covenant_review_decision_receipts;"));
    }
}
