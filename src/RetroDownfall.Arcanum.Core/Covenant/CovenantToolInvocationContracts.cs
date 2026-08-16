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

}

/// <summary>
/// The exact retirement target a Ward was shown, resolved outside the inference hot path.
/// </summary>
/// <remarks>
/// The operator cannot consent to "retire something the model named": they have to see the content
/// that is about to disappear, which lane it lives in, which revision it is, and whether Global
/// content will start applying in its place. This value is that disclosure, frozen before the Ward
/// opens so the thing approved and the thing staged are provably the same (§10.14).
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
/// The operator consent one sensitive-egress tool call actually received.
/// </summary>
/// <remarks>
/// Carries the evidence digest rather than the decision alone, because "approved" is only meaningful
/// against the exact tool name, final arguments, sensitivity, and destination it was approved for.
/// A receipt that recorded only a Boolean could be honoured for a different call.
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
