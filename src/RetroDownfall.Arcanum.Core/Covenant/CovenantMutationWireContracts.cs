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
            CovenantWireValidation.RequireNonNegative(ExpectedRevision, "expected Covenant revision"),
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
            CovenantWireValidation.RequireNonNegative(ExpectedRevision, "expected Covenant revision"),
            CovenantWireValidation.RequireIdentity(MutationId, "client-generated mutation identity"),
            CovenantWireValidation.ValidateToken(PreflightToken));

}
