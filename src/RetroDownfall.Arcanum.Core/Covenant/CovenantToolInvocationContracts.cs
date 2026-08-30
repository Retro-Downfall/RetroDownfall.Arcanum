using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The single bounded head read one live turn may perform for an unplanned Covenant key.
/// </summary>
/// <remarks>
/// Deliberately narrower than <c>ICovenantStore</c>. A staging handler needs to know whether a lane
/// head exists, at which revision, and whether it is a tombstone — nothing more. Handing it the whole
/// read port would let a tool call read content the turn plan never admitted (§10.14).
/// </remarks>
public interface ICovenantTurnHeadProbe
{

    ValueTask<Result<CovenantLaneHeadProbe>> ProbeAsync(
        CovenantLane lane,
        string normalizedKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Measures the Section this turn's Campaign renders one lane into, ignoring named keys.
    /// </summary>
    /// <remarks>
    /// Three numbers and no content, which is why a staging handler is allowed to ask. It has to be
    /// allowed to ask: a proposal that would push its Section past a ceiling is refused by the write
    /// authority, and the write authority runs inside the transaction that carries the operator's
    /// answer, so a proposal admitted here and refused there costs the operator the reply they asked
    /// for rather than only the proposal.
    /// </remarks>
    ValueTask<Result<CovenantSectionOccupancy>> ProbeSectionAsync(
        CovenantLane lane,
        ImmutableArray<string> excludedKeys,
        CancellationToken cancellationToken);

    /// <summary>
    /// Measures every scope-wide capacity counter for the scope this turn would write into.
    /// </summary>
    /// <remarks>
    /// Counts and no content, on the same terms as the Section probe. The Section ceilings are two of
    /// twelve; the other ten bound the scope's stored rows and are applied by the same authority in
    /// the same transaction, so a staging preflight that checked only the Section pair still let a
    /// batch through that the commit would refuse — and refusing there discards the operator's reply.
    /// </remarks>
    ValueTask<Result<CovenantQuotaSnapshot>> ProbeScopeAsync(
        ImmutableArray<string> excludedKeys,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the exact retirement target the one-call capability will bind, or refuses it.
    /// </summary>
    /// <remarks>
    /// Here rather than on a port of its own, because this is the one object holding both the store
    /// and the turn's own lease — the same reason the head probe lives here. Resolving it later would
    /// need the lease handed out, which is how a turn ends up with two owners for one admission.
    ///
    /// <para>It refuses before any effect: a missing head, a tombstone, and a pinned head are all
    /// targets the write authority would reject.</para>
    /// </remarks>
    ValueTask<Result<CovenantRetirementPreflight>> ResolveRetirementPreflightAsync(
        CovenantLane lane,
        string normalizedKey,
        CancellationToken cancellationToken);

}

/// <summary>
/// The exact retirement target canonical preflight resolved outside the inference hot path.
/// </summary>
/// <remarks>
/// The host does not retire merely "something the model named": it binds the content, lane, revision,
/// key epoch, and Global fallback fact from canonical state. This value is that target, frozen before
/// dispatch so the thing resolved and the thing staged are provably the same (§10.14).
/// </remarks>
public sealed class CovenantRetirementPreflight
{

    public CovenantRetirementPreflight(
        Guid entryId,
        Guid versionId,
        CovenantLane lane,
        long targetLaneRevision,
        string normalizedKey,
        string sanitizedAuthoredDisclosure,
        CovenantDigest renderedHash,
        bool globalFallbackApplies,
        long keyEpoch,
        CovenantDigest preflightBodyDigest)
    {

        EntryId = CovenantValidation.RequireNonEmpty(entryId, nameof(entryId));

        VersionId = CovenantValidation.RequireNonEmpty(versionId, nameof(versionId));

        Lane = lane is CovenantLane.Confirmed or CovenantLane.Proposed
            ? lane
            : throw new ArgumentOutOfRangeException(nameof(lane));

        TargetLaneRevision = CovenantValidation.RequirePositive(targetLaneRevision, nameof(targetLaneRevision));

        ArgumentException.ThrowIfNullOrEmpty(normalizedKey);

        ArgumentNullException.ThrowIfNull(sanitizedAuthoredDisclosure);

        NormalizedKey = normalizedKey;

        SanitizedAuthoredDisclosure = sanitizedAuthoredDisclosure;

        RenderedHash = CovenantValidation.RequireDigest(renderedHash, nameof(renderedHash));

        GlobalFallbackApplies = globalFallbackApplies;

        KeyEpoch = keyEpoch >= 0
            ? keyEpoch
            : throw new ArgumentOutOfRangeException(nameof(keyEpoch));

        PreflightBodyDigest = CovenantValidation.RequireDigest(preflightBodyDigest, nameof(preflightBodyDigest));

    }

    public Guid EntryId { get; }

    public Guid VersionId { get; }

    public CovenantLane Lane { get; }

    /// <summary>The revision the retirement will compare and swap against.</summary>
    public long TargetLaneRevision { get; }

    public string NormalizedKey { get; }

    /// <summary>The compiled fragment the operator is shown, already hardened by the compiler.</summary>
    public string SanitizedAuthoredDisclosure { get; }

    public CovenantDigest RenderedHash { get; }

    /// <summary>Whether Global Confirmed content starts applying once this target is gone.</summary>
    public bool GlobalFallbackApplies { get; }

    /// <summary>
    /// The normalized key's reclamation epoch at preflight time, which publication compares again.
    /// </summary>
    public long KeyEpoch { get; }

    /// <summary>The target-bound token digest the staged tombstone carries as evidence.</summary>
    public CovenantDigest PreflightBodyDigest { get; }

}

/// <summary>
/// Historical operator-consent evidence retained for Ward-backed retirement compatibility.
/// </summary>
/// <remarks>
/// New live calls never create or carry this type. Historical receipts retain the evidence digest
/// rather than the decision alone, because "approved" is only meaningful against the exact tool name,
/// final arguments, sensitivity, and destination it was approved for. A receipt that recorded only a
/// Boolean could be honoured for a different call.
/// </remarks>
public sealed class CovenantToolWardReceipt
{

    public CovenantToolWardReceipt(
        WardEvidenceDigestInput evidence,
        CovenantAuthorizationMode mode)
    {

        ArgumentNullException.ThrowIfNull(evidence);

        Mode = mode is CovenantAuthorizationMode.WardInteractive
            or CovenantAuthorizationMode.WardConfiguredAutoApproval
            ? mode
            : throw new ArgumentOutOfRangeException(
                nameof(mode),
                "A Ward receipt records interactive or configured auto-approval consent only.");

        Evidence = evidence;

        Decision = evidence.Decision;

        Digest = CovenantDigests.WardEvidence(evidence);

    }

    public WardEvidenceDigestInput Evidence { get; }

    public CovenantWardDecision Decision { get; }

    public CovenantAuthorizationMode Mode { get; }

    public CovenantDigest Digest { get; }

    public bool IsApproved => Decision is CovenantWardDecision.Approved;

}
