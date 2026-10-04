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
    /// A replay reports the subject's curation as it stands now. The receipt stores the dependency
    /// epoch its request bound, which is not the epoch a head is recorded under, so the state has to be
    /// found through the key's binding epoch.
    /// </summary>
    [Fact]
    public async Task A_replay_reports_the_subjects_current_state_through_the_binding_epoch()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await OperatorSetAsync(fixture, "global.style", expectedRevision: 0, expectedKeyEpoch: 0);

        CovenantCurationIntent intent = CovenantCurationFixture.Pin(
            CovenantOperationScope.Global,
            "global.style",
            expectedRevision: 0,
            keyEpoch: 1);

        CovenantCurationReceipt first = await ApplyAsync(fixture, intent);

        // The key moves on. The stored receipt still says dependency epoch 1.
        await OperatorSetAsync(fixture, "global.style", expectedRevision: 1, expectedKeyEpoch: 1, "Be direct.");

        CovenantCurationReceipt replayed = await ApplyAsync(fixture, intent);

        Assert.True(replayed.Replayed);

        Assert.Equal(first.ResultingVersionId, replayed.ResultingVersionId);

        Assert.True(replayed.ResultingState.IsPinned);

        Assert.Equal(1L, replayed.ResultingState.Revision);

        Assert.Equal(0L, replayed.Subject.KeyBindingEpoch);

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

        await AssertNoCurationRowsAsync(fixture);

    }

    /// <summary>
    /// The dependency epoch is what a prepared change is compared against. A change measured before
    /// some other write to the key is refused rather than applied to a key that has moved on, and the
    /// refusal leaves nothing behind.
    /// </summary>
    [Fact]
    public async Task A_change_prepared_before_another_write_to_the_key_is_refused_and_writes_nothing()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await OperatorSetAsync(fixture, "global.style", expectedRevision: 0, expectedKeyEpoch: 0);

        CovenantCurationIntent prepared = CovenantCurationFixture.Pin(
            CovenantOperationScope.Global,
            "global.style",
            expectedRevision: 0,
            keyEpoch: 1);

        await OperatorSetAsync(fixture, "global.style", expectedRevision: 1, expectedKeyEpoch: 1, "Be direct.");

        Result<CovenantCurationReceipt> refused = await TryApplyAsync(fixture, prepared);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

        await AssertNoCurationRowsAsync(fixture);

    }

    /// <summary>
    /// The two epochs a curation change touches, each in the column that means it.
    /// </summary>
    /// <remarks>
    /// The head and its version record the binding epoch, which is what every pin and mask read joins
    /// on and what no ordinary write moves. The receipt records the dependency epoch, because that is
    /// the value the request digest bound and a replay has to recompute it from.
    /// </remarks>
    [Fact]
    public async Task A_curation_row_binds_the_binding_epoch_and_its_receipt_the_dependency_epoch()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await OperatorSetAsync(fixture, "global.style", expectedRevision: 0, expectedKeyEpoch: 0);

        Assert.Equal(
            1,
            await ScalarAsync(fixture, "SELECT KeyEpoch FROM covenant_key_epochs WHERE NormalizedKey = 'global.style';"));

        Assert.Equal(
            0,
            await ScalarAsync(
                fixture,
                "SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = 'global.style';"));

        CovenantCurationReceipt receipt = await ApplyAsync(
            fixture,
            CovenantCurationFixture.Pin(
                CovenantOperationScope.Global,
                "global.style",
                expectedRevision: 0,
                keyEpoch: 1));

        Assert.Equal(0, await ScalarAsync(fixture, "SELECT KeyEpoch FROM covenant_curation_heads;"));

        Assert.Equal(0, await ScalarAsync(fixture, "SELECT KeyEpoch FROM covenant_curation_versions;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT KeyEpoch FROM covenant_curation_receipts;"));

        Assert.Equal(1L, receipt.Subject.KeyDependencyEpoch);

        Assert.Equal(0L, receipt.Subject.KeyBindingEpoch);

    }

    /// <summary>
    /// A key that existed before canonical version 6 kept its epoch as its binding epoch, so the value
    /// a curation row records there is the key row's own and not a constant.
    /// </summary>
    [Fact]
    public async Task A_curation_row_for_an_upgraded_key_binds_that_keys_nonzero_binding_epoch()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        // The row the version-6 backfill leaves for a key that already had three head changes.
        await ExecuteAsync(
            fixture,
            """
            INSERT INTO covenant_key_epochs (NormalizedKey, KeyEpoch, UpdatedAtUtc, IncarnationEpoch)
            VALUES ('global.style', 3, '2026-01-01T00:00:00.0000000Z', 3);
            """);

        await OperatorSetAsync(fixture, "global.style", expectedRevision: 0, expectedKeyEpoch: 3);

        CovenantCurationReceipt receipt = await ApplyAsync(
            fixture,
            CovenantCurationFixture.Pin(
                CovenantOperationScope.Global,
                "global.style",
                expectedRevision: 0,
                keyEpoch: 4));

        Assert.Equal(3, await ScalarAsync(fixture, "SELECT KeyEpoch FROM covenant_curation_heads;"));

        Assert.Equal(3, await ScalarAsync(fixture, "SELECT KeyEpoch FROM covenant_curation_versions;"));

        Assert.Equal(4, await ScalarAsync(fixture, "SELECT KeyEpoch FROM covenant_curation_receipts;"));

        Assert.Equal(3L, receipt.Subject.KeyBindingEpoch);

        // The same head is the one a later change compares and swaps against, across another write.
        await OperatorSetAsync(fixture, "global.style", expectedRevision: 1, expectedKeyEpoch: 4, "Be direct.");

        CovenantCurationReceipt unpinned = await ApplyAsync(
            fixture,
            CovenantCurationFixture.Unpin(
                CovenantOperationScope.Global,
                "global.style",
                expectedRevision: 1,
                keyEpoch: 5));

        Assert.Equal(2L, unpinned.ResultingRevision);

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_curation_heads;"));

        Assert.Equal(3, await ScalarAsync(fixture, "SELECT KeyEpoch FROM covenant_curation_heads;"));

    }

    /// <summary>
    /// A caller that read the binding epoch itself states it, and the kernel refuses when the key row
    /// it finds under the write lock disagrees, before anything is written.
    /// </summary>
    [Fact]
    public async Task A_change_whose_asserted_binding_epoch_disagrees_is_refused()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await OperatorSetAsync(fixture, "global.style", expectedRevision: 0, expectedKeyEpoch: 0);

        Result<CovenantCurationReceipt> refused = await TryApplyAsync(
            fixture,
            CovenantCurationFixture.Pin(
                CovenantOperationScope.Global,
                "global.style",
                expectedRevision: 0,
                keyEpoch: 1,
                keyBindingEpoch: 1));

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

        await AssertNoCurationRowsAsync(fixture);

        // The same change asserting the epoch the key row holds is the one that commits.
        CovenantCurationReceipt applied = await ApplyAsync(
            fixture,
            CovenantCurationFixture.Pin(
                CovenantOperationScope.Global,
                "global.style",
                expectedRevision: 0,
                keyEpoch: 1,
                keyBindingEpoch: 0));

        Assert.Equal(0L, applied.Subject.KeyBindingEpoch);

    }

    [Fact]
    public void A_negative_binding_epoch_cannot_be_constructed_at_all()
    {

        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
            () => CovenantCurationFixture.Pin(
                CovenantOperationScope.Global,
                "global.style",
                expectedRevision: 0,
                keyBindingEpoch: -1));

        Assert.Equal("subject", refused.ParamName);

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

    /// <summary>Commits one operator write of a Global key through the production mutation kernel.</summary>
    private static async Task OperatorSetAsync(
        CovenantCanonicalFixture fixture,
        string key,
        long expectedRevision,
        long expectedKeyEpoch,
        string content = "Be brief.")
    {

        Result<IReadOnlyList<CovenantMutationReceipt>> written = await CovenantMutationFixture.ApplyAsync(
            fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                fixture,
                Token,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    key,
                    content,
                    expectedRevision,
                    expectedKeyEpoch)),
            Token);

        Assert.True(written.IsSuccess, written.IsFailure ? written.Error.Message : string.Empty);

    }

    private static async Task AssertNoCurationRowsAsync(CovenantCanonicalFixture fixture)
    {

        Assert.Equal(0, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_curation_versions;"));

        Assert.Equal(0, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_curation_heads;"));

        Assert.Equal(0, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_curation_receipts;"));

    }

    private static async Task ExecuteAsync(CovenantCanonicalFixture fixture, string sql)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);

    }

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
