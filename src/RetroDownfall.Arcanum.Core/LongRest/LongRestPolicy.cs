using System.Text;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>A pure bounded policy over exact retained observations.</summary>
public static class LongRestPolicy
{
    public const int PolicyVersion = 1;

    public const int MaxTargets = 16;

    /// <summary>The maximum UTF-8 bytes in one opaque identity, before canonical allocation.</summary>
    public const int MaxIdentityBytes = 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Refuses malformed or unbounded declarations before any owning-store read.</summary>
    public static Result ValidateRequest(LongRestRequest request)
    {
        if (request is null
            || request.Kind is not (LongRestTransformationKind.ExactDuplicates
                or LongRestTransformationKind.EquivalentObservations or LongRestTransformationKind.Supersession)
            || request.Targets is null || request.Targets.Length is < 1 or > MaxTargets)
        {
            return Invalid("A transformation must declare a known kind and one to sixteen exact targets.");
        }

        HashSet<string> memoryIds = new(StringComparer.Ordinal);

        HashSet<string> versionIds = new(StringComparer.Ordinal);

        foreach (LongRestTarget target in request.Targets)
        {
            if (target is null || !ValidIdentity(target.MemoryId) || !ValidIdentity(target.ExpectedVersionId)
                || !ValidHash(target.ExpectedContentHash)
                || !memoryIds.Add(target.MemoryId) || !versionIds.Add(target.ExpectedVersionId))
            {
                return Invalid("Targets must have distinct exact identities and 32-byte hexadecimal content hashes.");
            }
        }

        if ((request.Kind == LongRestTransformationKind.Supersession && request.SurvivorVersionId is null)
            || (request.Kind != LongRestTransformationKind.Supersession && request.SurvivorVersionId is not null)
            || (request.SurvivorVersionId is not null
                && (!ValidIdentity(request.SurvivorVersionId) || !versionIds.Contains(request.SurvivorVersionId))))
        {
            return Invalid("Only supersession declares a survivor, and it must declare one exact target version.");
        }

        return Result.Success();
    }

    /// <summary>
    /// Identifies bound inputs before current-head validation, allowing the store to return an
    /// already committed receipt using its retained context after those inputs ceased being current.
    /// </summary>
    public static Result<string> Identify(LongRestRequest request, IReadOnlyList<LongRestSnapshot> snapshots)
    {
        Result<LongRestSnapshot[]> prepared = Prepare(request, snapshots);

        if (prepared.IsFailure)
        {
            return Result<string>.Failure(prepared.Error);
        }

        string inputHash = LongRestCanonicalHash.Input(prepared.Value);

        return LongRestCanonicalHash.Receipt(request.Kind, inputHash, request.SurvivorVersionId);
    }

    /// <summary>Decides without time, scores, provider work, or mutation of its supplied snapshots.</summary>
    public static Result<LongRestReceipt> Evaluate(LongRestRequest request, IReadOnlyList<LongRestSnapshot> snapshots)
    {
        Result<LongRestSnapshot[]> prepared = Prepare(request, snapshots);

        if (prepared.IsFailure)
        {
            return Result<LongRestReceipt>.Failure(prepared.Error);
        }

        LongRestSnapshot[] ordered = prepared.Value;

        if (ordered.Any(snapshot => !snapshot.IsCurrent))
        {
            return Result<LongRestReceipt>.Failure(new Error("LongRest.StaleInput", "An exact target version is no longer current."));
        }

        string inputHash = LongRestCanonicalHash.Input(ordered);

        string receiptId = LongRestCanonicalHash.Receipt(request.Kind, inputHash, request.SurvivorVersionId);

        LongRestSnapshot survivor = request.SurvivorVersionId is { } requested
            ? ordered.Single(snapshot => snapshot.VersionId == requested)
            : ordered.OrderBy(snapshot => OriginPriority(snapshot.Origin))
                .ThenBy(snapshot => snapshot.MemoryId, StringComparer.Ordinal)
                .First();

        LongRestSnapshot[] eliminated = ordered.Where(snapshot => snapshot.VersionId != survivor.VersionId).ToArray();

        LongRestReason reason = Decide(request.Kind, ordered, survivor, eliminated);

        LongRestOutcome outcome = reason == LongRestReason.None ? LongRestOutcome.Applied : LongRestOutcome.NoChange;

        LongRestSnapshot? output = outcome == LongRestOutcome.Applied ? survivor : null;

        LongRestSnapshot[] superseded = outcome == LongRestOutcome.Applied ? eliminated : [];

        string outputHash = LongRestCanonicalHash.Output(request.Kind, outcome, reason, output, superseded);

        return new LongRestReceipt(receiptId, PolicyVersion, request.Kind, outcome, reason, inputHash, outputHash,
            ordered.Select(Target).ToArray(), output?.MemoryId, output?.ClaimId, output?.VersionId,
            superseded.Select(snapshot => snapshot.VersionId).ToArray());
    }

    private static LongRestReason Decide(
        LongRestTransformationKind kind,
        LongRestSnapshot[] snapshots,
        LongRestSnapshot survivor,
        LongRestSnapshot[] eliminated)
    {
        if (snapshots.Length == 1)
        {
            return LongRestReason.SingleInput;
        }

        if (snapshots.Any(snapshot => snapshot.PinnedAtUtc is not null))
        {
            return LongRestReason.Pinned;
        }

        if (snapshots.Any(snapshot => snapshot.RetiredAtUtc is not null))
        {
            return LongRestReason.Retired;
        }

        if (snapshots.Any(snapshot => snapshot.ScopeKind is not (SagaMemoryScopeKind.Global or SagaMemoryScopeKind.Campaign)
            || snapshot.ScopeKind != survivor.ScopeKind || snapshot.CampaignId != survivor.CampaignId))
        {
            return LongRestReason.DifferentScope;
        }

        if (snapshots.Any(snapshot => snapshot.Sensitivity != ContentSensitivity.None || snapshot.HasSensitivityLabel))
        {
            return LongRestReason.Protected;
        }

        if (snapshots.Any(snapshot => snapshot.IsConsolidated))
        {
            return LongRestReason.AlreadyConsolidated;
        }

        if (!survivor.HasEmbedding)
        {
            return LongRestReason.EmbeddingMissing;
        }

        if (kind == LongRestTransformationKind.ExactDuplicates
            && snapshots.Any(snapshot => snapshot.ContentHashFormat != survivor.ContentHashFormat
                || snapshot.ContentHash != survivor.ContentHash))
        {
            return LongRestReason.DifferentContent;
        }

        if (snapshots.Any(snapshot => snapshot.ValidToUtc is not null)
            && snapshots.Any(snapshot => snapshot.ValidFromUtc != survivor.ValidFromUtc || snapshot.ValidToUtc != survivor.ValidToUtc))
        {
            return LongRestReason.IncompatibleValidity;
        }

        if (snapshots.Any(snapshot => !snapshot.Dependencies.SequenceEqual(survivor.Dependencies))
            || eliminated.Any(snapshot => snapshot.IsTransformationOutput
                || snapshot.IncomingDependencies.Any(edge => edge.IsCurrent && edge.Relation == AnnalDependencyRelation.DerivedFrom)))
        {
            return LongRestReason.DependencyConflict;
        }

        return LongRestReason.None;
    }

    private static Result<LongRestSnapshot[]> Prepare(LongRestRequest request, IReadOnlyList<LongRestSnapshot> snapshots)
    {
        Result validRequest = ValidateRequest(request);

        if (validRequest.IsFailure)
        {
            return Result<LongRestSnapshot[]>.Failure(validRequest.Error);
        }

        if (snapshots is null || snapshots.Count != request.Targets.Length)
        {
            return Result<LongRestSnapshot[]>.Failure(Invalid("Every exact target requires one complete policy snapshot.").Error);
        }

        Dictionary<string, LongRestTarget> targets = request.Targets.ToDictionary(target => target.MemoryId, StringComparer.Ordinal);

        HashSet<string> seenMemories = new(StringComparer.Ordinal);

        HashSet<string> seenClaims = new(StringComparer.Ordinal);

        LongRestSnapshot[] normalized = new LongRestSnapshot[snapshots.Count];

        for (int index = 0; index < snapshots.Count; index++)
        {
            LongRestSnapshot snapshot = snapshots[index];

            if (!ValidSnapshot(snapshot) || !seenMemories.Add(snapshot.MemoryId) || !seenClaims.Add(snapshot.ClaimId))
            {
                return Result<LongRestSnapshot[]>.Failure(Invalid("Policy snapshots must carry valid, distinct exact retained coordinates and bounded dependencies.").Error);
            }

            if (!targets.TryGetValue(snapshot.MemoryId, out LongRestTarget? target)
                || target.ExpectedVersionId != snapshot.VersionId
                || !StringComparer.OrdinalIgnoreCase.Equals(target.ExpectedContentHash, snapshot.ContentHash))
            {
                return Result<LongRestSnapshot[]>.Failure(new Error("LongRest.StaleInput", "A snapshot does not match its exact observed version and content hash."));
            }

            normalized[index] = snapshot with
            {
                ContentHash = snapshot.ContentHash.ToUpperInvariant(),

                Dependencies = snapshot.Dependencies.OrderBy(edge => edge.VersionId, StringComparer.Ordinal)
                    .ThenBy(edge => edge.Relation).ThenBy(edge => edge.Ordinal).ToArray(),

                IncomingDependencies = snapshot.IncomingDependencies.OrderBy(edge => edge.DependentVersionId, StringComparer.Ordinal)
                    .ThenBy(edge => edge.Relation).ThenBy(edge => edge.IsCurrent).ToArray(),
            };
        }

        Array.Sort(normalized, (left, right) => StringComparer.Ordinal.Compare(left.MemoryId, right.MemoryId));

        return normalized;
    }

    private static bool ValidSnapshot(LongRestSnapshot? snapshot)
    {
        if (snapshot is null || !ValidIdentity(snapshot.MemoryId) || !ValidIdentity(snapshot.ClaimId)
            || !ValidIdentity(snapshot.VersionId) || snapshot.Revision < 1 || !ValidHash(snapshot.ContentHash)
            || snapshot.ContentHashFormat is not (AnnalContentHashFormat.LegacyStoreDigest or AnnalContentHashFormat.LexiconStructuredSnapshot)
            || snapshot.Origin is not (AnnalOrigin.OperatorStated or AnnalOrigin.AgentAsserted or AnnalOrigin.AgentExtracted or AnnalOrigin.SystemBackfilled)
            || snapshot.ScopeKind is not (SagaMemoryScopeKind.Unclassified or SagaMemoryScopeKind.Global or SagaMemoryScopeKind.Campaign or SagaMemoryScopeKind.LegacyUnresolved)
            || ((snapshot.ScopeKind == SagaMemoryScopeKind.Campaign) != (snapshot.CampaignId is not null))
            || snapshot.CampaignId == Guid.Empty || snapshot.SourceSessionId == Guid.Empty
            || (snapshot.Origin == AnnalOrigin.SystemBackfilled && snapshot.SourceSessionId is not null)
            || snapshot.Sensitivity is not (ContentSensitivity.None or ContentSensitivity.CovenantDerived)
            || snapshot.ValidToUtc < snapshot.ValidFromUtc
            || snapshot.Dependencies is null || snapshot.Dependencies.Length > AnnalLimits.MaxDependenciesPerVersion
            || snapshot.IncomingDependencies is null || snapshot.IncomingDependencies.Length > MaxTargets)
        {
            return false;
        }

        HashSet<string> dependencies = new(StringComparer.Ordinal);

        HashSet<int> ordinals = [];

        foreach (LongRestDependency edge in snapshot.Dependencies)
        {
            if (edge is null || !ValidIdentity(edge.VersionId) || edge.VersionId == snapshot.VersionId
                || !ValidRelation(edge.Relation) || edge.Ordinal is < 1 or > AnnalLimits.MaxDependenciesPerVersion
                || !dependencies.Add(edge.VersionId) || !ordinals.Add(edge.Ordinal))
            {
                return false;
            }
        }

        HashSet<string> incoming = new(StringComparer.Ordinal);

        foreach (LongRestIncomingDependency edge in snapshot.IncomingDependencies)
        {
            if (edge is null || !ValidIdentity(edge.DependentVersionId) || edge.DependentVersionId == snapshot.VersionId
                || !ValidRelation(edge.Relation) || !incoming.Add(edge.DependentVersionId))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdentityBytes)
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(value) <= MaxIdentityBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool ValidHash(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');

    private static bool ValidRelation(AnnalDependencyRelation relation) =>
        relation is AnnalDependencyRelation.Supersedes or AnnalDependencyRelation.DerivedFrom or AnnalDependencyRelation.Corroborates;

    private static int OriginPriority(AnnalOrigin origin) => origin switch
    {
        AnnalOrigin.OperatorStated => 0,

        AnnalOrigin.AgentAsserted => 1,

        AnnalOrigin.AgentExtracted => 2,

        AnnalOrigin.SystemBackfilled => 3,

        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    private static LongRestReceiptTarget Target(LongRestSnapshot snapshot) =>
        new(snapshot.MemoryId, snapshot.ClaimId, snapshot.VersionId, snapshot.Revision, snapshot.ContentHashFormat, snapshot.ContentHash);

    private static Result Invalid(string message) => Result.Failure(new Error("LongRest.InvalidRequest", message));
}
