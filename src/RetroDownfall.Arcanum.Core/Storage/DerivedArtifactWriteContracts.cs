using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// One derived artifact, its identity, and the sensitivity its producer is asserting.
/// </summary>
/// <remarks>
/// Sensitivity is a required constructor argument rather than an optional one with a clean default.
/// A default would mean every sink added later is untainted until somebody remembers to say
/// otherwise, and "forgot to label it" and "proved it is clean" would be the same value (§10.12).
///
/// <para>The producing evidence is the pair a current Covenant admission leaves behind. A plan
/// without its admission, or an admission without its plan, would let a label claim a current
/// admission it cannot show a plan for, so the constructor refuses that shape rather than storing
/// it.</para>
/// </remarks>
public sealed record DerivedArtifactWrite
{

    public DerivedArtifactWrite(
        SensitiveArtifactKind artifactKind,
        Guid artifactId,
        Guid? sessionId,
        Guid? campaignId,
        Guid? turnId,
        ulong artifactRevision,
        CovenantDigest artifactContentDigest,
        ContentSensitivity sensitivity,
        GenerationProvenance provenance,
        CovenantDigest? producingPlanDigest = null,
        CovenantDigest? producingAdmissionDigest = null,
        CovenantDigest? producingMaintenanceReceiptDigest = null)
    {

        ArgumentNullException.ThrowIfNull(provenance);

        ArtifactKind = Enum.IsDefined(artifactKind)
            ? artifactKind
            : throw new ArgumentOutOfRangeException(nameof(artifactKind));

        ArtifactId = artifactId != Guid.Empty
            ? artifactId
            : throw new ArgumentException("A derived artifact requires its identity.", nameof(artifactId));

        SessionId = RequireOptional(sessionId, nameof(sessionId));

        CampaignId = RequireOptional(campaignId, nameof(campaignId));

        TurnId = RequireOptional(turnId, nameof(turnId));

        ArtifactRevision = artifactRevision;

        ArtifactContentDigest = artifactContentDigest.IsValid
            ? artifactContentDigest
            : throw new ArgumentException(
                "A derived artifact requires the digest of the exact bytes being labelled.",
                nameof(artifactContentDigest));

        ContentSensitivityAlgebra.Validate(sensitivity, provenance, nameof(provenance));

        Sensitivity = sensitivity;

        Provenance = provenance;

        if (producingPlanDigest.HasValue != producingAdmissionDigest.HasValue)
        {

            throw new ArgumentException(
                "A producing Covenant plan and its admission are recorded as a pair or not at all.",
                nameof(producingPlanDigest));

        }

        if (sensitivity is ContentSensitivity.None
            && (producingPlanDigest.HasValue || producingMaintenanceReceiptDigest.HasValue))
        {

            throw new ArgumentException(
                "Untainted content cannot claim producing Covenant evidence.",
                nameof(sensitivity));

        }

        ProducingPlanDigest = producingPlanDigest;

        ProducingAdmissionDigest = producingAdmissionDigest;

        ProducingMaintenanceReceiptDigest = producingMaintenanceReceiptDigest;

    }

    public SensitiveArtifactKind ArtifactKind { get; }

    public Guid ArtifactId { get; }

    public Guid? SessionId { get; }

    public Guid? CampaignId { get; }

    public Guid? TurnId { get; }

    public ulong ArtifactRevision { get; }

    public CovenantDigest ArtifactContentDigest { get; }

    public ContentSensitivity Sensitivity { get; }

    public GenerationProvenance Provenance { get; }

    public CovenantDigest? ProducingPlanDigest { get; }

    public CovenantDigest? ProducingAdmissionDigest { get; }

    public CovenantDigest? ProducingMaintenanceReceiptDigest { get; }

    /// <summary>Whether this write needs a durable label to exist beside its artifact.</summary>
    public bool RequiresLabel => Sensitivity is not ContentSensitivity.None;

    /// <summary>Builds the immutable label this write would persist.</summary>
    public ArtifactSensitivityLabel ToLabel(Guid labelId, DateTimeOffset createdAt) =>
        new(
            labelId,
            ArtifactKind,
            ArtifactId,
            SessionId,
            CampaignId,
            TurnId,
            ArtifactRevision,
            ArtifactContentDigest,
            Sensitivity,
            Provenance,
            ProducingPlanDigest,
            ProducingAdmissionDigest,
            ProducingMaintenanceReceiptDigest,
            createdAt);

    private static Guid? RequireOptional(Guid? value, string parameterName) =>
        value == Guid.Empty
            ? throw new ArgumentException("An optional identifier cannot be empty when present.", parameterName)
            : value;

}

/// <summary>What one labelled write durably resolved to.</summary>
/// <remarks>
/// An untainted write carries no label identity, because an untainted artifact has no row: the
/// absence of a label is the record that nothing was derived from Covenant content.
/// </remarks>
public sealed record LabeledArtifactWriteReceipt(
    SensitiveArtifactKind ArtifactKind,
    Guid ArtifactId,
    ContentSensitivity Sensitivity,
    Guid? LabelId,
    CovenantDigest? LabelDigest);

/// <summary>
/// The content-free current view of one Session's information flow.
/// </summary>
/// <remarks>
/// Read in one indexed lookup by the response-cache filter after request binding and before any
/// cache probe. It is conservative in one direction only: it may still report taint whose artifacts
/// were purged, because purged taint still bars a replay, but it can never report clean while
/// tainted artifacts are counted.
/// </remarks>
public sealed record SessionSensitivityProjection(
    Guid SessionId,
    long TaintedArtifactCount,
    ContentSensitivity MaximumSensitivity,
    CovenantDigest GenerationProvenanceDigest,
    long Revision)
{

    public bool IsTainted =>
        TaintedArtifactCount > 0 || MaximumSensitivity is not ContentSensitivity.None;

}

/// <summary>
/// The single writer and reader of <c>artifact_sensitivity</c> and its Session projection.
/// </summary>
/// <remarks>
/// One implementation, because a label that is written by two code paths is a label that can be
/// written by one of them and skipped by the other. Every derived-output producer in the closed
/// downstream inventory routes here, and each of them either persists its label in the same
/// transaction as its artifact or does not persist the artifact at all.
/// </remarks>
public interface IArtifactSensitivityLedger
{

    /// <summary>
    /// Persists the label for one derived artifact, or proves that none is required.
    /// </summary>
    /// <remarks>
    /// Fails closed on a downgrade: an artifact that already carries a tainted label cannot be
    /// relabelled clean, and a second label for the same artifact identity is refused rather than
    /// added beside the first.
    /// </remarks>
    Task<Result<LabeledArtifactWriteReceipt>> LabelAsync(
        DerivedArtifactWrite write,
        CancellationToken cancellationToken);

    /// <summary>Reads one artifact's label, or reports that it carries none.</summary>
    Task<Result<ArtifactSensitivityLabel?>> TryReadLabelAsync(
        SensitiveArtifactKind artifactKind,
        Guid artifactId,
        CancellationToken cancellationToken);

    /// <summary>Reads the Session's content-free sensitivity projection.</summary>
    Task<Result<SessionSensitivityProjection>> ReadSessionProjectionAsync(
        Guid sessionId,
        CancellationToken cancellationToken);

}
