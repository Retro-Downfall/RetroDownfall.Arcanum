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

        return await FinishAsync(id, CurationVerb.Correct, outcome, cancellationToken).ConfigureAwait(false);

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

        return await FinishAsync(id, CurationVerb.Retire, outcome, cancellationToken).ConfigureAwait(false);

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

        return await FinishAsync(id, CurationVerb.Reinstate, outcome, cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result<SagaCurationResult>> SetPinAsync(string id, bool pinned, CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(id);

        // Neither embeds nor takes a content hash, for the same reason RetireAsync does not embed: a
        // pin binds only the automatic retention path, and an operator must be able to set one whatever
        // the embedding substrate is doing.
        SagaCurationOutcome outcome = await store
            .SetPinAsync(id, pinned, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        return await FinishAsync(id, CurationVerb.Pin, outcome, cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Pairs a store outcome with <see cref="ShowAsync"/>'s own composition rather than a second copy of
    /// it, so a caller sees both what its call did and the state that call produced.
    /// </summary>
    /// <remarks>
    /// Which outcomes are errors is <see cref="MapOutcome"/>'s decision and depends on the verb; this
    /// method only carries <paramref name="verb"/> to it and pairs whatever survives with the projection.
    /// </remarks>
    private async Task<Result<SagaCurationResult>> FinishAsync(
        string id, CurationVerb verb, SagaCurationOutcome outcome, CancellationToken cancellationToken)
    {

        Error? failure = MapOutcome(verb, outcome.Kind);

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

    /// <summary>Which verb a store outcome came back from.</summary>
    /// <remarks>
    /// <see cref="MapOutcome"/> needs it because one store kind means different things to different
    /// verbs, and a verb-blind mapping got that wrong in exactly one place: it reported a correction of
    /// a retired memory as a success whose content had not changed.
    /// </remarks>
    private enum CurationVerb
    {

        Correct,

        Retire,

        Reinstate,

        Pin,

    }

    /// <summary>
    /// Decides which store outcomes are errors, per verb.
    /// </summary>
    /// <remarks>
    /// The question each arm answers is whether the operator got what they asked for. Asking to retire
    /// a memory that is already retired, to reinstate one that is not retired, or to correct one to the
    /// text it already holds all leave the operator holding exactly the state they named, so they are
    /// reported through <see cref="SagaCurationResult.Outcome"/> rather than refused.
    ///
    /// <para>A correction of a retired memory is the one that does not fit that shape, and it is why
    /// this mapping takes a verb at all. The operator asked for new text; the text did not change; the
    /// retirement is the reason. Reporting it as a success would tell them their correction landed when
    /// it did not — so it keeps <see cref="ErrorCodes.Saga.AlreadyRetired"/>, which is the refusal the
    /// design's correction table names.</para>
    ///
    /// <para>The pairs below are the ones <c>SagaMemoryStore.Curation.cs</c> can actually produce, read
    /// off its four verbs; an unlisted pair throws rather than being silently mapped to something.</para>
    /// </remarks>
    private static Error? MapOutcome(CurationVerb verb, SagaCurationOutcomeKind kind) =>
        (verb, kind) switch
        {

            (_, SagaCurationOutcomeKind.Applied) => null,

            (CurationVerb.Correct, SagaCurationOutcomeKind.Unchanged) => null,

            (CurationVerb.Retire, SagaCurationOutcomeKind.AlreadyRetired) => null,

            (CurationVerb.Reinstate, SagaCurationOutcomeKind.NotRetired) => null,

            (CurationVerb.Correct, SagaCurationOutcomeKind.AlreadyRetired) => new Error(
                ErrorCodes.Saga.AlreadyRetired,
                "This memory is retired. Reinstate it before correcting it."),

            (_, SagaCurationOutcomeKind.NotFound) => NotFoundError(),

            (_, SagaCurationOutcomeKind.StaleContent) => new Error(
                ErrorCodes.Saga.StaleContent,
                "The content read before this call no longer matches what is stored now."),

            _ => throw new InvalidOperationException(
                $"Unhandled {nameof(SagaCurationOutcomeKind)} for {verb}: {kind}."),

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
