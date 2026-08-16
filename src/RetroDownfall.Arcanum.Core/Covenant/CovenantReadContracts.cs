using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// Whether one head is currently shadowed for the evaluated Campaign.
/// </summary>
/// <remarks>
/// <see cref="NotEvaluated"/> is a real answer, not a missing one. An all-scope inspection with no
/// evaluation Campaign genuinely cannot say whether a Global entry is shadowed, because the answer is
/// different in every Campaign. Reporting <c>false</c> there would be a confident wrong answer, which
/// is worse than an honest absence (§10.16).
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantEffectiveShadowState>))]
public enum CovenantEffectiveShadowState : byte
{

    NotEvaluated = 1,

    NotShadowed = 2,

    ShadowedByCampaignConfirmed = 3,

}

/// <summary>
/// Whether one head would reach a prompt for the evaluated Campaign right now.
/// </summary>
/// <remarks>
/// Deliberately separate from lifecycle. A <c>Set</c> head is locally alive and may still be
/// review-only, shadowed, or pressured out, and a management surface that conflated the two would
/// tell an operator their preference is active when nothing has ever rendered it.
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantEffectiveMaterialization>))]
public enum CovenantEffectiveMaterialization : byte
{

    NotEvaluated = 1,

    Eligible = 2,

    ReviewOnly = 3,

    Shadowed = 4,

    Ineligible = 5,

}

/// <summary>
/// Why a page stopped early, when it did.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantPageTruncation>))]
public enum CovenantPageTruncation : byte
{

    None = 1,

    PageSizeReached = 2,

    FallbackCandidateCapReached = 3,

}

/// <summary>
/// The health of the inspection accelerator behind one page.
/// </summary>
/// <remarks>
/// A degraded accelerator is reported on a successful response rather than as an error. FTS5 is an
/// inspection accelerator: its absence changes how fast and how completely a query is answered, never
/// whether the caller gets a usable result (§10.11).
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantSearchHealthState>))]
public enum CovenantSearchHealthState : byte
{

    Healthy = 1,

    Synchronizing = 2,

    Degraded = 3,

    Unavailable = 4,

}

/// <summary>
/// The content-free health of the accelerator that answered, or declined to answer, a page.
/// </summary>
public sealed record CovenantSearchHealthDto(
    CovenantSearchHealthState State,
    CovenantSearchExecutionMode ExecutionMode,
    CovenantSearchRebuildGuidance Guidance);

/// <summary>
/// One current lane head, as every management read reports it.
/// </summary>
/// <remarks>
/// Hashes travel as uppercase hex strings rather than byte arrays: they are compared and displayed,
/// never used to size an allocation, and a base64 or raw-byte form would round-trip differently
/// through the CLI, the Compendium, and an operator's terminal.
///
/// <para>The authored content itself is deliberately absent. A list is a list of what exists, and a
/// surface that returned content by default would make the privacy gate on inspection the exception
/// rather than the rule.</para>
/// </remarks>
public sealed record CovenantHeadDto(
    Guid EntryId,
    Guid VersionId,
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    CovenantLane Lane,
    long LaneRevision,
    CovenantLifecycle Lifecycle,
    CovenantOrigin Origin,
    string? AuthoredHash,
    string? RenderedHash,
    long CompiledByteCost,
    long ProvenanceCount,
    string ProvenanceDigest,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    CovenantEffectiveShadowState Shadow,
    CovenantEffectiveMaterialization Materialization);

/// <summary>
/// A bounded page of current heads plus the exact identity a continuation has to match.
/// </summary>
/// <remarks>
/// <paramref name="FilterDigest"/> is the canonical filter identity the cursor binds. It is returned
/// so a caller can prove that the page it holds and the cursor it is about to send describe the same
/// query, rather than discovering the mismatch as an opaque stale-cursor refusal.
/// </remarks>
public sealed record CovenantPageDto(
    CovenantHeadDto[] Items,
    string? NextCursor,
    string FilterDigest,
    CovenantSearchHealthDto Search,
    bool Truncated,
    CovenantPageTruncation TruncationReason);

/// <summary>
/// One immutable version of one lane.
/// </summary>
public sealed record CovenantVersionDto(
    Guid VersionId,
    Guid EntryId,
    CovenantLane Lane,
    long LaneRevision,
    CovenantOperation Operation,
    CovenantOrigin Origin,
    string? AuthoredHash,
    string? RenderedHash,
    long CompiledByteCost,
    int CompilerPolicyVersion,
    int RendererPolicyVersion,
    Guid? PredecessorVersionId,
    Guid MutationId,
    long ProvenanceCount,
    string ProvenanceDigest,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// A separately paginated descending page of one entry lane's history.
/// </summary>
/// <remarks>
/// Separate from the current-head page on purpose. Nesting history inside a list would make the size
/// of one page depend on how often an operator has edited one entry, and a bounded response cannot be
/// bounded by something unbounded.
/// </remarks>
public sealed record CovenantVersionPageDto(
    CovenantVersionDto[] Items,
    string? NextCursor,
    string FilterDigest,
    bool Truncated);

/// <summary>
/// One exact attachment-provenance leaf of one immutable version.
/// </summary>
public sealed record CovenantSourceDto(
    int Ordinal,
    Guid AttachmentId,
    string AttachmentVersionIdentity,
    string LogicalKey,
    string ContentHash,
    CovenantMaterializationSourceRange SourceRangeKind,
    long? SourceStart,
    long? SourceEnd,
    Guid? SourceTurnId,
    string? MaterializationReference);

/// <summary>
/// Every source of one version, complete by construction, plus the digest recomputed from the leaves
/// actually returned.
/// </summary>
/// <remarks>
/// A version has at most 64 sources, so this response has no cursor and no truncation state. The
/// recomputed digest travels beside the stored one because a detailed source read is exactly the
/// moment the two can be compared with the leaves in hand.
/// </remarks>
public sealed record CovenantSourcesDto(
    Guid VersionId,
    CovenantSourceDto[] Sources,
    long StoredProvenanceCount,
    string StoredProvenanceDigest,
    string RecomputedProvenanceDigest,
    bool DigestMatches);

/// <summary>
/// Both lane heads of one scoped key, plus the key epoch a mutation has to match.
/// </summary>
public sealed record CovenantDetailDto(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    Guid? EntryId,
    CovenantHeadDto? Confirmed,
    CovenantHeadDto? Proposed,
    long KeyEpoch,
    CovenantSourcesDto? ConfirmedSources,
    CovenantSourcesDto? ProposedSources);

/// <summary>
/// One rendered section of a diagnostic plan.
/// </summary>
/// <remarks>
/// <paramref name="Content"/> is present only under the existing <c>showContent</c> privacy gate plus
/// clean Covenant read authority. Default inspection reports hashes, byte and token counts, placement,
/// and typed decisions, because those answer "why is my preference not being honoured" without
/// disclosing anything.
/// </remarks>
public sealed record CovenantExplainSectionDto(
    CovenantPlacement Placement,
    long ItemCount,
    long RenderedBytes,
    long EstimatedTokens,
    string SectionDigest,
    string? Content);

/// <summary>
/// Why one candidate was, or was not, rendered.
/// </summary>
public sealed record CovenantExplainDecisionDto(
    Guid EntryId,
    Guid VersionId,
    string Key,
    CovenantLane Lane,
    CovenantPlanDecision Decision,
    CovenantPlacement? Placement,
    Guid? ShadowingVersionId,
    long ByteCost,
    string FragmentDigest);

/// <summary>
/// One fresh diagnostic snapshot, plan, and preview receipt for one explicit evaluation.
/// </summary>
/// <remarks>
/// It runs the same loader, linker, and admission code as a live turn and publishes no mutation, so
/// an explanation cannot disagree with the thing it explains. It never enters the live turn cache:
/// a diagnostic that warmed the cache would change the behaviour it was asked to describe.
/// </remarks>
public sealed record CovenantExplainDto(
    Guid? CampaignId,
    bool Enabled,
    bool Available,
    string SnapshotDigest,
    string PlanDigest,
    CovenantExplainSectionDto[] Sections,
    CovenantExplainDecisionDto[] Decisions,
    long ConfirmedTokens,
    long ProposedTokens,
    CovenantAdmissionDecision Admission,
    bool ContentIncluded);

/// <summary>
/// One aggregate count in the memory-status Covenant block.
/// </summary>
public sealed record CovenantScopeCountDto(
    CovenantScope Scope,
    CovenantLane Lane,
    CovenantLifecycle Lifecycle,
    long Count);

/// <summary>
/// The content-free Covenant capability health, aggregate counts, retention, and degradation the
/// existing memory-status surface gains.
/// </summary>
/// <remarks>
/// Counts and ceilings only. This block is reachable wherever <c>/api/memory/status</c> is, so it may
/// never carry a key, a fragment, or a raw content hash; the exact per-entry surfaces are the typed
/// Covenant routes, which require Covenant read authority of their own.
/// </remarks>
public sealed record CovenantStatusDto(
    bool Enabled,
    bool Available,
    CovenantScopeCountDto[] Counts,
    long GlobalConfirmedRenderedBytes,
    long CampaignConfirmedRenderedBytes,
    long CampaignProposedRenderedBytes,
    long RenderedByteCeilingPerSection,
    CovenantSearchHealthDto Search,
    string Retention,
    string? DegradationCode);

/// <summary>
/// A bounded list of current heads over exactly one stated scope selection.
/// </summary>
public sealed record CovenantListRequest(
    CovenantCursorScopeSelection Scope,
    Guid? CampaignId,
    CovenantLane? Lane,
    CovenantLifecycle Lifecycle,
    Guid? EffectiveForCampaignId,
    int Limit,
    string? Cursor)
{

    /// <summary>The clamped page size, in the closed 1-200 range, defaulting at or below zero.</summary>
    [JsonIgnore]
    public int EffectiveLimit =>
        Limit <= 0 ? CovenantLimits.DefaultPageSize : CovenantManagementReadLimits.ClampPageSize(Limit);

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.ValidateScopeSelection(Scope, CampaignId),
            CovenantWireValidation.ValidateLane(Lane),
            CovenantWireValidation.ValidateLifecycle(Lifecycle),
            CovenantWireValidation.ValidateEvaluationCampaign(EffectiveForCampaignId),
            CovenantWireValidation.ValidateCursor(Cursor));

}

/// <summary>
/// A bounded free-text inspection of current heads.
/// </summary>
/// <remarks>
/// The query is a body field, never a query string. Covenant keys and search text are exactly the
/// values an access log would keep forever, and a URL is the one part of a request nothing redacts.
/// </remarks>
public sealed record CovenantQueryRequest(
    CovenantCursorScopeSelection Scope,
    Guid? CampaignId,
    string Query,
    CovenantLane? Lane,
    CovenantLifecycle Lifecycle,
    Guid? EffectiveForCampaignId,
    int Limit,
    string? Cursor)
{

    [JsonIgnore]
    public int EffectiveLimit =>
        Limit <= 0 ? CovenantLimits.DefaultPageSize : CovenantManagementReadLimits.ClampPageSize(Limit);

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.ValidateScopeSelection(Scope, CampaignId),
            CovenantWireValidation.ValidateLane(Lane),
            CovenantWireValidation.ValidateLifecycle(Lifecycle),
            CovenantWireValidation.ValidateEvaluationCampaign(EffectiveForCampaignId),
            CovenantWireValidation.ValidateSearchText(Query),
            CovenantWireValidation.ValidateCursor(Cursor));

}

/// <summary>
/// An exact scoped-key lookup.
/// </summary>
/// <remarks>
/// It carries <see cref="CovenantScope"/>, which has no all-scopes member: the same key can exist in
/// Global and in every Campaign, so an ambiguous lookup is unrepresentable rather than refused.
/// </remarks>
public sealed record CovenantDetailRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key)
{

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.ValidateOperationScope(Scope, CampaignId),
            CovenantWireValidation.ValidateKey(Key));

}

/// <summary>
/// A descending page of one entry lane's immutable versions.
/// </summary>
public sealed record CovenantVersionsRequest(
    Guid EntryId,
    CovenantLane Lane,
    int Limit,
    string? Cursor)
{

    [JsonIgnore]
    public int EffectiveLimit =>
        Limit <= 0 ? CovenantLimits.DefaultPageSize : CovenantManagementReadLimits.ClampPageSize(Limit);

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.RequireIdentity(EntryId, "Covenant entry identity"),
            CovenantWireValidation.ValidateLane(Lane),
            CovenantWireValidation.ValidateCursor(Cursor));

}

/// <summary>
/// The bounded, exact attachment provenance of one immutable version.
/// </summary>
public sealed record CovenantSourcesRequest(Guid VersionId)
{

    public Result Validate() =>
        CovenantWireValidation.RequireIdentity(VersionId, "Covenant version identity");

}

/// <summary>
/// One explicit Global-only or Campaign evaluation to explain.
/// </summary>
public sealed record CovenantExplainRequest(Guid? CampaignId, bool ShowContent)
{

    public Result Validate() =>
        CovenantWireValidation.ValidateEvaluationCampaign(CampaignId);

}
