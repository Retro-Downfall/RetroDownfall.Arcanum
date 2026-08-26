using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The operator's curation path, end to end against a real encrypted canonical tier.
/// </summary>
/// <remarks>
/// Every precondition is written through the production write path — the entry with <c>Set</c>, the
/// curation state with <c>Curate</c> — so nothing this suite asserts was put there by the suite.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CovenantCurationServiceTests
{

    private static CancellationToken Token => CancellationToken.None;

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    [Fact]
    public async Task A_prepared_pin_commits_and_the_subject_reports_pinned()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, "preference.builds", "Build from the root.", Token);

        Result<CovenantCurationResultDto> committed = await harness.CurateAsync(
            CovenantCurationKind.Pin,
            CovenantScope.Global,
            null,
            "preference.builds",
            Token);

        Assert.True(committed.IsSuccess, committed.IsFailure ? committed.Error.Message : string.Empty);

        Assert.Equal(CovenantMutationOutcome.Applied, committed.Value.Outcome);

        Assert.True(committed.Value.IsPinned);

        Assert.Equal(1, committed.Value.ResultingRevision);

    }

    /// <summary>
    /// The sentence an operator has to read before they confirm: what applies here afterwards.
    /// </summary>
    [Fact]
    public async Task Preparing_a_mask_reports_that_the_Global_entry_stops_applying_with_nothing_in_its_place()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, "preference.builds", "Build from the root.", Token);

        Result<CovenantCurationPreflightDto> prepared = await harness.PrepareCurationAsync(
            CovenantCurationKind.Mask,
            CovenantScope.Campaign,
            CampaignOne,
            "preference.builds",
            Token);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        Assert.True(prepared.Value.GlobalConfirmedSuppressed);

        Assert.False(prepared.Value.GlobalConfirmedResurfaces);

    }

    /// <summary>
    /// A Campaign already holding its own value for the key is shadowing the Global one, so masking
    /// changes nothing an operator could observe there. Saying otherwise would promise an effect they
    /// will not get.
    /// </summary>
    [Fact]
    public async Task Preparing_a_mask_where_the_Campaign_has_its_own_value_promises_no_suppression()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, "preference.builds", "Build from the root.", Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, "preference.builds", "Build from tools.", Token);

        Result<CovenantCurationPreflightDto> prepared = await harness.PrepareCurationAsync(
            CovenantCurationKind.Mask,
            CovenantScope.Campaign,
            CampaignOne,
            "preference.builds",
            Token);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        Assert.False(prepared.Value.GlobalConfirmedSuppressed);

    }

    [Fact]
    public async Task Preparing_an_unmask_reports_that_the_Global_entry_starts_applying_again()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, "preference.builds", "Build from the root.", Token);

        _ = await harness.CurateAsync(
            CovenantCurationKind.Mask,
            CovenantScope.Campaign,
            CampaignOne,
            "preference.builds",
            Token);

        Result<CovenantCurationPreflightDto> prepared = await harness.PrepareCurationAsync(
            CovenantCurationKind.Unmask,
            CovenantScope.Campaign,
            CampaignOne,
            "preference.builds",
            Token);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        Assert.True(prepared.Value.GlobalConfirmedResurfaces);

    }

    /// <summary>
    /// The token binds the whole request. Carrying one from a cheap subject onto another is the failure
    /// the two-step protocol exists to close.
    /// </summary>
    [Fact]
    public async Task A_commit_carrying_a_token_prepared_for_another_subject_is_refused()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, "preference.builds", "Build from the root.", Token);

        await harness.SetAsync(CovenantScope.Global, null, "preference.tests", "Run tests quietly.", Token);

        Result<CovenantCurationPreflightDto> prepared = await harness.PrepareCurationAsync(
            CovenantCurationKind.Pin,
            CovenantScope.Global,
            null,
            "preference.builds",
            Token);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        Result<CovenantCurationResultDto> refused = await harness.CommitCurationAsync(
            new CovenantCurationRequest(
                CovenantCurationKind.Pin,
                CovenantScope.Global,
                null,
                "preference.tests",
                CovenantLane.Confirmed,
                ExpectedRevision: 0,
                prepared.Value.MutationId,
                prepared.Value.PreflightToken),
            Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

    }

    /// <summary>
    /// A Global mask is refused by the request's own validation, before a single read is opened.
    /// </summary>
    [Fact]
    public void A_Global_mask_request_refuses_itself()
    {

        Result validated = new CovenantCurationPrepareRequest(
            CovenantCurationKind.Mask,
            CovenantScope.Global,
            null,
            "preference.builds",
            CovenantLane.Confirmed,
            ExpectedRevision: 0,
            Guid.CreateVersion7()).Validate();

        Assert.True(validated.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, validated.Error.Code);

    }

    [Fact]
    public void A_mask_of_the_Proposed_lane_refuses_itself()
    {

        Result validated = new CovenantCurationPrepareRequest(
            CovenantCurationKind.Mask,
            CovenantScope.Campaign,
            CampaignOne,
            "preference.builds",
            CovenantLane.Proposed,
            ExpectedRevision: 0,
            Guid.CreateVersion7()).Validate();

        Assert.True(validated.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, validated.Error.Code);

    }

    /// <summary>
    /// Receipt first. A client that lost its response and retried after the five-minute lifetime gets
    /// its committed answer rather than a stale-token refusal for work that already happened.
    /// </summary>
    [Fact]
    public async Task A_repeat_commit_after_the_token_expired_still_replays_the_committed_receipt()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, "preference.builds", "Build from the root.", Token);

        Result<CovenantCurationPreflightDto> prepared = await harness.PrepareCurationAsync(
            CovenantCurationKind.Pin,
            CovenantScope.Global,
            null,
            "preference.builds",
            Token);

        CovenantCurationRequest request = new(
            CovenantCurationKind.Pin,
            CovenantScope.Global,
            null,
            "preference.builds",
            CovenantLane.Confirmed,
            ExpectedRevision: 0,
            prepared.Value.MutationId,
            prepared.Value.PreflightToken);

        Result<CovenantCurationResultDto> first = await harness.CommitCurationAsync(request, Token);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : string.Empty);

        harness.Advance(TimeSpan.FromHours(1));

        Result<CovenantCurationResultDto> replayed = await harness.CommitCurationAsync(request, Token);

        Assert.True(replayed.IsSuccess, replayed.IsFailure ? replayed.Error.Message : string.Empty);

        Assert.True(replayed.Value.Replayed);

        Assert.Equal(first.Value.ResultingVersionId, replayed.Value.ResultingVersionId);

    }

}
