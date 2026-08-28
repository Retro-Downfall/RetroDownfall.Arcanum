using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// RAG Phase 4 — the operator-facing port over <see cref="ISagaMemoryStore"/>'s curation primitives.
/// </summary>
/// <remarks>
/// Every embedding this class needs is computed before the corresponding store call, never inside it:
/// the store opens its write transaction only once it is called, and an embedding-provider round trip
/// made from inside that transaction would hold a write lock across the network for as long as the
/// provider takes to answer.
/// </remarks>
internal sealed class SagaCurationService(
    ISagaMemoryStore store,
    IWeaveService weave,
    IAnnalsStore annals) : ISagaCurationService
{

    /// <summary><see cref="AnnalContentDigest.ForSagaMemory"/> is a SHA-256 binding: always 32 bytes.</summary>
    private const int ExpectedDigestLength = 32;

    public async Task<Result<SagaMemoryDetail>> ShowAsync(string id, CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(id);

        SagaMemoryCurationRow? row = await store.ReadCurationRowAsync(id, cancellationToken).ConfigureAwait(false);

        if (row is null)
        {

            return Result<SagaMemoryDetail>.Failure(NotFoundError());

        }

        return Result<SagaMemoryDetail>.Success(await ComposeDetailAsync(id, row, cancellationToken).ConfigureAwait(false));

    }

    /// <remarks>
    /// No advisory pre-check skips the embed call when <paramref name="content"/> already matches what
    /// is stored: <see cref="EmbedOrRefuseAsync"/> always runs first, unconditionally, for every
    /// correction including one that will turn out to be a no-op. A pre-check that read the row and
    /// skipped embedding whenever the text already matched would make a no-op correction succeed even
    /// while the embedding substrate is degraded -- narrowing the other refusal the operator reviewed
    /// and kept alongside this one. Paying for one wasted embed call on a no-op request is the accepted
    /// cost of keeping that refusal exactly as strict as it was before.
    /// </remarks>
    public async Task<Result<SagaCurationResult>> CorrectAsync(
        string id, string expectedContentHash, string content, CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(id);

        ArgumentNullException.ThrowIfNull(content);

        Result<byte[]> parsedHash = ParseExpectedContentHash(expectedContentHash);

        if (parsedHash.IsFailure)
        {

            return Result<SagaCurationResult>.Failure(parsedHash.Error);

        }

        // Embedded before the store is ever called: CorrectAsync's own content is already in hand, so
        // there is nothing to read first.
        Result<float[]> embedding = await EmbedOrRefuseAsync(content, cancellationToken).ConfigureAwait(false);

        if (embedding.IsFailure)
        {

            return Result<SagaCurationResult>.Failure(embedding.Error);

        }

        SagaCurationOutcome outcome = await store.CorrectAsync(
            id, parsedHash.Value, content, embedding.Value, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return await FinishAsync(id, outcome, cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result<SagaCurationResult>> RetireAsync(
        string id, string expectedContentHash, CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(id);

        Result<byte[]> parsedHash = ParseExpectedContentHash(expectedContentHash);

        if (parsedHash.IsFailure)
        {

            return Result<SagaCurationResult>.Failure(parsedHash.Error);

        }

        // No embedding step, deliberately: retiring only ever removes a vector, it never writes one, and
        // an operator must be able to retire a bad memory precisely when the embedding substrate is
        // unavailable — that is often the reason retrieval needs curating in the first place.
        SagaCurationOutcome outcome = await store
            .RetireAsync(id, parsedHash.Value, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        return await FinishAsync(id, outcome, cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result<SagaCurationResult>> ReinstateAsync(
        string id, string expectedContentHash, CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(id);

        Result<byte[]> parsedHash = ParseExpectedContentHash(expectedContentHash);

        if (parsedHash.IsFailure)
        {

            return Result<SagaCurationResult>.Failure(parsedHash.Error);

        }

        // Unlike CorrectAsync, reinstating carries no new text of its own -- it restores the row's
        // surviving content -- so that content has to be read before it can be re-embedded.
        SagaMemoryCurationRow? row = await store.ReadCurationRowAsync(id, cancellationToken).ConfigureAwait(false);

        if (row is null)
        {

            return Result<SagaCurationResult>.Failure(NotFoundError());

        }

        Result<float[]> embedding = await EmbedOrRefuseAsync(row.Memory.Content, cancellationToken).ConfigureAwait(false);

        if (embedding.IsFailure)
        {

            return Result<SagaCurationResult>.Failure(embedding.Error);

        }

        SagaCurationOutcome outcome = await store.ReinstateAsync(
            id, parsedHash.Value, embedding.Value, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return await FinishAsync(id, outcome, cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result<SagaCurationResult>> SetPinAsync(string id, bool pinned, CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(id);

        // Neither embeds nor takes a content hash, for the same reason RetireAsync does not embed: a
        // pin binds only the automatic retention path, and an operator must be able to set one whatever
        // the embedding substrate is doing.
        SagaCurationOutcome outcome = await store
            .SetPinAsync(id, pinned, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        return await FinishAsync(id, outcome, cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Pairs a store outcome with <see cref="ShowAsync"/>'s own composition rather than a second copy of
    /// it, so a caller sees both what its call did and the state that call produced.
    /// </summary>
    /// <remarks>
    /// Two of the store's kinds reach the caller as errors, and the line between them and the rest is
    /// whether the caller could have known. <see cref="SagaCurationOutcomeKind.NotFound"/> and
    /// <see cref="SagaCurationOutcomeKind.StaleContent"/> each report something the operator could not
    /// have seen in the state they were shown — the memory is gone, or its content moved since they read
    /// it — and acting on either without being told would lose work. Every other kind describes a memory
    /// that is now in the state the operator asked for, which is what they wanted, so it is reported
    /// through <see cref="SagaCurationResult.Outcome"/> instead of refused.
    /// </remarks>
    private async Task<Result<SagaCurationResult>> FinishAsync(
        string id, SagaCurationOutcome outcome, CancellationToken cancellationToken)
    {

        Error? failure = MapOutcome(outcome.Kind);

        if (failure is { } error)
        {

            return Result<SagaCurationResult>.Failure(error);

        }

        Result<SagaMemoryDetail> detail = await ShowAsync(id, cancellationToken).ConfigureAwait(false);

        if (detail.IsFailure)
        {

            return Result<SagaCurationResult>.Failure(detail.Error);

        }

        return Result<SagaCurationResult>.Success(new SagaCurationResult(outcome.Kind, detail.Value));

    }

    private async Task<SagaMemoryDetail> ComposeDetailAsync(
        string id, SagaMemoryCurationRow row, CancellationToken cancellationToken)
    {

        SagaRetrievalEligibility eligibility = ClassifyEligibility(row);

        AnnalClaimHead? claim = await annals
            .GetClaimAsync(AnnalSubjectStore.Saga, id, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<AnnalClaimVersion> history = claim is null
            ? []
            : await annals.GetVersionsAsync(claim.ClaimId, cancellationToken).ConfigureAwait(false);

        return new SagaMemoryDetail(row.Memory, row.Lifecycle, eligibility, claim, history);

    }

    /// <summary>
    /// Retired first, then ownership, then whether an embedding survives, then eligible — in that
    /// order because a retired memory has no embedding by construction, and reporting that as
    /// <see cref="SagaRetrievalEligibility.EmbeddingMissing"/> would describe the wrong problem to an
    /// operator trying to understand why a memory is not being recalled.
    /// </summary>
    internal static SagaRetrievalEligibility ClassifyEligibility(SagaMemoryCurationRow row)
    {

        if (row.Lifecycle.RetiredAtUtc is not null)
        {

            return SagaRetrievalEligibility.Retired;

        }

        // Unclassified (an upgrade has not reached this row yet) and LegacyUnresolved (the owning
        // Session's binding never resolved, or the Session is gone) both mean the same thing to an
        // operator: retrievable in no scope at all until someone resolves it. Global and Campaign are
        // the only two scopes that supply real authority.
        if (row.Memory.ScopeKind is SagaMemoryScopeKind.Unclassified or SagaMemoryScopeKind.LegacyUnresolved)
        {

            return SagaRetrievalEligibility.OwnershipUnresolved;

        }

        if (!row.HasEmbedding)
        {

            return SagaRetrievalEligibility.EmbeddingMissing;

        }

        return SagaRetrievalEligibility.Eligible;

    }

    private static Error? MapOutcome(SagaCurationOutcomeKind kind) =>
        kind switch
        {

            // The three kinds that write nothing because the memory is already in the state the caller
            // asked for. None of them misread anything: the operator asked for a state and has it. The
            // store rolls each of these back before any write (see SagaMemoryStore.Curation.cs), so the
            // caller is told which of the two things happened through SagaCurationResult.Outcome rather
            // than being refused something it already has.
            SagaCurationOutcomeKind.Applied
                or SagaCurationOutcomeKind.Unchanged
                or SagaCurationOutcomeKind.AlreadyRetired
                or SagaCurationOutcomeKind.NotRetired => null,

            SagaCurationOutcomeKind.NotFound => NotFoundError(),

            SagaCurationOutcomeKind.StaleContent => new Error(
                ErrorCodes.Saga.StaleContent,
                "The content read before this call no longer matches what is stored now."),

            _ => throw new InvalidOperationException($"Unhandled {nameof(SagaCurationOutcomeKind)}: {kind}."),

        };

    private static Error NotFoundError() =>
        new(ErrorCodes.Saga.NotFound, "No Saga memory exists with that identity.");

    /// <summary>
    /// Refuses with <see cref="ErrorCodes.Saga.EmbeddingUnavailable"/> before anything is written when
    /// the embedding substrate is disabled or fails, rather than leaving a memory whose stored text and
    /// stored vector disagree about what it says.
    /// </summary>
    private async Task<Result<float[]>> EmbedOrRefuseAsync(string content, CancellationToken cancellationToken)
    {

        if (!weave.IsAvailable)
        {

            return Result<float[]>.Failure(EmbeddingUnavailableError());

        }

        Result<Embedding<float>> embedded = await weave.EmbedAsync(content, cancellationToken).ConfigureAwait(false);

        if (embedded.IsFailure)
        {

            return Result<float[]>.Failure(EmbeddingUnavailableError());

        }

        return Result<float[]>.Success(embedded.Value.Vector.ToArray());

    }

    private static Error EmbeddingUnavailableError() =>
        new(
            ErrorCodes.Saga.EmbeddingUnavailable,
            "The embedding substrate cannot produce a vector right now, so this write was refused.");

    /// <summary>
    /// Parses the wire's uppercase hex rendering of <see cref="AnnalContentDigest.ForSagaMemory"/>.
    /// A malformed or wrong-length string is a request-shape problem, never a curation-domain refusal,
    /// so it fails with <see cref="ErrorCodes.Validation"/> rather than a <see cref="ErrorCodes.Saga"/>
    /// code.
    /// </summary>
    private static Result<byte[]> ParseExpectedContentHash(string expectedContentHash)
    {

        ArgumentNullException.ThrowIfNull(expectedContentHash);

        try
        {

            byte[] digest = Convert.FromHexString(expectedContentHash);

            if (digest.Length != ExpectedDigestLength)
            {

                return Result<byte[]>.Failure(InvalidHashError());

            }

            return Result<byte[]>.Success(digest);

        }
        catch (Exception failure) when (failure is FormatException or ArgumentException)
        {

            return Result<byte[]>.Failure(InvalidHashError());

        }

    }

    private static Error InvalidHashError() =>
        new(
            ErrorCodes.Validation.InvalidFields,
            "The expected content hash must be a 64-character hexadecimal digest.");

}
