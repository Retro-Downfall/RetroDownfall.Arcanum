namespace RetroDownfall.Arcanum.Core.Covenant;

public sealed record CovenantDisclosureDraft
{
    public CovenantDisclosureDraft(
        Guid originInstallationId,
        CovenantDisclosureSubjectKind subjectKind,
        Guid subjectId,
        CovenantDigest effectIdentityDigest,
        CovenantEgressDestination destination,
        CovenantDisclosureRevocability revocability,
        CovenantDigest opaqueDestinationIdentityDigest,
        CovenantDigest sensitivityDigest,
        CovenantDigest? wardEvidenceDigest,
        CovenantDigest? admissionDigest,
        CovenantDigest? backupEvidenceDigest,
        long timestamp)
    {
        CovenantValidation.RequireNonEmpty(originInstallationId, nameof(originInstallationId));
        CovenantValidation.RequireNonEmpty(subjectId, nameof(subjectId));
        _ = CovenantPolicyV1Manifest.GetCode(subjectKind);
        _ = CovenantPolicyV1Manifest.GetCode(destination);
        _ = CovenantPolicyV1Manifest.GetCode(revocability);
        EffectIdentityDigest = CovenantValidation.RequireDigest(effectIdentityDigest, nameof(effectIdentityDigest));
        OpaqueDestinationIdentityDigest = CovenantValidation.RequireDigest(opaqueDestinationIdentityDigest, nameof(opaqueDestinationIdentityDigest));
        SensitivityDigest = CovenantValidation.RequireDigest(sensitivityDigest, nameof(sensitivityDigest));
        WardEvidenceDigest = ValidateOptional(wardEvidenceDigest, nameof(wardEvidenceDigest));
        AdmissionDigest = ValidateOptional(admissionDigest, nameof(admissionDigest));
        BackupEvidenceDigest = ValidateOptional(backupEvidenceDigest, nameof(backupEvidenceDigest));
        OriginInstallationId = originInstallationId;
        SubjectKind = subjectKind;
        SubjectId = subjectId;
        Destination = destination;
        Revocability = revocability;
        Timestamp = timestamp;
    }

    public Guid OriginInstallationId { get; }

    public CovenantDisclosureSubjectKind SubjectKind { get; }

    public Guid SubjectId { get; }

    public CovenantDigest EffectIdentityDigest { get; }

    public CovenantEgressDestination Destination { get; }

    public CovenantDisclosureRevocability Revocability { get; }

    public CovenantDigest OpaqueDestinationIdentityDigest { get; }

    public CovenantDigest SensitivityDigest { get; }

    public CovenantDigest? WardEvidenceDigest { get; }

    public CovenantDigest? AdmissionDigest { get; }

    public CovenantDigest? BackupEvidenceDigest { get; }

    public long Timestamp { get; }

    private static CovenantDigest? ValidateOptional(CovenantDigest? value, string parameterName) =>
        value is { } present ? CovenantValidation.RequireDigest(present, parameterName) : null;
}

public sealed class CovenantDisclosureReceipt
{
    private readonly byte[] _evidenceBloom;

    public CovenantDisclosureReceipt(CovenantDisclosureDraft draft, ulong allocatedSubjectOrdinal)
    {
        Draft = draft ?? throw new ArgumentNullException(nameof(draft));
        AllocatedSubjectOrdinal = allocatedSubjectOrdinal;
        Digest = CovenantDigests.ExternalDisclosure(new ExternalDisclosureDigestInput(draft.OriginInstallationId, draft.SubjectKind, draft.SubjectId, draft.EffectIdentityDigest, allocatedSubjectOrdinal, draft.Destination, draft.Revocability, draft.OpaqueDestinationIdentityDigest, draft.SensitivityDigest, draft.WardEvidenceDigest, draft.AdmissionDigest, draft.BackupEvidenceDigest, draft.Timestamp));
        _evidenceBloom = CovenantDisclosureStateAlgebra.CreateEvidenceBloom(Digest);
    }

    public CovenantDisclosureDraft Draft { get; }

    public ulong AllocatedSubjectOrdinal { get; }

    public CovenantDigest Digest { get; }

    public byte[] EvidenceBloom => _evidenceBloom.ToArray();
}
