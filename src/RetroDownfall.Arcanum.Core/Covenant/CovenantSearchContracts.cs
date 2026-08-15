using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// How a page of search results was actually produced.
/// </summary>
/// <remarks>
/// A fallback page is a successful answer, not an error. FTS5 is an inspection accelerator, so its
/// absence changes how fast and how completely a query is answered, never whether the caller gets a
/// usable result.
/// </remarks>
public enum CovenantSearchExecutionMode : byte
{

    Fts = 1,

    CanonicalFallback = 2,

}

/// <summary>
/// What a caller should do about a degraded accelerator.
/// </summary>
public enum CovenantSearchRebuildGuidance : byte
{

    None = 1,

    WaitForSynchronization = 2,

    RebuildRequired = 3,

    AcceleratorUnavailable = 4,

}

/// <summary>
/// Which class of match a hit belongs to, and therefore its position in the deterministic order.
/// </summary>
public enum CovenantSearchMatchClass : byte
{

    ExactKey = 1,

    KeyPrefix = 2,

    Ranked = 3,

}

/// <summary>
/// The only thing a search query may carry into SQL.
/// </summary>
/// <remarks>
/// Every field is a parameter value, never a fragment spliced into SQL text. The MATCH expression is
/// built entirely from quoted literals joined by explicit <c>AND</c>: no <c>OR</c>, <c>NEAR</c>,
/// column filter, unary operator, or caller-supplied wildcard can survive compilation, because the
/// compiler emits the operators rather than passing the caller's through.
/// </remarks>
public sealed record CovenantCompiledSearchTerms(
    string MatchExpression,
    ImmutableArray<string> NormalizedTerms,
    ImmutableArray<string> LikePatterns)
{

    /// <summary>
    /// The escape character the fallback's <c>LIKE ... ESCAPE</c> clause declares.
    /// </summary>
    public const char LikeEscape = '\\';

}

/// <summary>
/// The stable FTS keyset: match class, then the exact IEEE-754 score bits, then stable identities.
/// </summary>
/// <remarks>
/// The score travels as raw bits rather than a rounded number. A continuation that re-derived the
/// score from a formatted decimal would land on a different row whenever two documents scored within
/// a rounding step of each other.
/// </remarks>
public readonly record struct CovenantSearchKeyset(
    CovenantSearchMatchClass MatchClass,
    ulong ScoreBits,
    Guid EntryId,
    Guid VersionId)
{

    /// <summary>
    /// Encodes a finite BM25 score, normalizing negative zero so two equal scores compare equal.
    /// </summary>
    public static Result<ulong> EncodeScore(double score)
    {

        if (double.IsNaN(score) || double.IsInfinity(score))
        {

            return new Error(
                "Covenant.InvalidCursor",
                "A Covenant search score must be a finite IEEE-754 binary64 value.");

        }

        // Negative zero and positive zero are the same score. Leaving the sign bit set would make one
        // of them sort before the other and split a tie across two pages.
        double canonical = score == 0d ? 0d : score;

        return Result<ulong>.Success(BitConverter.DoubleToUInt64Bits(canonical));

    }

    public double Score => BitConverter.UInt64BitsToDouble(ScoreBits);

}

/// <summary>
/// The generation facts one search page was answered from.
/// </summary>
/// <remarks>
/// All read in the same SQLite snapshot as the query itself. Reading them separately would let a
/// page claim an applied tuple that did not hold when its rows were selected.
/// </remarks>
public sealed record CovenantSearchSourceSnapshot(
    Guid DatasetGeneration,
    long CanonicalSearchSequence,
    long CoreCampaignDeletionSequence,
    Guid? AppliedDatasetGeneration,
    long? AppliedSearchSequence,
    long? AppliedCampaignDeletionSequence,
    ulong AcceleratorEpoch)
{

    /// <summary>
    /// Whether the accelerator is current enough to answer instead of the canonical fallback.
    /// </summary>
    public bool AcceleratorEligible =>
        AppliedDatasetGeneration == DatasetGeneration
        && AppliedSearchSequence == CanonicalSearchSequence
        && AppliedCampaignDeletionSequence == CoreCampaignDeletionSequence;

}

/// <summary>
/// One current head that matched.
/// </summary>
public sealed record CovenantSearchHit(
    Guid EntryId,
    Guid VersionId,
    CovenantScope Scope,
    Guid? CampaignId,
    CovenantLane Lane,
    CovenantLifecycle Lifecycle,
    string NormalizedKey,
    CovenantSearchMatchClass MatchClass,
    double Score,
    long SearchRowId);

/// <summary>
/// A bounded free-text inspection request over current heads.
/// </summary>
/// <remarks>
/// It carries the compiler's output, never a raw MATCH fragment. A query type that could hold raw
/// syntax would make "the compiler is the only normalization and escaping path" a convention rather
/// than a fact.
/// </remarks>
public sealed record CovenantSearchQuery(
    CovenantCompiledSearchTerms Terms,
    CovenantCursorScopeSelection ScopeSelection,
    Guid? CampaignId,
    CovenantLane? Lane,
    CovenantLifecycle Lifecycle,
    int PageSize,
    CovenantSearchKeyset? After)
{

    public int EffectivePageSize { get; } = CovenantManagementReadLimits.ClampPageSize(PageSize);

}

/// <summary>
/// One page of hits plus everything a cursor and a health report need.
/// </summary>
public sealed record CovenantSearchPage(
    ImmutableArray<CovenantSearchHit> Hits,
    CovenantSearchKeyset? NextKeyset,
    CovenantSearchSourceSnapshot Sources,
    CovenantSearchExecutionMode ExecutionMode,
    bool Truncated,
    CovenantSearchRebuildGuidance Guidance);

/// <summary>
/// The plaintext body of a list cursor, before Plan 04 protects it.
/// </summary>
/// <remarks>
/// These records are deliberately plaintext and deliberately here. The cursor's AEAD envelope is a
/// transport concern the API layer owns; what the storage layer owns is exactly which facts a cursor
/// has to bind so that reusing it across a changed dataset returns a stale-cursor failure instead of
/// mixing two pages.
/// </remarks>
public sealed record CovenantListCursorBody(
    CovenantCursorEndpoint Endpoint,
    CovenantDigest FilterDigest,
    Guid DatasetGeneration,
    long CanonicalSearchSequence,
    long CoreCampaignDeletionSequence,
    long EnvelopeKeyVersion,
    CovenantListKeyset Keyset);

public sealed record CovenantVersionCursorBody(
    CovenantDigest FilterDigest,
    Guid DatasetGeneration,
    long CanonicalSearchSequence,
    long CoreCampaignDeletionSequence,
    long EnvelopeKeyVersion,
    CovenantVersionKeyset Keyset);

public sealed record CovenantFtsCursorBody(
    CovenantDigest FilterDigest,
    Guid DatasetGeneration,
    long CanonicalSearchSequence,
    long CoreCampaignDeletionSequence,
    Guid AppliedDatasetGeneration,
    long AppliedSearchSequence,
    long AppliedCampaignDeletionSequence,
    ulong AcceleratorEpoch,
    long EnvelopeKeyVersion,
    CovenantSearchKeyset Keyset);

public sealed record CovenantFallbackCursorBody(
    CovenantDigest FilterDigest,
    Guid DatasetGeneration,
    long CanonicalSearchSequence,
    long CoreCampaignDeletionSequence,
    long EnvelopeKeyVersion,
    CovenantListKeyset Keyset);

/// <summary>
/// Why a cursor body cannot continue against the current dataset.
/// </summary>
/// <remarks>
/// <see cref="Stale"/> and <see cref="Invalid"/> are separate because they answer different
/// questions. A stale cursor was genuinely issued by this installation and its dataset simply moved
/// on; an invalid one failed authentication or shape validation and cannot be trusted to say
/// anything at all, including which query it belonged to.
/// </remarks>
public enum CovenantCursorRejection : byte
{

    None = 1,

    Stale = 2,

    Invalid = 3,

}

/// <summary>
/// Pure validation of a cursor body against the facts a fresh read observed.
/// </summary>
public static class CovenantCursorBodyValidator
{

    public static CovenantCursorRejection Validate(
        CovenantListCursorBody body,
        CovenantDigest expectedFilterDigest,
        CovenantSearchSourceSnapshot sources,
        long envelopeKeyVersion)
    {

        ArgumentNullException.ThrowIfNull(body);

        ArgumentNullException.ThrowIfNull(sources);

        if (body.Endpoint is not CovenantCursorEndpoint.List and not CovenantCursorEndpoint.FallbackQuery
            || body.FilterDigest != expectedFilterDigest
            || body.EnvelopeKeyVersion != envelopeKeyVersion)
        {

            return CovenantCursorRejection.Invalid;

        }

        return body.DatasetGeneration != sources.DatasetGeneration
            || body.CanonicalSearchSequence != sources.CanonicalSearchSequence
            || body.CoreCampaignDeletionSequence != sources.CoreCampaignDeletionSequence
                ? CovenantCursorRejection.Stale
                : CovenantCursorRejection.None;

    }

    public static CovenantCursorRejection Validate(
        CovenantFtsCursorBody body,
        CovenantDigest expectedFilterDigest,
        CovenantSearchSourceSnapshot sources,
        long envelopeKeyVersion)
    {

        ArgumentNullException.ThrowIfNull(body);

        ArgumentNullException.ThrowIfNull(sources);

        if (body.FilterDigest != expectedFilterDigest || body.EnvelopeKeyVersion != envelopeKeyVersion)
        {

            return CovenantCursorRejection.Invalid;

        }

        // An FTS cursor binds the accelerator's position as well as canonical's, because a page
        // produced from one applied tuple cannot be continued against another without either
        // repeating or skipping rows.
        return body.DatasetGeneration != sources.DatasetGeneration
            || body.CanonicalSearchSequence != sources.CanonicalSearchSequence
            || body.CoreCampaignDeletionSequence != sources.CoreCampaignDeletionSequence
            || body.AppliedDatasetGeneration != sources.AppliedDatasetGeneration
            || body.AppliedSearchSequence != sources.AppliedSearchSequence
            || body.AppliedCampaignDeletionSequence != sources.AppliedCampaignDeletionSequence
            || body.AcceleratorEpoch != sources.AcceleratorEpoch
                ? CovenantCursorRejection.Stale
                : CovenantCursorRejection.None;

    }

    public static CovenantCursorRejection Validate(
        CovenantVersionCursorBody body,
        CovenantDigest expectedFilterDigest,
        CovenantSearchSourceSnapshot sources,
        long envelopeKeyVersion)
    {

        ArgumentNullException.ThrowIfNull(body);

        ArgumentNullException.ThrowIfNull(sources);

        if (body.FilterDigest != expectedFilterDigest || body.EnvelopeKeyVersion != envelopeKeyVersion)
        {

            return CovenantCursorRejection.Invalid;

        }

        return body.DatasetGeneration != sources.DatasetGeneration
            || body.CanonicalSearchSequence != sources.CanonicalSearchSequence
            || body.CoreCampaignDeletionSequence != sources.CoreCampaignDeletionSequence
                ? CovenantCursorRejection.Stale
                : CovenantCursorRejection.None;

    }

}
