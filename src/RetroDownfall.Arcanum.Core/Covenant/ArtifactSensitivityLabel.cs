using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

public sealed class ArtifactSensitivityLabel
{
    public ArtifactSensitivityLabel(
        Guid labelId,
        SensitiveArtifactKind artifactKind,
        Guid artifactId,
        Guid? sessionId,
        Guid? campaignId,
        Guid? turnId,
        ulong artifactRevision,
        CovenantDigest artifactContentDigest,
        ContentSensitivity sensitivity,
        GenerationProvenance provenance,
        CovenantDigest? producingPlanDigest,
        CovenantDigest? producingAdmissionDigest,
        CovenantDigest? producingMaintenanceReceiptDigest,
        DateTimeOffset createdAt)
    {
        LabelId = CovenantValidation.RequireNonEmpty(labelId, nameof(labelId));
        ArtifactKind = RequireArtifactKind(artifactKind);
        ArtifactId = CovenantValidation.RequireNonEmpty(artifactId, nameof(artifactId));
        SessionId = ValidateOptionalId(sessionId, nameof(sessionId));
        CampaignId = ValidateOptionalId(campaignId, nameof(campaignId));
        TurnId = ValidateOptionalId(turnId, nameof(turnId));
        ArtifactRevision = artifactRevision;
        ArtifactContentDigest = CovenantValidation.RequireDigest(artifactContentDigest, nameof(artifactContentDigest));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));

        ContentSensitivityAlgebra.Validate(sensitivity, provenance, nameof(provenance));

        if (createdAt == default)
        {
            throw new ArgumentException("The label timestamp cannot be the default value.", nameof(createdAt));
        }

        Sensitivity = sensitivity;
        ProducingPlanDigest = producingPlanDigest;
        ProducingAdmissionDigest = producingAdmissionDigest;
        ProducingMaintenanceReceiptDigest = producingMaintenanceReceiptDigest;
        CreatedAt = createdAt;
        SensitivityDigest = CovenantDigests.Sensitivity(provenance.ToDigestInput(sensitivity));
        LabelDigest = CovenantDigests.ArtifactLabel(ToDigestInput());
    }

    [JsonConstructor]
    public ArtifactSensitivityLabel(
        Guid labelId,
        SensitiveArtifactKind artifactKind,
        Guid artifactId,
        Guid? sessionId,
        Guid? campaignId,
        Guid? turnId,
        ulong artifactRevision,
        CovenantDigest artifactContentDigest,
        ContentSensitivity sensitivity,
        GenerationProvenance provenance,
        CovenantDigest sensitivityDigest,
        CovenantDigest? producingPlanDigest,
        CovenantDigest? producingAdmissionDigest,
        CovenantDigest? producingMaintenanceReceiptDigest,
        CovenantDigest labelDigest,
        DateTimeOffset createdAt)
        : this(
            labelId,
            artifactKind,
            artifactId,
            sessionId,
            campaignId,
            turnId,
            artifactRevision,
            artifactContentDigest,
            sensitivity,
            provenance,
            producingPlanDigest,
            producingAdmissionDigest,
            producingMaintenanceReceiptDigest,
            createdAt)
    {
        CovenantValidation.RequireDigest(sensitivityDigest, nameof(sensitivityDigest));
        CovenantValidation.RequireDigest(labelDigest, nameof(labelDigest));

        if (SensitivityDigest != sensitivityDigest)
        {
            throw new ArgumentException("The sensitivity digest does not match the label's sensitivity and provenance.", nameof(sensitivityDigest));
        }

        if (LabelDigest != labelDigest)
        {
            throw new ArgumentException("The label digest does not match the label's canonical fields.", nameof(labelDigest));
        }
    }

    internal ArtifactSensitivityLabel(
        Guid labelId,
        SensitiveArtifactKind artifactKind,
        Guid artifactId,
        Guid? sessionId,
        Guid? campaignId,
        Guid? turnId,
        ulong artifactRevision,
        CovenantDigest artifactContentDigest,
        ContentSensitivity sensitivity,
        GenerationProvenance provenance,
        CovenantDigest sensitivityDigest,
        CovenantDigest? producingEvidenceDigest,
        CovenantDigest labelDigest,
        DateTimeOffset createdAt)
        : this(
            labelId,
            artifactKind,
            artifactId,
            sessionId,
            campaignId,
            turnId,
            artifactRevision,
            artifactContentDigest,
            sensitivity,
            provenance,
            sensitivityDigest,
            producingPlanDigest: null,
            producingAdmissionDigest: null,
            producingMaintenanceReceiptDigest: RequireLegacyEvidenceAbsent(producingEvidenceDigest),
            labelDigest,
            createdAt)
    {
    }

    public Guid LabelId { get; }

    public SensitiveArtifactKind ArtifactKind { get; }

    public Guid ArtifactId { get; }

    public Guid? SessionId { get; }

    public Guid? CampaignId { get; }

    public Guid? TurnId { get; }

    public ulong ArtifactRevision { get; }

    public CovenantDigest ArtifactContentDigest { get; }

    public ContentSensitivity Sensitivity { get; }

    public GenerationProvenance Provenance { get; }

    public CovenantDigest SensitivityDigest { get; }

    public CovenantDigest? ProducingPlanDigest { get; }

    public CovenantDigest? ProducingAdmissionDigest { get; }

    public CovenantDigest? ProducingMaintenanceReceiptDigest { get; }

    public CovenantDigest LabelDigest { get; }

    public DateTimeOffset CreatedAt { get; }

    public ArtifactLabelDigestInput ToDigestInput() =>
        new(
            ArtifactKind,
            ArtifactId,
            SessionId,
            CampaignId,
            TurnId,
            ArtifactRevision,
            ArtifactContentDigest,
            SensitivityDigest,
            ProducingPlanDigest,
            ProducingAdmissionDigest,
            ProducingMaintenanceReceiptDigest);

    private static Guid? ValidateOptionalId(Guid? value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An optional identifier cannot be empty when present.", parameterName);
        }

        return value;
    }

    private static CovenantDigest? RequireLegacyEvidenceAbsent(CovenantDigest? producingEvidenceDigest)
    {
        if (producingEvidenceDigest.HasValue)
        {
            throw new ArgumentException("Legacy production evidence cannot select an unambiguous evidence arm.", nameof(producingEvidenceDigest));
        }

        return null;
    }

    private static SensitiveArtifactKind RequireArtifactKind(SensitiveArtifactKind value) =>
        value is >= SensitiveArtifactKind.AssistantEntry and <= SensitiveArtifactKind.IdempotencyClaim
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));

}
