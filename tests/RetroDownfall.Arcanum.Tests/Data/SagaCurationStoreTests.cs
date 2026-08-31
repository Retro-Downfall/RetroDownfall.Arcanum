using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class SagaCurationStoreTests
{

    [SkippableFact]
    public async Task A_freshly_written_memory_reads_back_active_unpinned_and_embedded()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1",
            "the operator prefers tabs",
            DateTimeOffset.UtcNow,
            sessionId: null,
            tags: null,
            source: null,
            harness.Embedding(),
            CancellationToken.None).ConfigureAwait(false);

        SagaMemoryCurationRow? row = await harness.Store
            .ReadCurationRowAsync("m-1", CancellationToken.None)
            .ConfigureAwait(false);

        Assert.NotNull(row);

        Assert.Null(row.Lifecycle.RetiredAtUtc);

        Assert.Null(row.Lifecycle.PinnedAtUtc);

        Assert.True(row.HasEmbedding);

    }

    [SkippableFact]
    public async Task An_unknown_identity_reads_back_as_nothing_rather_than_as_an_error()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        Assert.Null(await harness.Store
            .ReadCurationRowAsync("m-absent", CancellationToken.None)
            .ConfigureAwait(false));

    }

    [SkippableFact]
    public async Task Retirement_removes_both_embedding_rows_and_leaves_the_memory_inspectable()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationOutcome outcome = await harness.Store.RetireAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.Applied, outcome.Kind);

        SagaMemoryCurationRow row = (await harness.Store
            .ReadCurationRowAsync("m-1", CancellationToken.None).ConfigureAwait(false))!;

        // The memory is still there to read. What is gone is the only thing retrieval can reach it by.
        Assert.Equal("the operator prefers tabs", row.Memory.Content);

        Assert.NotNull(row.Lifecycle.RetiredAtUtc);

        Assert.False(row.HasEmbedding);

        Assert.Equal(0, await harness.CountAsync("saga_memory_embeddings", "MemoryId = 'm-1'").ConfigureAwait(false));

    }

    [SkippableFact]
    public async Task Retirement_refuses_content_the_caller_did_not_read()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationOutcome outcome = await harness.Store.RetireAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("something else entirely"),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.StaleContent, outcome.Kind);

        Assert.True((await harness.Store
            .ReadCurationRowAsync("m-1", CancellationToken.None).ConfigureAwait(false))!.HasEmbedding);

    }

    [SkippableFact]
    public async Task Retiring_twice_is_refused_rather_than_recorded_twice()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        byte[] digest = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

        SagaCurationOutcome first = await harness.Store
            .RetireAsync("m-1", digest, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.Applied, first.Kind);

        AnnalClaimHead headBeforeSecond = (await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None).ConfigureAwait(false))!;

        Assert.NotNull(headBeforeSecond);

        int versionCountBeforeSecond = (await harness.Annals
            .GetVersionsAsync(headBeforeSecond.ClaimId, CancellationToken.None).ConfigureAwait(false)).Count;

        SagaCurationOutcome second = await harness.Store
            .RetireAsync("m-1", digest, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.AlreadyRetired, second.Kind);

        AnnalClaimHead headAfterSecond = (await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None).ConfigureAwait(false))!;

        Assert.NotNull(headAfterSecond);

        Assert.Equal(headBeforeSecond.CurrentVersionId, headAfterSecond.CurrentVersionId);

        Assert.Equal(headBeforeSecond.CurrentRevision, headAfterSecond.CurrentRevision);

        Assert.Equal(
            versionCountBeforeSecond,
            (await harness.Annals.GetVersionsAsync(headAfterSecond.ClaimId, CancellationToken.None)
                .ConfigureAwait(false)).Count);

    }

    [SkippableFact]
    public async Task Reinstatement_restores_the_embedding_and_releases_the_suppression()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        byte[] digest = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

        _ = await harness.Store.RetireAsync("m-1", digest, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(1, await harness.CountAsync("saga_retirement_suppressions", "1 = 1").ConfigureAwait(false));

        SagaCurationOutcome outcome = await harness.Store.ReinstateAsync(
            "m-1", digest, harness.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.Applied, outcome.Kind);

        SagaMemoryCurationRow row = (await harness.Store
            .ReadCurationRowAsync("m-1", CancellationToken.None).ConfigureAwait(false))!;

        Assert.Null(row.Lifecycle.RetiredAtUtc);

        Assert.True(row.HasEmbedding);

        Assert.Equal(0, await harness.CountAsync("saga_retirement_suppressions", "1 = 1").ConfigureAwait(false));

    }

    [SkippableFact]
    public async Task Reinstating_a_live_memory_is_refused()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationOutcome outcome = await harness.Store.ReinstateAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            harness.Embedding(),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.NotRetired, outcome.Kind);

    }

    [SkippableFact]
    public async Task Reinstating_twice_is_refused_without_advancing_annals_history()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: false)
            .ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(seed: 1), CancellationToken.None).ConfigureAwait(false);

        byte[] digest = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

        _ = await harness.Store.RetireAsync("m-1", digest, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        SagaCurationOutcome first = await harness.Store.ReinstateAsync(
            "m-1", digest, harness.Embedding(seed: 2), DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.Applied, first.Kind);

        AnnalClaimHead headBeforeSecond = (await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None).ConfigureAwait(false))!;

        Assert.NotNull(headBeforeSecond);

        int versionCountBeforeSecond = (await harness.Annals
            .GetVersionsAsync(headBeforeSecond.ClaimId, CancellationToken.None).ConfigureAwait(false)).Count;

        SagaCurationOutcome second = await harness.Store.ReinstateAsync(
            "m-1", digest, harness.Embedding(seed: 9), DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.NotRetired, second.Kind);

        AnnalClaimHead headAfterSecond = (await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None).ConfigureAwait(false))!;

        Assert.NotNull(headAfterSecond);

        Assert.Equal(headBeforeSecond.CurrentVersionId, headAfterSecond.CurrentVersionId);

        Assert.Equal(headBeforeSecond.CurrentRevision, headAfterSecond.CurrentRevision);

        Assert.Equal(
            versionCountBeforeSecond,
            (await harness.Annals.GetVersionsAsync(headAfterSecond.ClaimId, CancellationToken.None)
                .ConfigureAwait(false)).Count);

    }

    /// <summary>
    /// Two memories with identical content in one scope hash to the same suppression digest.
    /// <c>ON CONFLICT(SuppressionDigest) DO NOTHING</c> is what keeps the second retirement from
    /// aborting on the first's row: the conflict target names the digest specifically, so a duplicate
    /// digest is tolerated while a malformed row still aborts the transaction -- the distinction a bare
    /// <c>INSERT OR IGNORE</c> would not draw.
    /// </summary>
    [SkippableFact]
    public async Task Retiring_two_memories_that_share_content_and_scope_produces_one_suppression()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(seed: 1), CancellationToken.None).ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-2", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(seed: 2), CancellationToken.None).ConfigureAwait(false);

        byte[] digest = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

        SagaCurationOutcome first = await harness.Store
            .RetireAsync("m-1", digest, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);

        SagaCurationOutcome second = await harness.Store
            .RetireAsync("m-2", digest, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.Applied, first.Kind);

        Assert.Equal(SagaCurationOutcomeKind.Applied, second.Kind);

        Assert.Equal(1, await harness.CountAsync("saga_retirement_suppressions", "1 = 1").ConfigureAwait(false));

    }

    /// <summary>
    /// Retirement and reinstatement write Annals history whatever the feature flag says, because the
    /// record that the operator ended (or restored) a memory is evidence rather than retrieval. The
    /// harness's default off flag is what makes this assertion also prove the two writes are ungated.
    /// </summary>
    [SkippableFact]
    public async Task Retirement_and_reinstatement_write_annals_history_even_when_the_feature_is_off()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: false)
            .ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        byte[] digest = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

        _ = await harness.Store.RetireAsync("m-1", digest, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        AnnalClaimHead? afterRetire = await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None).ConfigureAwait(false);

        Assert.NotNull(afterRetire);

        Assert.Equal(AnnalOperation.Retire, afterRetire.CurrentOperation);

        Assert.Equal(2, afterRetire.CurrentRevision);

        IReadOnlyList<AnnalClaimVersion> versionsAfterRetire = await harness.Annals
            .GetVersionsAsync(afterRetire.ClaimId, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(2, versionsAfterRetire.Count);

        Assert.Equal(AnnalOperation.Assert, versionsAfterRetire[0].Operation);

        Assert.Equal(AnnalOrigin.AgentExtracted, versionsAfterRetire[0].Origin);

        Assert.Equal(AnnalOperation.Retire, versionsAfterRetire[1].Operation);

        Assert.Equal(AnnalOrigin.OperatorStated, versionsAfterRetire[1].Origin);

        _ = await harness.Store.ReinstateAsync(
            "m-1", digest, harness.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        AnnalClaimHead? afterReinstate = await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None).ConfigureAwait(false);

        Assert.NotNull(afterReinstate);

        Assert.Equal(AnnalOperation.Correct, afterReinstate.CurrentOperation);

        Assert.Equal(3, afterReinstate.CurrentRevision);

    }

    /// <summary>
    /// The same convergence as the flag-off history test, with the Annals turned on: <c>InsertAsync</c>
    /// has already opened the claim (revision one, Assert, AgentExtracted) before <c>RetireAsync</c> ever
    /// runs, so its own <c>AppendAssertAsync</c> call no-ops against that existing head rather than
    /// writing a second one. Both flag states therefore have to land on the same two-revision shape --
    /// this pins that they provably do, rather than duplicating the flag-off assertions against a
    /// different setup.
    /// </summary>
    [SkippableFact]
    public async Task Retirement_and_reinstatement_write_the_same_annals_history_when_the_feature_is_on()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        byte[] digest = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

        _ = await harness.Store.RetireAsync("m-1", digest, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        AnnalClaimHead? afterRetire = await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None).ConfigureAwait(false);

        Assert.NotNull(afterRetire);

        Assert.Equal(AnnalOperation.Retire, afterRetire.CurrentOperation);

        Assert.Equal(2, afterRetire.CurrentRevision);

        IReadOnlyList<AnnalClaimVersion> versionsAfterRetire = await harness.Annals
            .GetVersionsAsync(afterRetire.ClaimId, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(2, versionsAfterRetire.Count);

        Assert.Equal(AnnalOperation.Assert, versionsAfterRetire[0].Operation);

        Assert.Equal(AnnalOrigin.AgentExtracted, versionsAfterRetire[0].Origin);

        Assert.Equal(AnnalOperation.Retire, versionsAfterRetire[1].Operation);

        Assert.Equal(AnnalOrigin.OperatorStated, versionsAfterRetire[1].Origin);

        _ = await harness.Store.ReinstateAsync(
            "m-1", digest, harness.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        AnnalClaimHead? afterReinstate = await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None).ConfigureAwait(false);

        Assert.NotNull(afterReinstate);

        Assert.Equal(AnnalOperation.Correct, afterReinstate.CurrentOperation);

        Assert.Equal(3, afterReinstate.CurrentRevision);

    }

    [SkippableFact]
    public async Task Retiring_an_unknown_identity_is_refused_and_writes_nothing()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        SagaCurationOutcome outcome = await harness.Store.RetireAsync(
            "m-absent",
            AnnalContentDigest.ForSagaMemory("anything"),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.NotFound, outcome.Kind);

        Assert.Equal(0, await harness.CountAsync("saga_retirement_suppressions", "1 = 1").ConfigureAwait(false));

        Assert.Null(await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-absent", CancellationToken.None).ConfigureAwait(false));

    }

    [SkippableFact]
    public async Task Reinstating_an_unknown_identity_is_refused_and_writes_nothing()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        SagaCurationOutcome outcome = await harness.Store.ReinstateAsync(
            "m-absent",
            AnnalContentDigest.ForSagaMemory("anything"),
            harness.Embedding(),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.NotFound, outcome.Kind);

        Assert.Equal(0, await harness.CountAsync("saga_retirement_suppressions", "1 = 1").ConfigureAwait(false));

        Assert.Null(await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-absent", CancellationToken.None).ConfigureAwait(false));

    }

    [SkippableFact]
    public async Task Correction_replaces_the_content_and_the_vector_together()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(seed: 1), CancellationToken.None).ConfigureAwait(false);

        byte[] before = await harness.EmbeddingBytesAsync("m-1").ConfigureAwait(false);

        SagaCurationOutcome outcome = await harness.Store.CorrectAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            "the operator prefers spaces",
            harness.Embedding(seed: 2),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.Applied, outcome.Kind);

        SagaMemoryCurationRow row = (await harness.Store
            .ReadCurationRowAsync("m-1", CancellationToken.None).ConfigureAwait(false))!;

        Assert.Equal("the operator prefers spaces", row.Memory.Content);

        // The vector moved with the text. A correction that changed one without the other would leave
        // retrieval surfacing the sentence the operator just rejected.
        Assert.NotEqual(before, await harness.EmbeddingBytesAsync("m-1").ConfigureAwait(false));

    }

    [SkippableFact]
    public async Task Correction_refuses_content_the_caller_did_not_read()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationOutcome outcome = await harness.Store.CorrectAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("something else entirely"),
            "the operator prefers spaces",
            harness.Embedding(),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.StaleContent, outcome.Kind);

        Assert.Equal(
            "the operator prefers tabs",
            (await harness.Store.ReadCurationRowAsync("m-1", CancellationToken.None)
                .ConfigureAwait(false))!.Memory.Content);

    }

    [SkippableFact]
    public async Task Correcting_a_retired_memory_is_refused()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        byte[] digest = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

        _ = await harness.Store.RetireAsync("m-1", digest, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        SagaCurationOutcome outcome = await harness.Store.CorrectAsync(
            "m-1", digest, "the operator prefers spaces", harness.Embedding(),
            DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.AlreadyRetired, outcome.Kind);

    }

    /// <summary>
    /// Correcting to the text already stored reports <see cref="SagaCurationOutcomeKind.Unchanged"/> and
    /// writes nothing at all -- proven here by reading the content, the embedding bytes, and the claim's
    /// revision count back rather than trusting the outcome kind. The service layer (not this store)
    /// is what decides whether <c>Unchanged</c> is a success or a failure; at this layer "nothing
    /// needed doing" and "nothing was written" are the whole of the contract.
    /// </summary>
    [SkippableFact]
    public async Task Correcting_to_the_text_already_stored_writes_nothing()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true).ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(seed: 1), CancellationToken.None).ConfigureAwait(false);

        byte[] embeddingBefore = await harness.EmbeddingBytesAsync("m-1").ConfigureAwait(false);

        AnnalClaimHead claimBefore = (await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None).ConfigureAwait(false))!;

        Assert.NotNull(claimBefore);

        int revisionsBefore = (await harness.Annals
            .GetVersionsAsync(claimBefore.ClaimId, CancellationToken.None).ConfigureAwait(false)).Count;

        // seed: 7 is deliberately not seed: 1 -- a store that wrote this embedding anyway would move
        // the bytes read back below, which is what makes that assertion able to fail.
        SagaCurationOutcome outcome = await harness.Store.CorrectAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            "the operator prefers tabs",
            harness.Embedding(seed: 7),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.Unchanged, outcome.Kind);

        Assert.Equal(
            "the operator prefers tabs",
            (await harness.Store.ReadCurationRowAsync("m-1", CancellationToken.None)
                .ConfigureAwait(false))!.Memory.Content);

        Assert.Equal(embeddingBefore, await harness.EmbeddingBytesAsync("m-1").ConfigureAwait(false));

        // The vec0 mirror is not asserted here: WeaveIndexAvailability.IsVecAvailable is permanently
        // false on this hermetic build (SQLite is compiled with SQLITE_OMIT_LOAD_EXTENSION, per that
        // type's own doc comment), so SagaStoreHarness never exercises CorrectAsync's
        // "if (availability.IsVecAvailable)" branch and no test built on this harness -- including the
        // pre-existing Correction_replaces_the_content_and_the_vector_together -- writes to
        // saga_memory_embeddings_vec at all. There is no build reachable from this harness where the
        // omission could be filled in.
        AnnalClaimHead claimAfter = (await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None).ConfigureAwait(false))!;

        Assert.NotNull(claimAfter);

        Assert.Equal(
            revisionsBefore,
            (await harness.Annals.GetVersionsAsync(claimAfter.ClaimId, CancellationToken.None)
                .ConfigureAwait(false)).Count);

    }

    [SkippableFact]
    public async Task Correcting_claimless_identical_text_with_annals_off_writes_nothing()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: false)
            .ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(seed: 1), CancellationToken.None).ConfigureAwait(false);

        byte[] embeddingBefore = await harness.EmbeddingBytesAsync("m-1").ConfigureAwait(false);

        Assert.Null(await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None).ConfigureAwait(false));

        SagaCurationOutcome outcome = await harness.Store.CorrectAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            "the operator prefers tabs",
            harness.Embedding(seed: 7),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.Unchanged, outcome.Kind);

        Assert.Equal(
            "the operator prefers tabs",
            (await harness.Store.ReadCurationRowAsync("m-1", CancellationToken.None)
                .ConfigureAwait(false))!.Memory.Content);

        Assert.Equal(embeddingBefore, await harness.EmbeddingBytesAsync("m-1").ConfigureAwait(false));

        Assert.Null(await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None).ConfigureAwait(false));

    }

    [SkippableFact]
    public async Task A_required_annals_failure_rolls_back_a_correction_when_the_feature_is_off()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: false)
            .ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(seed: 1), CancellationToken.None).ConfigureAwait(false);

        byte[] embeddingBefore = await harness.EmbeddingBytesAsync("m-1").ConfigureAwait(false);

        await using (SqliteCommand trigger = (SqliteCommand)harness.Connection.CreateCommand())
        {

            trigger.CommandText =
                """
                CREATE TEMP TRIGGER fail_curation_annals
                BEFORE INSERT ON main.annal_claims
                BEGIN
                    SELECT RAISE(ABORT, 'forced curation Annals failure');
                END;
                """;

            _ = await trigger.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);

        }

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(
            () => harness.Store.CorrectAsync(
                "m-1",
                AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
                "the operator prefers spaces",
                harness.Embedding(seed: 2),
                DateTimeOffset.UtcNow,
                CancellationToken.None));

        Assert.Contains("forced curation Annals failure", exception.Message, StringComparison.Ordinal);

        Assert.Equal(
            "the operator prefers tabs",
            (await harness.Store.ReadCurationRowAsync("m-1", CancellationToken.None)
                .ConfigureAwait(false))!.Memory.Content);

        Assert.Equal(embeddingBefore, await harness.EmbeddingBytesAsync("m-1").ConfigureAwait(false));

        Assert.Null(await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None).ConfigureAwait(false));

        Assert.Equal(0, await harness.CountAsync("annal_versions", "1 = 1").ConfigureAwait(false));

    }

    [SkippableFact]
    public async Task Correction_records_the_operator_as_the_author_and_extraction_as_the_asserter()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true).ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        _ = await harness.Store.CorrectAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            "the operator prefers spaces",
            harness.Embedding(),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        AnnalClaimHead head = (await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None)
            .ConfigureAwait(false))!;

        IReadOnlyList<AnnalClaimVersion> history = await harness.Annals
            .GetVersionsAsync(head.ClaimId, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(AnnalOrigin.AgentExtracted, history[0].Origin);

        Assert.Equal(AnnalOperation.Correct, history[1].Operation);

        Assert.Equal(AnnalOrigin.OperatorStated, history[1].Origin);

    }

    [SkippableFact]
    public async Task Correction_leaves_the_memory_formation_time_and_its_sensitivity_label_alone()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        DateTimeOffset formed = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Guid id = Guid.NewGuid();

        await harness.Store.InsertAsync(
            id.ToString(), "the operator prefers tabs", formed,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        await harness.LabelSensitiveAsync(id).ConfigureAwait(false);

        _ = await harness.Store.CorrectAsync(
            id.ToString(),
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            "the operator prefers spaces",
            harness.Embedding(),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        SagaMemoryCurationRow row = (await harness.Store
            .ReadCurationRowAsync(id.ToString(), CancellationToken.None).ConfigureAwait(false))!;

        // The memory was formed then; the Annals records when it was corrected.
        Assert.Equal(formed, row.Memory.CreatedAt);

        // The label stays. Removing it would be the fail-open direction: the operator's own text is not
        // Covenant-derived, but a label that over-reaches is safe and one that under-reaches is not.
        //
        // UPPER() on both sides because the ledger canonicalizes a Guid's text as uppercase
        // (ArtifactSensitivityLedger.Format), while saga_memories.Id is free-form text carrying
        // whatever case the caller inserted it with -- here, Guid.ToString()'s default lowercase.
        // This is not a harmless idiom: CovenantProtectedArtifactErasureKernel binds every Saga purge
        // statement's $artifactId through that same Format, so its saga_memory_embeddings,
        // saga_memory_attachment_provenance, and saga_memories deletes -- bound against lowercase ids
        // written by SagaExtractionService under SQLite's default BINARY collation -- silently match
        // nothing, while its artifact_sensitivity delete (both sides Format'd) succeeds and reports
        // the erasure as complete. That is a live pre-existing defect in the erasure kernel, tracked
        // separately; this fold exists only so this test can drive the real ledger rather than
        // seeding a row, and must not be read as evidence the mismatch is harmless elsewhere.
        Assert.Equal(
            1,
            await harness.CountAsync("artifact_sensitivity", $"UPPER(ArtifactId) = UPPER('{id}')").ConfigureAwait(false));

    }

    /// <summary>
    /// The same convergence <c>RetireAsync</c>'s and <c>ReinstateAsync</c>'s ungated-history tests pin:
    /// the record that the operator corrected this memory is evidence rather than retrieval, so it has
    /// to land whatever <c>Arcanum:Features:Annals</c> says. Without this, a <c>CorrectAsync</c> that
    /// wrapped its Annals pair in the feature check would still pass every other test in this file,
    /// because the sibling test below runs with the flag on, where <c>InsertAsync</c> has already
    /// opened the claim and the flag's effect is invisible.
    /// </summary>
    [SkippableFact]
    public async Task Correction_writes_annals_history_even_when_the_feature_is_off()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: false)
            .ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        _ = await harness.Store.CorrectAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            "the operator prefers spaces",
            harness.Embedding(),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        AnnalClaimHead head = (await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None)
            .ConfigureAwait(false))!;

        Assert.Equal(AnnalOperation.Correct, head.CurrentOperation);

        Assert.Equal(2, head.CurrentRevision);

        IReadOnlyList<AnnalClaimVersion> history = await harness.Annals
            .GetVersionsAsync(head.ClaimId, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(2, history.Count);

        Assert.Equal(AnnalOperation.Assert, history[0].Operation);

        Assert.Equal(AnnalOrigin.AgentExtracted, history[0].Origin);

        Assert.Equal(AnnalOperation.Correct, history[1].Operation);

        Assert.Equal(AnnalOrigin.OperatorStated, history[1].Origin);

    }

    /// <summary>
    /// A memory's attachment provenance names where it came from, independent of what its text says
    /// now. A correction that dropped that row on the way through would leave a still-embedded memory
    /// with no record it was ever attachment-derived -- silently, since nothing about correcting text
    /// looks like it should touch provenance at all.
    /// </summary>
    [SkippableFact]
    public async Task Correction_leaves_attachment_provenance_rows_untouched()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        Guid sessionId = Guid.NewGuid();

        Guid attachmentId = Guid.NewGuid();

        AttachmentMemoryProvenance provenance = new(
            sessionId,
            attachmentId,
            "architecture",
            1,
            "attachment-hash",
            DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            "SessionAttachmentRag",
            AttachmentSourceAvailability.Available);

        await harness.Store.InsertAsync(
            "m-1",
            "the operator prefers tabs",
            DateTimeOffset.UtcNow,
            sessionId,
            tags: null,
            source: "extraction",
            harness.Embedding(),
            provenance,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(1, await harness.CountAsync("saga_memory_attachment_provenance", "MemoryId = 'm-1'").ConfigureAwait(false));

        _ = await harness.Store.CorrectAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            "the operator prefers spaces",
            harness.Embedding(),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(1, await harness.CountAsync("saga_memory_attachment_provenance", "MemoryId = 'm-1'").ConfigureAwait(false));

    }

    [SkippableFact]
    public async Task A_pin_is_recorded_and_released_without_touching_the_memory()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(
            SagaCurationOutcomeKind.Applied,
            (await harness.Store.SetPinAsync("m-1", true, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false)).Kind);

        Assert.NotNull((await harness.Store
            .ReadCurationRowAsync("m-1", CancellationToken.None).ConfigureAwait(false))!.Lifecycle.PinnedAtUtc);

        Assert.Equal(
            SagaCurationOutcomeKind.Applied,
            (await harness.Store.SetPinAsync("m-1", false, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false)).Kind);

        Assert.Null((await harness.Store
            .ReadCurationRowAsync("m-1", CancellationToken.None).ConfigureAwait(false))!.Lifecycle.PinnedAtUtc);

    }

    [SkippableFact]
    public async Task Pinning_an_unknown_identity_is_refused_and_writes_nothing()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        SagaCurationOutcome outcome = await harness.Store
            .SetPinAsync("m-absent", true, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.NotFound, outcome.Kind);

        Assert.Null(outcome.Lifecycle);

        Assert.Null(await harness.Store.ReadCurationRowAsync("m-absent", CancellationToken.None).ConfigureAwait(false));

    }

    [SkippableFact]
    public async Task A_pinned_memory_can_still_be_corrected_and_retired_by_the_operator()
    {

        // A pin an operator has to argue with is a pin they stop using. What it binds is the automatic
        // path, because that is the one that acts without being asked.
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        _ = await harness.Store.SetPinAsync("m-1", true, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(
            SagaCurationOutcomeKind.Applied,
            (await harness.Store.CorrectAsync(
                "m-1",
                AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
                "the operator prefers spaces",
                harness.Embedding(),
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false)).Kind);

        Assert.Equal(
            SagaCurationOutcomeKind.Applied,
            (await harness.Store.RetireAsync(
                "m-1",
                AnnalContentDigest.ForSagaMemory("the operator prefers spaces"),
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false)).Kind);

    }

}
