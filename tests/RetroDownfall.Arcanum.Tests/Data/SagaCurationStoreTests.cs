using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class SagaCurationStoreTests
{

    [SkippableFact]
    public async Task A_freshly_written_memory_reads_back_active_unpinned_and_embedded()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
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

        await harness.Store.InsertAsync(
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

        await harness.Store.InsertAsync(
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

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        byte[] digest = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

        _ = await harness.Store.RetireAsync("m-1", digest, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        SagaCurationOutcome second = await harness.Store
            .RetireAsync("m-1", digest, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.AlreadyRetired, second.Kind);

    }

    [SkippableFact]
    public async Task Reinstatement_restores_the_embedding_and_releases_the_suppression()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
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

        await harness.Store.InsertAsync(
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

    /// <summary>
    /// Two memories with identical content in one scope hash to the same suppression digest.
    /// <c>INSERT OR IGNORE</c> is what keeps the second retirement from aborting on the first's row.
    /// </summary>
    [SkippableFact]
    public async Task Retiring_two_memories_that_share_content_and_scope_produces_one_suppression()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(seed: 1), CancellationToken.None).ConfigureAwait(false);

        await harness.Store.InsertAsync(
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

        await harness.Store.InsertAsync(
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

        await harness.Store.InsertAsync(
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

}
