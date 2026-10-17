using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>Version-two canonical decisions retain each protected label and the identical nonempty partition.</summary>
public static class ProtectedLongRestPolicy
{
    public const int PolicyVersion = 2;

    public static Result<LongRestReceipt> Evaluate(LongRestRequest request, IReadOnlyList<LongRestSourceLeaf> leaves, LongRestProtectedEvidence evidence)
    {
        Result<LongRestSnapshot[]> prepared = Prepare(request, leaves, evidence);

        if (prepared.IsFailure)
        {
            return Result<LongRestReceipt>.Failure(prepared.Error);
        }

        LongRestSnapshot[] ordered = prepared.Value;

        if (ordered.Any(snapshot => !snapshot.IsCurrent))
        {
            return new Error(ErrorCodes.LongRest.StaleInput, "An exact protected input is no longer current.");
        }

        string input = Input(ordered, leaves, evidence);

        LongRestSnapshot survivor = request.SurvivorVersionId is { } requested
            ? ordered.Single(snapshot => snapshot.VersionId == requested)
            : ordered.OrderBy(snapshot => LongRestPolicy.OriginPriority(snapshot.Origin)).ThenBy(snapshot => snapshot.MemoryId, StringComparer.Ordinal).First();

        LongRestSnapshot[] eliminated = ordered.Where(snapshot => snapshot.VersionId != survivor.VersionId).ToArray();

        LongRestReason reason = LongRestPolicy.Decide(request.Kind, ordered, survivor, eliminated, evidence);

        LongRestOutcome outcome = reason == LongRestReason.None ? LongRestOutcome.Applied : LongRestOutcome.NoChange;

        LongRestSnapshot? output = outcome == LongRestOutcome.Applied ? survivor : null;

        LongRestSnapshot[] superseded = outcome == LongRestOutcome.Applied ? eliminated : [];

        return new LongRestReceipt(ProtectedLongRestCanonicalHash.Receipt(request.Kind, input, request.SurvivorVersionId), PolicyVersion,
            request.Kind, outcome, reason, input, ProtectedLongRestCanonicalHash.Output(request.Kind, outcome, reason, output, superseded),
            ordered.Select(LongRestPolicy.Target).ToArray(), output?.MemoryId, output?.ClaimId, output?.VersionId,
            superseded.Select(snapshot => snapshot.VersionId).ToArray());
    }

    public static Result<string> Identify(LongRestRequest request, IReadOnlyList<LongRestSourceLeaf> leaves, LongRestProtectedEvidence evidence)
    {
        Result<LongRestSnapshot[]> prepared = Prepare(request, leaves, evidence);

        return prepared.IsFailure ? Result<string>.Failure(prepared.Error)
            : ProtectedLongRestCanonicalHash.Receipt(request.Kind, Input(prepared.Value, leaves, evidence), request.SurvivorVersionId);
    }

    private static Result<LongRestSnapshot[]> Prepare(LongRestRequest request, IReadOnlyList<LongRestSourceLeaf> leaves, LongRestProtectedEvidence evidence)
    {
        if (leaves is null || leaves.Any(leaf => leaf?.Snapshot is null) || evidence is null || !evidence.PartitionDigest.IsValid
            || evidence.Provenance is null || evidence.Provenance.IsEmpty || evidence.LabelDigests.IsDefault
            || evidence.LabelDigests.Length != leaves.Count || evidence.LabelDigests.Any(digest => !digest.IsValid)
            || evidence.LabelDigests.Distinct().Count() != leaves.Count
            || CovenantDigests.Sensitivity(evidence.Provenance.ToDigestInput(ContentSensitivity.CovenantDerived)) != evidence.PartitionDigest)
        {
            return Invalid();
        }

        Result<LongRestSnapshot[]> snapshots = LongRestPolicy.Prepare(request, leaves.Select(leaf => leaf.Snapshot).ToArray());

        if (snapshots.IsFailure)
        {
            return snapshots;
        }

        HashSet<Guid> labels = [];

        HashSet<CovenantDigest> digests = [];

        foreach (LongRestSourceLeaf leaf in leaves)
        {
            ArtifactSensitivityLabel? label = leaf.CanonicalLabel;

            if (label is null || label.ArtifactKind != SensitiveArtifactKind.Saga || !labels.Add(label.LabelId) || !digests.Add(label.LabelDigest)
                || label.ArtifactId.ToString("D") != leaf.Snapshot.MemoryId || label.SessionId != leaf.Snapshot.SourceSessionId
                || label.CampaignId != leaf.Snapshot.CampaignId || !leaf.Snapshot.HasSensitivityLabel
                || label.ArtifactRevision != leaf.SagaRevision || label.ArtifactContentDigest != leaf.SagaContentDigest
                || label.Sensitivity != ContentSensitivity.CovenantDerived || leaf.Sensitivity != ContentSensitivity.CovenantDerived
                || !label.Provenance.Equals(evidence.Provenance) || !label.Provenance.Equals(leaf.Provenance)
                || label.SensitivityDigest != evidence.PartitionDigest || leaf.LabelDigests.IsDefault || leaf.LabelDigests.Length != 1
                || leaf.LabelDigests[0] != label.LabelDigest || !evidence.LabelDigests.Contains(label.LabelDigest))
            {
                return Invalid();
            }
        }

        return snapshots;
    }

    private static string Input(LongRestSnapshot[] snapshots, IReadOnlyList<LongRestSourceLeaf> leaves, LongRestProtectedEvidence evidence) =>
        ProtectedLongRestCanonicalHash.Input(snapshots, evidence, leaves.Select(leaf => (leaf.Snapshot.MemoryId, leaf.CanonicalLabel!.LabelId, leaf.CanonicalLabel.LabelDigest)).ToArray());

    private static Result<LongRestSnapshot[]> Invalid() => new Error(ErrorCodes.LongRest.InvalidRequest, "Protected inputs require complete exact retained labels and one identical nonempty partition.");
}
