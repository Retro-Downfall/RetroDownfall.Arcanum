using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Tests.Memory;

public sealed class MemoryReviewContractTests
{
    private static readonly Guid Campaign = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly Guid RequestId = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Review_pages_refuse_limits_outside_the_closed_bound(int limit)
    {
        AssertInvalid(new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, limit, null).Validate());

        AssertInvalid(new LexiconReviewListRequest(GlobalLexiconScope(), limit, null).Validate());

        AssertInvalid(new CovenantReviewListRequest(
            CovenantScope.Global,
            CampaignId: null,
            CovenantLane.Confirmed,
            limit,
            Cursor: null).Validate());
    }

    [Fact]
    public void Review_pages_accept_the_fifty_item_bound()
    {
        Assert.True(new SagaReviewListRequest(
            SagaMemoryScopeKind.Campaign,
            Campaign,
            MemoryReviewLimits.MaxPageSize,
            Cursor: null).Validate().IsSuccess);

        Assert.True(new LexiconReviewListRequest(
            new LexiconCurationScope(LexiconScopeKind.Campaign, Campaign),
            MemoryReviewLimits.MaxPageSize,
            Cursor: null).Validate().IsSuccess);

        Assert.True(new CovenantReviewListRequest(
            CovenantScope.Campaign,
            Campaign,
            CovenantLane.Proposed,
            MemoryReviewLimits.MaxPageSize,
            Cursor: null).Validate().IsSuccess);
    }

    [Fact]
    public void Review_pages_require_one_exact_store_scope()
    {
        AssertInvalid(new SagaReviewListRequest(
            SagaMemoryScopeKind.Global,
            Campaign,
            10,
            Cursor: null).Validate());

        AssertInvalid(new SagaReviewListRequest(
            SagaMemoryScopeKind.Campaign,
            CampaignId: null,
            10,
            Cursor: null).Validate());

        AssertInvalid(new CovenantReviewListRequest(
            CovenantScope.Global,
            Campaign,
            CovenantLane.Confirmed,
            10,
            Cursor: null).Validate());

        AssertInvalid(new CovenantReviewListRequest(
            CovenantScope.Campaign,
            CampaignId: null,
            CovenantLane.Confirmed,
            10,
            Cursor: null).Validate());
    }

    [Fact]
    public void Bulk_requests_are_single_action_nonempty_and_bounded()
    {
        SagaReviewBulkPrepareRequest empty = new(
            RequestId,
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Confirm,
            []);

        SagaReviewDecision[] oversized = Enumerable
            .Range(0, MemoryReviewLimits.MaxBulkOperations + 1)
            .Select(index => new SagaReviewDecision($"observation-{index}", ReplacementContent: null))
            .ToArray();

        AssertInvalid(empty.Validate());

        AssertInvalid((empty with { Decisions = oversized }).Validate());

        AssertInvalid((empty with
        {
            Decisions = [new SagaReviewDecision("observation", ReplacementContent: null)],
            Action = default,
        }).Validate());
    }

    [Fact]
    public void Confirm_is_acknowledgement_only_and_rejects_replacement_content()
    {
        SagaReviewBulkPrepareRequest request = new(
            RequestId,
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Confirm,
            [new SagaReviewDecision("observation", "replacement")]);

        AssertInvalid(request.Validate());
    }

    [Fact]
    public void Correction_requires_replacement_content_and_other_actions_forbid_it()
    {
        SagaReviewBulkPrepareRequest baseline = new(
            RequestId,
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Correct,
            [new SagaReviewDecision("observation", ReplacementContent: null)]);

        AssertInvalid(baseline.Validate());

        Assert.True((baseline with
        {
            Decisions = [new SagaReviewDecision("observation", "corrected")],
        }).Validate().IsSuccess);

        AssertInvalid((baseline with
        {
            Action = MemoryReviewAction.Retire,
            Decisions = [new SagaReviewDecision("observation", "corrected")],
        }).Validate());
    }

    [Fact]
    public void Duplicate_observations_are_refused_before_any_store_is_called()
    {
        LexiconReviewBulkPrepareRequest request = new(
            RequestId,
            GlobalLexiconScope(),
            MemoryReviewAction.Pin,
            [
                new LexiconReviewDecision("same", ReplacementContent: null),
                new LexiconReviewDecision("same", ReplacementContent: null),
            ]);

        AssertInvalid(request.Validate());
    }

    [Fact]
    public void Apply_repeats_the_exact_prepared_request_and_requires_its_token()
    {
        CovenantReviewBulkPrepareRequest prepared = new(
            RequestId,
            CovenantScope.Global,
            CampaignId: null,
            CovenantLane.Confirmed,
            MemoryReviewAction.Unpin,
            [new CovenantReviewDecision("observation", ReplacementContent: null)]);

        AssertInvalid(new CovenantReviewBulkApplyRequest(prepared, PreparedPlanToken: " ").Validate());

        Assert.True(new CovenantReviewBulkApplyRequest(prepared, PreparedPlanToken: "prepared-token").Validate().IsSuccess);
    }

    [Fact]
    public void Bounded_review_items_use_current_snapshots_without_unbounded_histories()
    {
        Assert.Equal(
            typeof(SagaReviewCurrentDto),
            typeof(SagaReviewItemDto).GetProperty(nameof(SagaReviewItemDto.Current))!.PropertyType);

        Assert.Equal(
            typeof(LexiconReviewCurrentDto),
            typeof(LexiconReviewItemDto).GetProperty(nameof(LexiconReviewItemDto.Current))!.PropertyType);

        Assert.Null(typeof(SagaReviewCurrentDto).GetProperty("History"));
        Assert.Null(typeof(LexiconReviewCurrentDto).GetProperty("AnnalHistory"));
        Assert.Null(typeof(LexiconReviewCurrentDto).GetProperty("HistoricalFactProvenance"));
    }

    private static LexiconCurationScope GlobalLexiconScope() =>
        new(LexiconScopeKind.Global, CampaignId: null);

    private static void AssertInvalid(Result result) => Assert.True(result.IsFailure);
}
