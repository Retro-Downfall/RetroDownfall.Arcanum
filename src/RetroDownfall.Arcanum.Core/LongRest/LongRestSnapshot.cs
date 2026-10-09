using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>An outgoing immutable dependency on one exact retained version.</summary>
public sealed record LongRestDependency(string VersionId, AnnalDependencyRelation Relation, int Ordinal);

/// <summary>One exact incoming dependency, read with its current-head status.</summary>
public sealed record LongRestIncomingDependency(
    string DependentVersionId,
    AnnalDependencyRelation Relation,
    bool IsCurrent);

/// <summary>
/// The owning store's complete policy snapshot. Live current-head status does not form receipt identity;
/// decision-relevant suppression and prior-output context and every retained attribute do. No allocation
/// sequence or model score enters the policy.
/// </summary>
public sealed record LongRestSnapshot(
    string MemoryId,
    string ClaimId,
    string VersionId,
    int Revision,
    AnnalContentHashFormat ContentHashFormat,
    string ContentHash,
    AnnalOrigin Origin,
    Guid? SourceSessionId,
    SagaMemoryScopeKind ScopeKind,
    Guid? CampaignId,
    ContentSensitivity Sensitivity,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset? ValidToUtc,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? RetiredAtUtc,
    DateTimeOffset? PinnedAtUtc,
    bool IsCurrent,
    bool IsConsolidated,
    bool HasEmbedding,
    bool HasSensitivityLabel,
    bool IsTransformationOutput,
    LongRestDependency[] Dependencies,
    LongRestIncomingDependency[] IncomingDependencies);
