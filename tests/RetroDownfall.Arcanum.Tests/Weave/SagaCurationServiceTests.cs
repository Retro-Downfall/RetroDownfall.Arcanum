using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>RAG Phase 4 — <see cref="SagaCurationService"/>: embedding-before-transaction, outcome-to-error-code mapping, and eligibility composition.</summary>
public sealed class SagaCurationServiceTests
{

    [SkippableFact]
    public async Task Correction_is_refused_before_anything_is_written_when_the_substrate_cannot_embed()
    {

        // Refusing is the point. A correction that cannot re-embed would leave the row saying one thing and
        // the vector saying another, so retrieval would keep surfacing the sentence the operator rejected.
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Unavailable, harness.Annals);

        Result<SagaMemoryDetail> result = await service.CorrectAsync(
            "m-1",
            Convert.ToHexString(AnnalContentDigest.ForSagaMemory("the operator prefers tabs")),
            "the operator prefers spaces",
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Saga.EmbeddingUnavailable, result.Error.Code);

        Assert.Equal(
            "the operator prefers tabs",
            (await harness.Store.ReadCurationRowAsync("m-1", CancellationToken.None)
                .ConfigureAwait(false))!.Memory.Content);

    }

    [SkippableFact]
    public async Task A_retired_memory_reports_retired_rather_than_a_missing_embedding()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        _ = await harness.Store.RetireAsync(
            "m-1", AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Available, harness.Annals);

        Result<SagaMemoryDetail> result = await service
            .ShowAsync("m-1", CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaRetrievalEligibility.Retired, result.Value.Eligibility);

    }

    [SkippableFact]
    public async Task A_memory_whose_ownership_never_resolved_reports_that_rather_than_eligible()
    {

        // Retrievable in no scope at all is a different answer from retired and a different answer from
        // broken, and the operator has to be able to tell the three apart.
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        Guid orphan = await harness.SessionWithUnresolvedBindingAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            orphan, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Available, harness.Annals);

        Result<SagaMemoryDetail> result = await service
            .ShowAsync("m-1", CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaRetrievalEligibility.OwnershipUnresolved, result.Value.Eligibility);

    }

    [SkippableFact]
    public async Task A_memory_with_no_claim_is_shown_rather_than_refused()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: false).ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Available, harness.Annals);

        Result<SagaMemoryDetail> result = await service
            .ShowAsync("m-1", CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.IsSuccess);

        Assert.Null(result.Value.Claim);

        Assert.Empty(result.Value.History);

    }

    [SkippableFact]
    public async Task Showing_an_unknown_identity_fails_with_not_found()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Available, harness.Annals);

        Result<SagaMemoryDetail> result = await service
            .ShowAsync("m-absent", CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Saga.NotFound, result.Error.Code);

    }

    [SkippableFact]
    public async Task Correction_with_malformed_hex_is_refused_as_validation_rather_than_a_saga_code()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Available, harness.Annals);

        Result<SagaMemoryDetail> result = await service.CorrectAsync(
            "m-1", "not-a-valid-hex-string", "the operator prefers spaces", CancellationToken.None)
            .ConfigureAwait(false);

        Assert.True(result.IsFailure);

        // Not a Saga.* code: a malformed hash on the wire is a request-shape problem, never a
        // curation-domain refusal.
        Assert.StartsWith("Validation.", result.Error.Code, StringComparison.Ordinal);

        Assert.Equal(
            "the operator prefers tabs",
            (await harness.Store.ReadCurationRowAsync("m-1", CancellationToken.None)
                .ConfigureAwait(false))!.Memory.Content);

    }

    [SkippableFact]
    public async Task Correction_refuses_content_the_caller_did_not_read()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Available, harness.Annals);

        Result<SagaMemoryDetail> result = await service.CorrectAsync(
            "m-1",
            Convert.ToHexString(AnnalContentDigest.ForSagaMemory("something else entirely")),
            "the operator prefers spaces",
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Saga.StaleContent, result.Error.Code);

    }

    [SkippableFact]
    public async Task Correcting_a_retired_memory_is_refused()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        _ = await harness.Store.RetireAsync(
            "m-1", AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Available, harness.Annals);

        Result<SagaMemoryDetail> result = await service.CorrectAsync(
            "m-1",
            Convert.ToHexString(AnnalContentDigest.ForSagaMemory("the operator prefers tabs")),
            "the operator prefers spaces",
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Saga.AlreadyRetired, result.Error.Code);

    }

    [SkippableFact]
    public async Task Correcting_to_the_stored_text_is_refused_as_unchanged()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Available, harness.Annals);

        Result<SagaMemoryDetail> result = await service.CorrectAsync(
            "m-1",
            Convert.ToHexString(AnnalContentDigest.ForSagaMemory("the operator prefers tabs")),
            "the operator prefers tabs",
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Saga.Unchanged, result.Error.Code);

    }

    [SkippableFact]
    public async Task Correction_returns_the_corrected_content_in_its_projection()
    {

        // ShowAsync's composition, reused: the caller sees the state its own call produced rather than
        // a stale read of what the row said before this call.
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Available, harness.Annals);

        Result<SagaMemoryDetail> result = await service.CorrectAsync(
            "m-1",
            Convert.ToHexString(AnnalContentDigest.ForSagaMemory("the operator prefers tabs")),
            "the operator prefers spaces",
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.IsSuccess);

        Assert.Equal("the operator prefers spaces", result.Value.Memory.Content);

    }

    [SkippableFact]
    public async Task Reinstating_a_live_memory_is_refused()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Available, harness.Annals);

        Result<SagaMemoryDetail> result = await service.ReinstateAsync(
            "m-1",
            Convert.ToHexString(AnnalContentDigest.ForSagaMemory("the operator prefers tabs")),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Saga.NotRetired, result.Error.Code);

    }

    [SkippableFact]
    public async Task Reinstatement_is_refused_before_anything_is_written_when_the_substrate_cannot_embed()
    {

        // The mirror of the correction case: reinstating re-embeds the memory's surviving text, and a
        // reinstated memory with no vector would be retrievable-in-name-only.
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        _ = await harness.Store.RetireAsync(
            "m-1", AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Unavailable, harness.Annals);

        Result<SagaMemoryDetail> result = await service.ReinstateAsync(
            "m-1",
            Convert.ToHexString(AnnalContentDigest.ForSagaMemory("the operator prefers tabs")),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Saga.EmbeddingUnavailable, result.Error.Code);

        Assert.NotNull((await harness.Store.ReadCurationRowAsync("m-1", CancellationToken.None)
            .ConfigureAwait(false))!.Lifecycle.RetiredAtUtc);

    }

    [SkippableFact]
    public async Task Retiring_when_the_embedding_substrate_is_unavailable_still_succeeds()
    {

        // Retiring never embeds. A courtesy IsAvailable check here would leave an operator unable to
        // retire a bad memory precisely when retrieval is already degraded.
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Unavailable, harness.Annals);

        Result<SagaMemoryDetail> result = await service.RetireAsync(
            "m-1",
            Convert.ToHexString(AnnalContentDigest.ForSagaMemory("the operator prefers tabs")),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.IsSuccess);

        Assert.Equal(SagaRetrievalEligibility.Retired, result.Value.Eligibility);

    }

    [SkippableFact]
    public async Task Pinning_when_the_embedding_substrate_is_unavailable_still_succeeds()
    {

        // Pinning neither embeds nor takes a content hash, and must not be caught by the same
        // availability gate that guards correct/reinstate.
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        SagaCurationService service = new(harness.Store, FakeWeaveService.Unavailable, harness.Annals);

        Result<SagaMemoryDetail> result = await service
            .SetPinAsync("m-1", true, CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.IsSuccess);

        Assert.NotNull(result.Value.Lifecycle.PinnedAtUtc);

    }

    /// <summary>Hand-written, no Moq — matching every other double in this suite.</summary>
    private sealed class FakeWeaveService : IWeaveService
    {

        /// <summary>
        /// Matches <see cref="SagaStoreHarness"/>'s own embedding-dimension floor, so a successful embed
        /// never trips the store's dimension-validation guard.
        /// </summary>
        private const int Dimensions = 64;

        private readonly Result<Embedding<float>> _embedResult;

        private FakeWeaveService(bool isAvailable, Result<Embedding<float>> embedResult)
        {

            IsAvailable = isAvailable;

            _embedResult = embedResult;

        }

        public static FakeWeaveService Available { get; } =
            new(true, Result<Embedding<float>>.Success(new Embedding<float>(new float[Dimensions])));

        public static FakeWeaveService Unavailable { get; } =
            new(
                false,
                Result<Embedding<float>>.Failure(new Error(
                    ErrorCodes.Embeddings.FeatureDisabled, "The embedding substrate is unavailable.")));

        public bool IsAvailable { get; }

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(_embedResult);

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new NotSupportedException("SagaCurationService only ever calls EmbedAsync.");

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SagaCurationService.");

    }

}
