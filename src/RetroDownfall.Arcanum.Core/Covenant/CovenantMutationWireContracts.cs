using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// One affected Campaign, as an example inside a bounded preflight effect.
/// </summary>
public sealed record CovenantMutationEffectExampleDto(
    Guid CampaignId,
    CovenantEffectDecision Decision,
    bool HasCampaignConfirmedHead,
    bool HasCampaignProposedHead);

/// <summary>
/// Exactly what a prepared mutation would do to resolution, before anybody commits it.
/// </summary>
/// <remarks>
/// The count is exact and the examples are truncated, never the other way round. An operator deciding
/// whether to retire a Global entry needs to know it affects four hundred Campaigns even when only
/// fifty can be shown, and a truncated count would understate the blast radius of the one operation
/// whose blast radius matters most.
///
/// <para>Global semantics also apply to Campaigns that do not exist yet. <paramref name="AppliesToFutureCampaigns"/>
/// says so explicitly rather than leaving it to be inferred from a count that can only describe today.</para>
///
/// <para>There is deliberately no Section byte projection here. The preflight computes one compiled
/// artifact, not the Section that artifact would join, and reporting the artifact's own size beside
/// the Section's ceiling invited exactly one reading: that a preference well under the ceiling is
/// therefore safe to write. Whether a Section has room is settled where it can be enforced, by the
/// quota guard refusing the mutation.</para>
/// </remarks>
public sealed record CovenantMutationEffectDto(
    CovenantEffectDecision LocalDecision,
    long AffectedCampaignCount,
    CovenantMutationEffectExampleDto[] Examples,
    bool ExamplesTruncated,
    bool AppliesToFutureCampaigns,
    bool GlobalConfirmedResurfaces,
    bool ProposedBecomesEligible,
    bool ProposedRemainsReviewOnly,
    string DependentHeadVectorDigest,
    string EffectDigest);

/// <summary>
/// The server-authoritative plan for one operator mutation, and the token that binds it.
/// </summary>
/// <remarks>
/// Read-only and no-store. The token is what makes the preview binding rather than advisory: it
/// carries the operator-authority epoch, dataset generation, request digest, expected revision, key
/// and reclamation epochs, compiled artifact hash, dependent-head vector digest, and effect digest,
/// so a commit that arrives after any of them moved is refused instead of applied against state the
/// operator never saw (§10.16).
/// </remarks>
public sealed record CovenantMutationPreflightDto(
    CovenantScope Scope,
    Guid? CampaignId,
    string NormalizedKey,
    CovenantLane Lane,
    CovenantOperation Operation,
    Guid MutationId,
    string RequestDigest,
    string? AuthoredHash,
    string? RenderedHash,
    long? CompiledByteCost,
    long CurrentLaneRevision,

    /// <summary>The revision the request said it expected, carried back beside the live one.</summary>
    /// <remarks>
    /// Both numbers travel because only the pair can be compared. The commit refuses when they
    /// differ, and a preview that reported the head alone rendered a screen every line of which was
    /// true and which described a write that could not succeed — so the operator approved it and was
    /// then refused, with a message naming neither number.
    /// </remarks>
    long ExpectedLaneRevision,
    long KeyEpoch,
    CovenantMutationEffectDto Effect,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string PreflightToken);

/// <summary>
/// The durable outcome of one committed mutation.
/// </summary>
/// <remarks>
/// A <see cref="CovenantMutationOutcome.NoChange"/> result is returned exactly like an applied one,
/// and <paramref name="Replayed"/> distinguishes a repeat from a first commit. Reporting only the
/// mutations that changed something would make a replay of a deliberate no-op indistinguishable from
/// a mutation that never arrived.
/// </remarks>
public sealed record CovenantMutationResultDto(
    Guid MutationId,
    CovenantMutationOutcome Outcome,
    CovenantOperation Operation,
    CovenantScope Scope,
    Guid? CampaignId,
    string NormalizedKey,
    CovenantLane Lane,
    Guid EntryId,
    Guid? ResultingVersionId,
    long? ResultingLaneRevision,
    string RequestDigest,
    string ResponseReceiptDigest,
    bool Replayed);

/// <summary>
/// A prepared operator <c>Set</c>: every canonical field the commit will carry, so preflight can
/// produce the exact request-idempotency digest.
/// </summary>
/// <remarks>
/// A <c>Set</c> has no lane. The Confirmed lane is the only one an operator authors; the Proposed
/// lane belongs to the agent, and a lane field would make "operator writes Proposed" a validation rule
/// rather than an impossibility.
/// </remarks>
public sealed record CovenantSetPrepareRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    string Content,
    long ExpectedRevision,
    Guid MutationId,
    bool Reactivate)
{

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.ValidateOperationScope(Scope, CampaignId),
            CovenantWireValidation.ValidateKey(Key),
            CovenantWireValidation.ValidateAuthoredContent(Content),
            CovenantWireValidation.RequireNonNegative(ExpectedRevision, "expected Covenant revision"),
            CovenantWireValidation.RequireIdentity(MutationId, "client-generated mutation identity"));

}

/// <summary>
/// A prepared operator retirement of one exact lane head.
/// </summary>
public sealed record CovenantRetirePrepareRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    CovenantLane Lane,
    long ExpectedRevision,
    Guid MutationId)
{

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.ValidateOperationScope(Scope, CampaignId),
            CovenantWireValidation.ValidateKey(Key),
            CovenantWireValidation.ValidateLane(Lane),
            CovenantWireValidation.ValidateRetirableLane(Scope, Lane),
            CovenantWireValidation.RequirePositive(ExpectedRevision, "expected Covenant revision"),
            CovenantWireValidation.RequireIdentity(MutationId, "client-generated mutation identity"));

}

/// <summary>
/// The committed operator <c>Set</c>, carrying the same canonical fields its preflight digested plus
/// the token that authorizes them.
/// </summary>
/// <remarks>
/// The fields are repeated rather than referenced by token, so the server recomputes the request
/// digest from what the caller actually sent and compares it to what the token bound. A commit that
/// carried only a token would be a commit whose content nobody could re-derive.
/// </remarks>
public sealed record CovenantSetRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    string Content,
    long ExpectedRevision,
    Guid MutationId,
    bool Reactivate,
    string PreflightToken)
{

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.ValidateOperationScope(Scope, CampaignId),
            CovenantWireValidation.ValidateKey(Key),
            CovenantWireValidation.ValidateAuthoredContent(Content),
            CovenantWireValidation.RequireNonNegative(ExpectedRevision, "expected Covenant revision"),
            CovenantWireValidation.RequireIdentity(MutationId, "client-generated mutation identity"),
            CovenantWireValidation.ValidateToken(PreflightToken));

}

/// <summary>
/// The committed operator retirement.
/// </summary>
public sealed record CovenantRetireRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    CovenantLane Lane,
    long ExpectedRevision,
    Guid MutationId,
    string PreflightToken)
{

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.ValidateOperationScope(Scope, CampaignId),
            CovenantWireValidation.ValidateKey(Key),
            CovenantWireValidation.ValidateLane(Lane),
            CovenantWireValidation.ValidateRetirableLane(Scope, Lane),
            CovenantWireValidation.RequirePositive(ExpectedRevision, "expected Covenant revision"),
            CovenantWireValidation.RequireIdentity(MutationId, "client-generated mutation identity"),
            CovenantWireValidation.ValidateToken(PreflightToken));

}

/// <summary>
/// The server-authoritative plan for one operator curation change, and the token that binds it.
/// </summary>
/// <remarks>
/// Read-only and no-store. The two fallback flags are the sentence an operator reads before they
/// confirm: retiring or masking a Campaign entry and masking a Global key are opposite answers to
/// "what applies here afterwards", and only the server has measured which one this is.
/// </remarks>
public sealed record CovenantCurationPreflightDto(
    CovenantCurationKind Kind,
    CovenantScope Scope,
    Guid? CampaignId,
    string NormalizedKey,
    CovenantLane Lane,
    Guid MutationId,
    string RequestDigest,
    bool IsPinned,
    bool IsMasked,

    /// <summary>The curation revision the request said it expected, carried back beside the live one.</summary>
    /// <remarks>
    /// Both numbers travel because only the pair can be compared. A preview reporting the head alone
    /// renders a screen every line of which is true and which describes a change that cannot succeed.
    /// </remarks>
    long CurrentRevision,
    long ExpectedRevision,
    long KeyEpoch,

    /// <summary>Whether Global content stops reaching this Campaign, with nothing in its place.</summary>
    bool GlobalConfirmedSuppressed,

    /// <summary>Whether Global content starts reaching this Campaign again.</summary>
    bool GlobalConfirmedResurfaces,

    /// <summary>Whether the change would alter anything at all, or is already the state of the subject.</summary>
    bool ChangesAnything,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string PreflightToken);

/// <summary>
/// The durable outcome of one committed curation change.
/// </summary>
public sealed record CovenantCurationResultDto(
    Guid MutationId,
    CovenantMutationOutcome Outcome,
    CovenantCurationKind Kind,
    CovenantScope Scope,
    Guid? CampaignId,
    string NormalizedKey,
    CovenantLane Lane,
    bool IsPinned,
    bool IsMasked,
    Guid? ResultingVersionId,
    long? ResultingRevision,
    string RequestDigest,
    string ResponseReceiptDigest,
    bool Replayed);

/// <summary>
/// A prepared operator curation change: every canonical field the commit will carry.
/// </summary>
public sealed record CovenantCurationPrepareRequest(
    CovenantCurationKind Kind,
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    CovenantLane Lane,
    long ExpectedRevision,
    Guid MutationId)
{

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.ValidateOperationScope(Scope, CampaignId),
            CovenantWireValidation.ValidateKey(Key),
            CovenantWireValidation.ValidateLane(Lane),
            CovenantWireValidation.ValidateRetirableLane(Scope, Lane),
            CovenantWireValidation.ValidateCurationKind(Kind),
            CovenantWireValidation.ValidateMaskablePlacement(Kind, Scope, Lane),
            CovenantWireValidation.RequireNonNegative(ExpectedRevision, "expected Covenant curation revision"),
            CovenantWireValidation.RequireIdentity(MutationId, "client-generated mutation identity"));

}

/// <summary>
/// The committed operator curation change, carrying the same canonical fields its preflight digested
/// plus the token that authorizes them.
/// </summary>
public sealed record CovenantCurationRequest(
    CovenantCurationKind Kind,
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    CovenantLane Lane,
    long ExpectedRevision,
    Guid MutationId,
    string PreflightToken)
{

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.ValidateOperationScope(Scope, CampaignId),
            CovenantWireValidation.ValidateKey(Key),
            CovenantWireValidation.ValidateLane(Lane),
            CovenantWireValidation.ValidateRetirableLane(Scope, Lane),
            CovenantWireValidation.ValidateCurationKind(Kind),
            CovenantWireValidation.ValidateMaskablePlacement(Kind, Scope, Lane),
            CovenantWireValidation.RequireNonNegative(ExpectedRevision, "expected Covenant curation revision"),
            CovenantWireValidation.RequireIdentity(MutationId, "client-generated mutation identity"),
            CovenantWireValidation.ValidateToken(PreflightToken));

}

/// <summary>
/// A prepared operator correction: a <c>Set</c> that names the exact version it replaces.
/// </summary>
/// <remarks>
/// Correction is not a separate operation. <c>Set</c> already appends an immutable version, links its
/// predecessor, and preserves provenance and sensitivity by construction; what a correction adds is
/// the binding. The four target fields are what make "I am replacing this" checkable rather than
/// asserted: a version that has moved, a lane that is not the one an operator authors, a revision that
/// is no longer current, and a compiled hash that names content the operator never saw are each
/// refused before anything is appended.
///
/// <para><paramref name="TargetLane"/> is carried and validated rather than omitted. <c>Set</c> has no
/// lane because there is nothing to name; a correction names an existing version, and a version id
/// alone does not say which lane it belongs to — which is exactly the mistake an operator makes after
/// reading a history that lists both.</para>
/// </remarks>
public sealed record CovenantCorrectPrepareRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    string Content,
    Guid TargetVersionId,
    CovenantLane TargetLane,
    long ExpectedRevision,
    string TargetRenderedHash,
    Guid MutationId)
{

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.ValidateOperationScope(Scope, CampaignId),
            CovenantWireValidation.ValidateKey(Key),
            CovenantWireValidation.ValidateAuthoredContent(Content),
            CovenantWireValidation.RequireIdentity(TargetVersionId, "target Covenant version identity"),
            CovenantWireValidation.ValidateCorrectableLane(TargetLane),
            CovenantWireValidation.ValidateDigestText(TargetRenderedHash, "target rendered hash"),
            CovenantWireValidation.RequirePositive(ExpectedRevision, "expected Covenant revision"),
            CovenantWireValidation.RequireIdentity(MutationId, "client-generated mutation identity"));

}

/// <summary>
/// The committed operator correction, repeating every field its preflight digested plus the token.
/// </summary>
public sealed record CovenantCorrectRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    string Content,
    Guid TargetVersionId,
    CovenantLane TargetLane,
    long ExpectedRevision,
    string TargetRenderedHash,
    Guid MutationId,
    string PreflightToken)
{

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.ValidateOperationScope(Scope, CampaignId),
            CovenantWireValidation.ValidateKey(Key),
            CovenantWireValidation.ValidateAuthoredContent(Content),
            CovenantWireValidation.RequireIdentity(TargetVersionId, "target Covenant version identity"),
            CovenantWireValidation.ValidateCorrectableLane(TargetLane),
            CovenantWireValidation.ValidateDigestText(TargetRenderedHash, "target rendered hash"),
            CovenantWireValidation.RequirePositive(ExpectedRevision, "expected Covenant revision"),
            CovenantWireValidation.RequireIdentity(MutationId, "client-generated mutation identity"),
            CovenantWireValidation.ValidateToken(PreflightToken));

}
