using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The bounded management-read contracts the canonical store answers.
/// </summary>
/// <remarks>
/// These are domain values, not wire DTOs. There is no plaintext cursor token, provider type, HTTP
/// shape, or mutable collection anywhere in this file: the API layer maps them and protects the
/// cursor, and keeping the two apart is what stops a transport concern from quietly becoming a
/// storage one.
/// </remarks>
public static class CovenantManagementReadLimits
{

    /// <summary>
    /// Clamps a caller-supplied page size into the closed 1-200 range.
    /// </summary>
    public static int ClampPageSize(int requested) =>
        requested < 1
            ? 1
            : requested > CovenantLimits.MaxPageSize
                ? CovenantLimits.MaxPageSize
                : requested;

}

/// <summary>
/// The stable list keyset: scope ordinal, Campaign ID or zero, normalized key, entry ID, lane
/// ordinal.
/// </summary>
public readonly record struct CovenantListKeyset(
    byte ScopeOrdinal,
    Guid CampaignId,
    string NormalizedKey,
    Guid EntryId,
    byte LaneOrdinal);

/// <summary>
/// The version keyset: descending lane revision, then stable version ID.
/// </summary>
public readonly record struct CovenantVersionKeyset(long LaneRevision, Guid VersionId);

/// <summary>
/// One current head as management surfaces see it.
/// </summary>
public sealed record CovenantHeadItem(
    Guid EntryId,
    Guid VersionId,
    CovenantScope Scope,
    Guid? CampaignId,
    string NormalizedKey,
    string AuthoredKey,
    CovenantLane Lane,
    long LaneRevision,
    CovenantLifecycle Lifecycle,
    CovenantOrigin Origin,
    CovenantDigest? AuthoredHash,
    CovenantDigest? RenderedHash,
    long CompiledByteCost,
    uint ProvenanceCount,
    CovenantDigest ProvenanceDigest,
    long SearchRowId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// A page of current heads plus the exact snapshot facts a cursor has to bind.
/// </summary>
public sealed record CovenantListPage(
    ImmutableArray<CovenantHeadItem> Items,
    CovenantListKeyset? NextKeyset,
    Guid DatasetGeneration,
    long CanonicalSearchSequence,
    long CoreCampaignDeletionSequence,
    bool Truncated);

/// <summary>
/// A bounded list request over exactly one scope selection.
/// </summary>
public sealed record CovenantListQuery(
    CovenantCursorScopeSelection ScopeSelection,
    Guid? CampaignId,
    CovenantLane? Lane,
    CovenantLifecycle Lifecycle,
    int PageSize,
    CovenantListKeyset? After)
{

    public int EffectivePageSize => CovenantManagementReadLimits.ClampPageSize(PageSize);

}

/// <summary>
/// An exact scoped-key lookup. All-scope detail is deliberately impossible: the same key can exist
/// in Global and in every Campaign, so "the" entry would be a guess.
/// </summary>
public sealed record CovenantDetailQuery(CovenantOperationScope Scope, string NormalizedKey);

/// <summary>
/// One scoped key with both lane heads and the current provenance of each.
/// </summary>
public sealed record CovenantDetail(
    CovenantOperationScope Scope,
    string NormalizedKey,
    Guid? EntryId,
    CovenantHeadItem? ConfirmedHead,
    CovenantHeadItem? ProposedHead,
    long KeyEpoch,
    Guid DatasetGeneration,
    long CanonicalSearchSequence);

/// <summary>
/// A descending page of one entry lane's immutable versions.
/// </summary>
public sealed record CovenantVersionQuery(
    Guid EntryId,
    CovenantLane Lane,
    int PageSize,
    CovenantVersionKeyset? After)
{

    public int EffectivePageSize => CovenantManagementReadLimits.ClampPageSize(PageSize);

}

/// <summary>
/// One immutable version as the version page reports it.
/// </summary>
public sealed record CovenantVersionItem(
    Guid VersionId,
    Guid EntryId,
    CovenantLane Lane,
    long LaneRevision,
    CovenantOperation Operation,
    CovenantOrigin Origin,
    CovenantDigest? AuthoredHash,
    CovenantDigest? RenderedHash,
    long CompiledByteCost,
    int CompilerPolicyVersion,
    int RendererPolicyVersion,
    Guid? PredecessorVersionId,
    Guid MutationId,
    uint ProvenanceCount,
    CovenantDigest ProvenanceDigest,
    DateTimeOffset CreatedAtUtc);

public sealed record CovenantVersionPage(
    ImmutableArray<CovenantVersionItem> Items,
    CovenantVersionKeyset? NextKeyset,
    Guid DatasetGeneration,
    long CanonicalSearchSequence,
    bool Truncated);

/// <summary>
/// The bounded attachment provenance of one immutable version.
/// </summary>
public sealed record CovenantSourceQuery(Guid VersionId);

public sealed record CovenantSourceItem(
    int Ordinal,
    Guid AttachmentId,
    string AttachmentVersionIdentity,
    string LogicalKey,
    CovenantDigest ContentHash,
    CovenantMaterializationSourceRange SourceRangeKind,
    long? SourceStart,
    long? SourceEnd,
    Guid? SourceTurnId,
    string? MaterializationReference);

/// <summary>
/// Every source of one version, plus the digest recomputed from the returned leaves.
/// </summary>
/// <remarks>
/// A version can have at most 64 sources, so this page is complete by construction and needs no
/// cursor. The recomputed digest is returned beside the stored one on purpose: a detailed source
/// read is exactly the moment the two can be compared with the leaves in hand.
/// </remarks>
public sealed record CovenantSourcePage(
    Guid VersionId,
    ImmutableArray<CovenantSourceItem> Items,
    uint StoredProvenanceCount,
    CovenantDigest StoredProvenanceDigest,
    CovenantDigest RecomputedProvenanceDigest)
{

    public bool DigestMatches => StoredProvenanceDigest == RecomputedProvenanceDigest;

}

/// <summary>
/// What a prospective mutation would do to resolution.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantEffectDecision>))]
public enum CovenantEffectDecision : byte
{

    NoEffect = 1,

    HeadCreated = 2,

    HeadUpdated = 3,

    HeadRetired = 4,

    GlobalConfirmedResurfaces = 5,

    ProposedBecomesEligible = 6,

    ProposedRemainsReviewOnly = 7,

}

/// <summary>
/// One affected Campaign, as an example inside a bounded effect snapshot.
/// </summary>
public sealed record CovenantMutationEffectExample(
    Guid CampaignId,
    CovenantEffectDecision Decision,
    bool HasCampaignConfirmedHead,
    bool HasCampaignProposedHead);

/// <summary>
/// One scope-lane-lifecycle bucket of the head census.
/// </summary>
public readonly record struct CovenantScopeCensusRow(
    CovenantScope Scope,
    CovenantLane Lane,
    CovenantLifecycle Lifecycle,
    long Count,
    long RenderedBytes);

/// <summary>
/// How much Covenant an installation holds, without saying what any of it is.
/// </summary>
/// <remarks>
/// Buckets rather than one total, because "you have forty retired Campaign proposals" and "you have
/// forty confirmed Global preferences" are different facts about the same number, and an operator
/// deciding whether to prune needs the difference.
///
/// <para>The Global figure is a sum and the two Campaign figures are maxima, because that is what
/// makes all three comparable to the same number. There is one Global Confirmed section per turn, so
/// its sum is what that section costs; a Campaign section is rendered for one Campaign, so summing
/// every Campaign's would compare ten Campaigns at ten percent against a ceiling one of them would
/// have to breach alone.</para>
/// </remarks>
public sealed record CovenantScopeCensus(
    ImmutableArray<CovenantScopeCensusRow> Rows,
    long GlobalConfirmedRenderedBytes,
    long MaxCampaignConfirmedRenderedBytes,
    long MaxCampaignProposedRenderedBytes)
{

    public static CovenantScopeCensus Empty { get; } = new([], 0, 0, 0);

}

/// <summary>
/// The prospective effect of a Global or Campaign mutation on one normalized key.
/// </summary>
public sealed record CovenantMutationEffectQuery(
    CovenantOperationScope Scope,
    string NormalizedKey,
    CovenantLane Lane,
    CovenantOperation Operation);

/// <summary>
/// The exact affected-Campaign count, at most fifty examples, and every epoch the preflight token
/// has to bind.
/// </summary>
/// <remarks>
/// The count is exact and the examples are truncated, never the other way round. A caller deciding
/// whether to retire a Global entry needs to know that it affects four hundred Campaigns even if it
/// can only be shown fifty of them.
/// </remarks>
public sealed record CovenantMutationEffectSnapshot(
    CovenantOperationScope Scope,
    string NormalizedKey,
    CovenantLane Lane,
    CovenantOperation Operation,
    CovenantEffectDecision LocalDecision,
    long AffectedCampaignCount,
    ImmutableArray<CovenantMutationEffectExample> Examples,
    bool ExamplesTruncated,
    CovenantDigest DependentHeadVectorDigest,
    long KeyEpoch,
    long KeyReclamationEpoch,
    long CampaignRegistryEpoch,
    Guid DatasetGeneration,
    long CanonicalSearchSequence)
{

    /// <summary>
    /// The bound on how many affected Campaigns a snapshot will illustrate.
    /// </summary>
    public const int MaxExamples = 50;

}
