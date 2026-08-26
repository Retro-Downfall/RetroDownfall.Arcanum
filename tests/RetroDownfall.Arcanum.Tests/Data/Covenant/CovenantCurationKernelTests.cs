using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// State, receipt, and conflict behaviour of the transactional curation kernel.
/// </summary>
/// <remarks>
/// Every precondition here is reached by applying a change through the kernel, never by writing a
/// curation row. A test that seeded the state it then asserted could not discover that nothing in
/// production can produce it.
/// </remarks>
public sealed class CovenantCurationKernelTests
{

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_pin_on_an_uncurated_subject_opens_revision_one()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantCurationReceipt receipt = await ApplyAsync(
            fixture,
            CovenantCurationFixture.Pin(CovenantOperationScope.Global, "global.style", expectedRevision: 0));

        Assert.Equal(CovenantMutationOutcome.Applied, receipt.Outcome);

        Assert.Equal(1L, receipt.ResultingRevision);

        Assert.True(receipt.ResultingState.IsPinned);

        Assert.False(receipt.Replayed);

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_curation_versions;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_curation_heads;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_curation_receipts;"));

    }

    [Fact]
    public async Task An_unpin_advances_the_revision_and_links_its_predecessor()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantCurationReceipt pinned = await ApplyAsync(
            fixture,
            CovenantCurationFixture.Pin(CovenantOperationScope.Global, "global.style", expectedRevision: 0));

        CovenantCurationReceipt unpinned = await ApplyAsync(
            fixture,
            CovenantCurationFixture.Unpin(CovenantOperationScope.Global, "global.style", expectedRevision: 1));

        Assert.Equal(2L, unpinned.ResultingRevision);

        Assert.False(unpinned.ResultingState.IsPinned);

        Assert.Equal(2, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_curation_versions;"));

        // History is a chain, not a set: the second revision names the first.
        Assert.Equal(
            pinned.ResultingVersionId!.Value.ToString("D"),
            await TextAsync(
                fixture,
                "SELECT PredecessorVersionId FROM covenant_curation_versions WHERE Revision = 2;"));

    }

    [Fact]
    public async Task The_same_change_applied_twice_replays_its_first_receipt_and_appends_nothing()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantCurationIntent intent =
            CovenantCurationFixture.Pin(CovenantOperationScope.Global, "global.style", expectedRevision: 0);

        CovenantCurationReceipt first = await ApplyAsync(fixture, intent);

        CovenantCurationReceipt replayed = await ApplyAsync(fixture, intent);

        Assert.False(first.Replayed);

        Assert.True(replayed.Replayed);

        Assert.Equal(first.ResultingVersionId, replayed.ResultingVersionId);

        Assert.Equal(first.ResultingRevision, replayed.ResultingRevision);

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_curation_versions;"));

    }

    /// <summary>
    /// Pinning what is already pinned is a deliberate no-op, and it still writes a receipt. Recording
    /// only the changes that changed something would make a replay of the no-op indistinguishable from
    /// a request that never arrived.
    /// </summary>
    [Fact]
    public async Task Pinning_an_already_pinned_subject_reports_no_change_and_still_writes_a_receipt()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        _ = await ApplyAsync(
            fixture,
            CovenantCurationFixture.Pin(CovenantOperationScope.Global, "global.style", expectedRevision: 0));

        CovenantCurationReceipt again = await ApplyAsync(
            fixture,
            CovenantCurationFixture.Pin(CovenantOperationScope.Global, "global.style", expectedRevision: 1));

        Assert.Equal(CovenantMutationOutcome.NoChange, again.Outcome);

        Assert.Null(again.ResultingVersionId);

        Assert.True(again.ResultingState.IsPinned);

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_curation_versions;"));

        Assert.Equal(2, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_curation_receipts;"));

    }

    [Fact]
    public async Task A_change_whose_expected_revision_disagrees_with_the_head_is_refused()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        _ = await ApplyAsync(
            fixture,
            CovenantCurationFixture.Pin(CovenantOperationScope.Global, "global.style", expectedRevision: 0));

        Result<CovenantCurationReceipt> refused = await TryApplyAsync(
            fixture,
            CovenantCurationFixture.Unpin(CovenantOperationScope.Global, "global.style", expectedRevision: 0));

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.RevisionConflict, refused.Error.Code);

    }

    [Fact]
    public async Task Reusing_a_change_identity_with_different_input_is_an_idempotency_conflict()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid identity = Guid.CreateVersion7();

        _ = await ApplyAsync(
            fixture,
            CovenantCurationFixture.Pin(CovenantOperationScope.Global, "global.style", 0, identity));

        Result<CovenantCurationReceipt> refused = await TryApplyAsync(
            fixture,
            CovenantCurationFixture.Pin(CovenantOperationScope.Global, "global.other", 0, identity));

        Assert.True(refused.IsFailure);

        Assert.Equal("Security.IdempotencyConflict", refused.Error.Code);

    }

    [Fact]
    public async Task A_mask_records_that_the_Global_key_stops_applying_in_that_Campaign()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "Campaign One", Token);

        CovenantCurationReceipt receipt = await ApplyAsync(
            fixture,
            CovenantCurationFixture.Mask(CampaignOne, "global.style", expectedRevision: 0));

        Assert.True(receipt.ResultingState.IsMasked);

        Assert.False(receipt.ResultingState.IsPinned);

        // The mask names a key this Campaign holds nothing for. That is the whole point of a subject
        // that is a scoped key rather than an entry identity.
        Assert.Equal(0, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_entries;"));

    }

    [Fact]
    public void A_mask_on_the_Global_scope_cannot_be_constructed_at_all()
    {

        ArgumentException refused = Assert.Throws<ArgumentException>(
            () => CovenantCurationFixture.Mask(campaignId: null, "global.style", expectedRevision: 0));

        Assert.Equal("subject", refused.ParamName);

    }

    /// <summary>
    /// A key that was retired, reclaimed, and re-created is a different key wearing an old name. The
    /// epoch the subject binds is what stops an earlier pin applying to it.
    /// </summary>
    [Fact]
    public async Task A_change_bound_to_a_superseded_key_epoch_is_refused()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Result<CovenantCurationReceipt> refused = await TryApplyAsync(
            fixture,
            CovenantCurationFixture.Pin(
                CovenantOperationScope.Global,
                "global.style",
                expectedRevision: 0,
                keyEpoch: 7));

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    [Fact]
    public async Task A_change_prepared_against_another_dataset_generation_is_refused()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Result<CovenantCurationReceipt> refused = await CovenantCurationFixture.ApplyAsync(
            fixture,
            new CovenantCurationCommit(
                Guid.CreateVersion7(),
                1,
                CovenantMutationFixture.CommitTime,
                CovenantCurationFixture.Pin(CovenantOperationScope.Global, "global.style", 0)),
            Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    private static async Task<CovenantCurationReceipt> ApplyAsync(
        CovenantCanonicalFixture fixture,
        CovenantCurationIntent intent)
    {

        Result<CovenantCurationReceipt> applied = await TryApplyAsync(fixture, intent);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        return applied.Value;

    }

    private static async Task<Result<CovenantCurationReceipt>> TryApplyAsync(
        CovenantCanonicalFixture fixture,
        CovenantCurationIntent intent) =>
        await CovenantCurationFixture.ApplyAsync(
            fixture,
            new CovenantCurationCommit(
                await fixture.ReadDatasetGenerationAsync(Token),
                1,
                CovenantMutationFixture.CommitTime,
                intent),
            Token);

    private static async Task<long> ScalarAsync(CovenantCanonicalFixture fixture, string sql)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);

    }

    private static async Task<string?> TextAsync(CovenantCanonicalFixture fixture, string sql)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(Token);

        return value is DBNull or null ? null : (string)value;

    }

}
