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

    /// <summary>
    /// The preflight reads the subject's curation through the key row's own binding epoch. A key that
    /// existed before the binding epoch did carries a nonzero one, so a read that assumed zero would
    /// show an upgraded installation's pin as absent at revision 0, and the unpin an operator then
    /// prepared from that screen could never commit.
    /// </summary>
    [Fact]
    public async Task Preparing_an_unpin_of_a_key_with_a_nonzero_binding_epoch_reports_the_pin_and_commits()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SeedUpgradedKeyAsync("preference.builds", epoch: 3, Token);

        await harness.SetAsync(CovenantScope.Global, null, "preference.builds", "Build from the root.", Token);

        Result<CovenantCurationResultDto> pinned = await harness.CurateAsync(
            CovenantCurationKind.Pin,
            CovenantScope.Global,
            null,
            "preference.builds",
            Token);

        Assert.True(pinned.IsSuccess, pinned.IsFailure ? pinned.Error.Message : string.Empty);

        // The operator writes the key again, which moves the dependency epoch and nothing else.
        await harness.CorrectAsync(CovenantScope.Global, null, "preference.builds", "Build from tools.", Token);

        Result<CovenantCurationPreflightDto> prepared = await harness.PrepareCurationAsync(
            CovenantCurationKind.Unpin,
            CovenantScope.Global,
            null,
            "preference.builds",
            Token,
            expectedRevision: 1);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        Assert.True(prepared.Value.IsPinned);

        Assert.Equal(1, prepared.Value.CurrentRevision);

        // The dependency epoch is the one reported: the upgrade left 3, and two writes followed.
        Assert.Equal(5, prepared.Value.KeyEpoch);

        Result<CovenantCurationResultDto> unpinned = await harness.CommitCurationAsync(
            new CovenantCurationRequest(
                CovenantCurationKind.Unpin,
                CovenantScope.Global,
                null,
                "preference.builds",
                CovenantLane.Confirmed,
                ExpectedRevision: 1,
                prepared.Value.MutationId,
                prepared.Value.PreflightToken),
            Token);

        Assert.True(unpinned.IsSuccess, unpinned.IsFailure ? unpinned.Error.Message : string.Empty);

        Assert.Equal(CovenantMutationOutcome.Applied, unpinned.Value.Outcome);

        Assert.False(unpinned.Value.IsPinned);

        Assert.Equal(2, unpinned.Value.ResultingRevision);

    }

    /// <summary>
    /// The same read for a mask: the preflight has to find the mask an upgraded installation already
    /// holds before it can say that lifting it lets the Global entry apply again.
    /// </summary>
    [Fact]
    public async Task Preparing_an_unmask_of_a_key_with_a_nonzero_binding_epoch_reports_the_mask_and_what_resurfaces()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SeedUpgradedKeyAsync("preference.builds", epoch: 3, Token);

        await harness.SetAsync(CovenantScope.Global, null, "preference.builds", "Build from the root.", Token);

        Result<CovenantCurationResultDto> masked = await harness.CurateAsync(
            CovenantCurationKind.Mask,
            CovenantScope.Campaign,
            CampaignOne,
            "preference.builds",
            Token);

        Assert.True(masked.IsSuccess, masked.IsFailure ? masked.Error.Message : string.Empty);

        await harness.CorrectAsync(CovenantScope.Global, null, "preference.builds", "Build from tools.", Token);

        Result<CovenantCurationPreflightDto> prepared = await harness.PrepareCurationAsync(
            CovenantCurationKind.Unmask,
            CovenantScope.Campaign,
            CampaignOne,
            "preference.builds",
            Token,
            expectedRevision: 1);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        Assert.True(prepared.Value.IsMasked);

        Assert.Equal(1, prepared.Value.CurrentRevision);

        Assert.True(prepared.Value.GlobalConfirmedResurfaces);

    }

    /// <summary>
    /// A change prepared before some other write to the key is refused, and the refusal leaves nothing
    /// behind. The counts are read on a second connection after the call returns, so they describe
    /// what the service committed rather than what its own transaction could still see.
    /// </summary>
    [Fact]
    public async Task A_commit_prepared_before_another_write_to_the_key_is_refused_as_stale_and_commits_nothing()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        // Curation that already exists, so "unchanged" is a statement about three nonzero counts.
        await harness.SetAsync(CovenantScope.Global, null, "preference.tests", "Run tests quietly.", Token);

        Result<CovenantCurationResultDto> existing = await harness.CurateAsync(
            CovenantCurationKind.Pin,
            CovenantScope.Global,
            null,
            "preference.tests",
            Token);

        Assert.True(existing.IsSuccess, existing.IsFailure ? existing.Error.Message : string.Empty);

        await harness.SetAsync(CovenantScope.Global, null, "preference.builds", "Build from the root.", Token);

        Result<CovenantCurationPreflightDto> prepared = await harness.PrepareCurationAsync(
            CovenantCurationKind.Pin,
            CovenantScope.Global,
            null,
            "preference.builds",
            Token);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        Assert.Equal(1, prepared.Value.KeyEpoch);

        (long Heads, long Versions, long Receipts) before = await CurationCountsOnAFreshConnectionAsync(harness);

        Assert.Equal((1L, 1L, 1L), before);

        // An ordinary write to the key. Its dependency epoch moves; the token still says 1.
        await harness.CorrectAsync(CovenantScope.Global, null, "preference.builds", "Build from tools.", Token);

        Result<CovenantCurationResultDto> refused = await harness.CommitCurationAsync(
            new CovenantCurationRequest(
                CovenantCurationKind.Pin,
                CovenantScope.Global,
                null,
                "preference.builds",
                CovenantLane.Confirmed,
                ExpectedRevision: 0,
                prepared.Value.MutationId,
                prepared.Value.PreflightToken),
            Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

        Assert.Equal(before, await CurationCountsOnAFreshConnectionAsync(harness));

        // The refused identity left no receipt, so a retry is a new request rather than a replay.
        await using Microsoft.Data.Sqlite.SqliteCommand command = harness.Fixture.Connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM covenant_curation_receipts WHERE MutationId = $mutation;";

        _ = command.Parameters.AddWithValue("$mutation", prepared.Value.MutationId.ToString("D"));

        Assert.Equal(
            0L,
            Convert.ToInt64(
                await command.ExecuteScalarAsync(Token),
                System.Globalization.CultureInfo.InvariantCulture));

    }

    /// <summary>
    /// Counts the three curation tables through a connection the service never held.
    /// </summary>
    private static async Task<(long Heads, long Versions, long Receipts)> CurationCountsOnAFreshConnectionAsync(
        CovenantServiceHarness harness)
    {

        await using Microsoft.Data.Sqlite.SqliteConnection fresh =
            await harness.Fixture.OpenAdditionalConnectionAsync(Token);

        await using Microsoft.Data.Sqlite.SqliteCommand command = fresh.CreateCommand();

        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM covenant_curation_heads),
                   (SELECT COUNT(*) FROM covenant_curation_versions),
                   (SELECT COUNT(*) FROM covenant_curation_receipts);
            """;

        await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync(Token);

        Assert.True(await reader.ReadAsync(Token));

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));

    }

    /// <summary>
    /// The other half of receipt-first: only a committed change outlives its token. A request that
    /// never committed and arrives after the lifetime is refused by the codec, and writes nothing.
    /// </summary>
    [Fact]
    public async Task A_first_commit_after_the_token_expired_is_refused_and_writes_nothing()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, "preference.builds", "Build from the root.", Token);

        Result<CovenantCurationPreflightDto> prepared = await harness.PrepareCurationAsync(
            CovenantCurationKind.Pin,
            CovenantScope.Global,
            null,
            "preference.builds",
            Token);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        harness.Advance(TimeSpan.FromHours(1));

        Result<CovenantCurationResultDto> refused = await harness.CommitCurationAsync(
            new CovenantCurationRequest(
                CovenantCurationKind.Pin,
                CovenantScope.Global,
                null,
                "preference.builds",
                CovenantLane.Confirmed,
                ExpectedRevision: 0,
                prepared.Value.MutationId,
                prepared.Value.PreflightToken),
            Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

        await using Microsoft.Data.Sqlite.SqliteCommand command = harness.Fixture.Connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM covenant_curation_receipts;";

        Assert.Equal(
            0L,
            Convert.ToInt64(
                await command.ExecuteScalarAsync(Token),
                System.Globalization.CultureInfo.InvariantCulture));

    }

}
