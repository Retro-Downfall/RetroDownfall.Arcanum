using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The exact retirement target a Ward is shown, resolved outside the inference hot path.
/// </summary>
/// <remarks>
/// The operator cannot consent to "retire something the model named". They have to see the content
/// that is about to disappear, which lane it lives in, which revision it is, and whether Global
/// content starts applying in its place. This resolves that, and refuses everything the operator must
/// not be asked to approve.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CovenantRetirementPreflightTests
{

    private static CancellationToken Token => CancellationToken.None;

    private const string Key = "preference.builds";

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    [Fact]
    public async Task Resolving_a_live_Campaign_head_reports_that_Global_content_starts_applying()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from tools.", Token);

        CovenantRetirementPreflight preflight = await ResolveAsync(harness);

        Assert.True(preflight.GlobalFallbackApplies);

        Assert.Equal(Key, preflight.NormalizedKey);

        Assert.Equal(CovenantLane.Confirmed, preflight.Lane);

        Assert.Equal(1, preflight.TargetLaneRevision);

        // The disclosure is the compiled fragment, already hardened by the compiler. An operator
        // approving a retirement reads the content, not a hash of it.
        Assert.Contains("Build from tools.", preflight.SanitizedAuthoredDisclosure, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Resolving_a_head_with_no_Global_sibling_reports_no_fallback()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from tools.", Token);

        Assert.False((await ResolveAsync(harness)).GlobalFallbackApplies);

    }

    /// <summary>
    /// A masked Global entry is not applying here, so retiring the Campaign entry reveals nothing.
    /// Promising a fallback that a mask suppresses would describe an effect the operator will not get.
    /// </summary>
    [Fact]
    public async Task A_masked_Global_sibling_is_not_reported_as_a_fallback()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from tools.", Token);

        Assert.True((await ResolveAsync(harness)).GlobalFallbackApplies);

        _ = await harness.CurateAsync(
            CovenantCurationKind.Mask,
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            Token);

        Assert.False((await ResolveAsync(harness)).GlobalFallbackApplies);

    }

    [Fact]
    public async Task Resolving_a_key_with_no_head_in_that_lane_is_refused()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        Result<CovenantRetirementPreflight> refused = await TryResolveAsync(harness);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    [Fact]
    public async Task Resolving_an_already_retired_head_is_refused()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from tools.", Token);

        await harness.RetireAsync(CovenantScope.Campaign, CampaignOne, Key, 1, Token);

        Result<CovenantRetirementPreflight> refused = await TryResolveAsync(harness);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    /// <summary>
    /// A pinned head is refused here, before a Ward is ever raised, so the operator is never asked to
    /// approve something the write authority would refuse anyway.
    /// </summary>
    [Fact]
    public async Task Resolving_a_pinned_head_is_refused_before_a_Ward_can_be_raised()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from tools.", Token);

        _ = await harness.CurateAsync(
            CovenantCurationKind.Pin,
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            Token);

        Result<CovenantRetirementPreflight> refused = await TryResolveAsync(harness);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

    }

    /// <summary>
    /// The pin is found through the key row's own binding epoch. A key that existed before the binding
    /// epoch did carries a nonzero one, so a target read that assumed zero would stop refusing every
    /// pin an upgraded installation already held.
    /// </summary>
    [Fact]
    public async Task Resolving_a_pinned_head_of_a_key_with_a_nonzero_binding_epoch_is_still_refused()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SeedUpgradedKeyAsync(Key, epoch: 3, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from tools.", Token);

        // Not pinned yet, so the target resolves: the refusal below is the pin's and nothing else's.
        Assert.Equal(1, (await ResolveAsync(harness)).TargetLaneRevision);

        Result<CovenantCurationResultDto> pinned = await harness.CurateAsync(
            CovenantCurationKind.Pin,
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            Token);

        Assert.True(pinned.IsSuccess, pinned.IsFailure ? pinned.Error.Message : string.Empty);

        // The operator writes the key in the other scope, which moves the dependency epoch only.
        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        Result<CovenantRetirementPreflight> refused = await TryResolveAsync(harness);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

    }

    /// <summary>
    /// The mask is found through the key row's own binding epoch too. Read under an assumed zero, a
    /// mask an upgraded installation already held would be missed, and the target would promise a
    /// Global fallback the mask suppresses.
    /// </summary>
    [Fact]
    public async Task A_masked_Global_sibling_of_a_key_with_a_nonzero_binding_epoch_is_not_reported_as_a_fallback()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SeedUpgradedKeyAsync(Key, epoch: 3, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from tools.", Token);

        Assert.True((await ResolveAsync(harness)).GlobalFallbackApplies);

        Result<CovenantCurationResultDto> masked = await harness.CurateAsync(
            CovenantCurationKind.Mask,
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            Token);

        Assert.True(masked.IsSuccess, masked.IsFailure ? masked.Error.Message : string.Empty);

        Assert.False((await ResolveAsync(harness)).GlobalFallbackApplies);

        // Correcting the Global entry is an ordinary write, and the mask still covers it afterwards.
        await harness.CorrectAsync(CovenantScope.Global, null, Key, "Build from the tools directory.", Token);

        Assert.False((await ResolveAsync(harness)).GlobalFallbackApplies);

    }

    /// <summary>
    /// The digest the staged tombstone carries as evidence is bound to the target, so two different
    /// targets cannot present the same proof of what an operator was shown.
    /// </summary>
    [Fact]
    public async Task Two_different_targets_do_not_share_a_preflight_digest()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from tools.", Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, "preference.tests", "Run tests quietly.", Token);

        CovenantRetirementPreflight first = await ResolveAsync(harness);

        CovenantRetirementPreflight second = await ResolveAsync(harness, "preference.tests");

        Assert.NotEqual(first.PreflightBodyDigest, second.PreflightBodyDigest);

    }

    private static async Task<CovenantRetirementPreflight> ResolveAsync(
        CovenantServiceHarness harness,
        string key = Key)
    {

        Result<CovenantRetirementPreflight> resolved = await TryResolveAsync(harness, key);

        Assert.True(resolved.IsSuccess, resolved.IsFailure ? resolved.Error.Message : string.Empty);

        return resolved.Value;

    }

    private static async Task<Result<CovenantRetirementPreflight>> TryResolveAsync(
        CovenantServiceHarness harness,
        string key = Key)
    {

        await using ICovenantSnapshotReadLease read =
            (await harness.Gate.AcquireReadAsync(CovenantOperationScope.ForCampaign(CampaignOne), Token)).Value;

        ICovenantTurnHeadProbe probe = new CovenantTurnHeadProbe(
            harness.Fixture.Store,
            CovenantCanonicalFixture.CampaignContext(CampaignOne),
            read);

        return await probe.ResolveRetirementPreflightAsync(CovenantLane.Confirmed, key, Token);

    }

}
