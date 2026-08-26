using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Core.Annals;

/// <summary>
/// One immutable statement of one claim, as a reader sees it.
/// </summary>
/// <remarks>
/// <paramref name="RecordedUntilUtc"/> is <b>derived</b>, never stored: a version's transaction time
/// ends at the moment its successor was recorded, and is open when it has no successor. Storing it
/// would need an update to a row the append-only guard forbids updating, and would be a second
/// measurement of a quantity the successor's own timestamp already states. Where a value is checked
/// in two places, the second reads the first's result rather than mirroring it.
///
/// <para><paramref name="ValidToUtc"/> is stored, because a validity end is a fact the version states
/// about the world rather than a consequence of a later write. A version may say "true until March"
/// on the day it is written and never be superseded at all.</para>
/// </remarks>
public sealed record AnnalClaimVersion(
    string VersionId,
    string ClaimId,
    long Sequence,
    int Revision,
    AnnalOperation Operation,
    AnnalOrigin Origin,
    SagaMemoryScopeKind ScopeKind,
    Guid? CampaignId,
    ContentSensitivity Sensitivity,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset? ValidToUtc,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? RecordedUntilUtc,
    string? PredecessorVersionId);

/// <summary>
/// One dependency edge, as a reader sees it.
/// </summary>
/// <remarks>
/// <paramref name="Ordinal"/> is a stable total order over one version's edges, so two readers of the
/// same claim see the same dependency list in the same order.
/// </remarks>
public sealed record AnnalDependencyEdge(
    string DependentVersionId,
    string DependencyVersionId,
    AnnalDependencyRelation Relation,
    int Ordinal);

/// <summary>
/// A claim's identity, the durable row it is about, and its guarded current pointer.
/// </summary>
public sealed record AnnalClaimHead(
    string ClaimId,
    AnnalSubjectStore SubjectStore,
    string SubjectId,
    string CurrentVersionId,
    int CurrentRevision,
    AnnalOperation CurrentOperation,
    DateTimeOffset UpdatedAtUtc);
