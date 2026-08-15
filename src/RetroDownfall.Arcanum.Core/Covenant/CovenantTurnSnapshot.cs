using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

namespace RetroDownfall.Arcanum.Core.Covenant;

public enum CovenantSnapshotCandidateIntegrity : byte
{
    Verified = 1,
    Quarantined = 2,
    Invalid = 3
}

public sealed class CovenantSnapshotCandidate
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly SnapshotCandidateDigestInput _digestInput;

    public CovenantSnapshotCandidate(
        ulong searchDocumentId,
        Guid entryId,
        Guid versionId,
        CovenantKey normalizedKey,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane,
        CovenantOperation operation,
        CovenantOrigin origin,
        ulong revision,
        Guid? predecessorId,
        uint compilerPolicy,
        uint rendererPolicy,
        CovenantDigest authoredDigest,
        CovenantDigest fragmentDigest,
        uint provenanceCount,
        CovenantDigest provenanceDigest,
        ImmutableArray<byte> compiledFragment,
        CovenantSnapshotCandidateIntegrity integrity)
    {
        if (searchDocumentId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(searchDocumentId));
        }

        if (revision == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        SearchDocumentId = searchDocumentId;
        EntryId = CovenantValidation.RequireNonEmpty(entryId, nameof(entryId));
        VersionId = CovenantValidation.RequireNonEmpty(versionId, nameof(versionId));
        NormalizedKey = ValidateKey(normalizedKey);
        Scope = ValidateScope(scope, campaignId);
        CampaignId = campaignId;
        Lane = ValidateLane(lane, scope);
        Operation = ValidateOperation(operation);
        Origin = ValidateOrigin(origin, lane);
        Revision = revision;
        PredecessorId = ValidatePredecessor(predecessorId, versionId);
        CompilerPolicy = compilerPolicy;
        RendererPolicy = rendererPolicy;
        AuthoredDigest = CovenantValidation.RequireDigest(authoredDigest, nameof(authoredDigest));
        FragmentDigest = CovenantValidation.RequireDigest(fragmentDigest, nameof(fragmentDigest));

        if (provenanceCount > CovenantLimits.MaxVersionSources)
        {
            throw new ArgumentException("Covenant provenance exceeds the bounded source count.", nameof(provenanceCount));
        }

        ProvenanceCount = provenanceCount;
        ProvenanceDigest = CovenantValidation.RequireDigest(provenanceDigest, nameof(provenanceDigest));
        Integrity = ValidateIntegrity(integrity);
        CompiledFragment = FreezeFragment(compiledFragment, Integrity);
        CompiledBytes = checked((uint)CompiledFragment.Length);
        _digestInput = new SnapshotCandidateDigestInput(
            SearchDocumentId,
            EntryId,
            VersionId,
            Scope,
            CampaignId,
            Lane,
            Operation,
            Origin,
            Revision,
            PredecessorId,
            CompilerPolicy,
            RendererPolicy,
            AuthoredDigest,
            FragmentDigest,
            ProvenanceCount,
            ProvenanceDigest,
            CompiledBytes);
    }

    public ulong SearchDocumentId { get; }

    public Guid EntryId { get; }

    public Guid VersionId { get; }

    public CovenantKey NormalizedKey { get; }

    public CovenantScope Scope { get; }

    public Guid? CampaignId { get; }

    public CovenantLane Lane { get; }

    public CovenantOperation Operation { get; }

    public CovenantOrigin Origin { get; }

    public ulong Revision { get; }

    public Guid? PredecessorId { get; }

    public uint CompilerPolicy { get; }

    public uint RendererPolicy { get; }

    public CovenantDigest AuthoredDigest { get; }

    public CovenantDigest FragmentDigest { get; }

    public uint ProvenanceCount { get; }

    public CovenantDigest ProvenanceDigest { get; }

    public ImmutableArray<byte> CompiledFragment { get; }

    public uint CompiledBytes { get; }

    public CovenantSnapshotCandidateIntegrity Integrity { get; }

    public SnapshotCandidateDigestInput ToDigestInput() =>
        _digestInput;

    private static CovenantKey ValidateKey(CovenantKey value)
    {
        try
        {
            return new CovenantKey(value.Value);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new ArgumentException("The normalized Covenant key is uninitialized or invalid.", nameof(value), exception);
        }
    }

    private static CovenantScope ValidateScope(CovenantScope scope, Guid? campaignId)
    {
        if (scope is not CovenantScope.Global and not CovenantScope.Campaign)
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        if ((scope == CovenantScope.Campaign) != campaignId.HasValue)
        {
            throw new ArgumentException("Campaign scope and Campaign identity must be present together.", nameof(campaignId));
        }

        if (campaignId is { } present)
        {
            CovenantValidation.RequireNonEmpty(present, nameof(campaignId));
        }

        return scope;
    }

    private static CovenantLane ValidateLane(CovenantLane lane, CovenantScope scope)
    {
        if (lane is not CovenantLane.Confirmed and not CovenantLane.Proposed)
        {
            throw new ArgumentOutOfRangeException(nameof(lane));
        }

        if (scope == CovenantScope.Global && lane == CovenantLane.Proposed)
        {
            throw new ArgumentException("Global Covenant candidates can only use the Confirmed lane.", nameof(lane));
        }

        return lane;
    }

    private static CovenantOperation ValidateOperation(CovenantOperation operation)
    {
        if (operation is not CovenantOperation.Set and not CovenantOperation.Retire)
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        return operation;
    }

    private static CovenantOrigin ValidateOrigin(CovenantOrigin origin, CovenantLane lane)
    {
        bool valid = lane switch
        {
            CovenantLane.Confirmed => origin is CovenantOrigin.Operator or CovenantOrigin.AgentApproved,
            CovenantLane.Proposed => origin == CovenantOrigin.AgentProposed,
            _ => false
        };

        if (!valid)
        {
            throw new ArgumentException("The Covenant origin is incompatible with its lane.", nameof(origin));
        }

        return origin;
    }

    private static Guid? ValidatePredecessor(Guid? predecessorId, Guid versionId)
    {
        if (predecessorId is not { } present)
        {
            return null;
        }

        CovenantValidation.RequireNonEmpty(present, nameof(predecessorId));

        if (present == versionId)
        {
            throw new ArgumentException("A Covenant version cannot be its own predecessor.", nameof(predecessorId));
        }

        return present;
    }

    private static ImmutableArray<byte> FreezeFragment(
        ImmutableArray<byte> compiledFragment,
        CovenantSnapshotCandidateIntegrity integrity)
    {
        if (compiledFragment.IsDefault)
        {
            throw new ArgumentException("A Covenant candidate requires an initialized compiled-fragment vector.", nameof(compiledFragment));
        }

        byte[] frozen = compiledFragment.ToArray();

        if (integrity != CovenantSnapshotCandidateIntegrity.Verified)
        {
            return ImmutableCollectionsMarshal.AsImmutableArray(frozen);
        }

        if (compiledFragment.IsEmpty)
        {
            throw new ArgumentException("A verified Set candidate requires a nonempty compiled fragment.", nameof(compiledFragment));
        }

        if (compiledFragment[^1] != (byte)'\n')
        {
            throw new ArgumentException("A compiled Covenant fragment must end with LF.", nameof(compiledFragment));
        }

        try
        {
            _ = StrictUtf8.GetCharCount(compiledFragment.AsSpan());
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException("A compiled Covenant fragment must contain strict UTF-8.", nameof(compiledFragment), exception);
        }

        return ImmutableCollectionsMarshal.AsImmutableArray(frozen);
    }

    private static CovenantSnapshotCandidateIntegrity ValidateIntegrity(CovenantSnapshotCandidateIntegrity integrity) =>
        integrity is CovenantSnapshotCandidateIntegrity.Verified
            or CovenantSnapshotCandidateIntegrity.Quarantined
            or CovenantSnapshotCandidateIntegrity.Invalid
                ? integrity
                : throw new ArgumentOutOfRangeException(nameof(integrity));
}

public sealed class CovenantTurnSnapshot
{
    private readonly record struct ScopedCandidateKey(
        CovenantScope Scope,
        Guid? CampaignId,
        string NormalizedKey);

    private readonly SnapshotDigestInput _digestInput;

    public CovenantTurnSnapshot(
        CovenantGenerationId datasetGeneration,
        Guid? canonicalCampaignId,
        ulong canonicalSearchSequence,
        ImmutableArray<CovenantSnapshotCandidate> candidates)
    {
        DatasetGeneration = ValidateGeneration(datasetGeneration);
        CanonicalCampaignId = ValidateCampaign(canonicalCampaignId);
        CanonicalSearchSequence = canonicalSearchSequence;
        Candidates = FreezeCandidates(candidates);
        _digestInput = new SnapshotDigestInput(
            DatasetGeneration.Value,
            CanonicalCampaignId,
            CanonicalSearchSequence,
            [.. Candidates.Select(static candidate => candidate.ToDigestInput())]);
        Digest = CovenantDigests.Snapshot(_digestInput);
    }

    public CovenantGenerationId DatasetGeneration { get; }

    public Guid? CanonicalCampaignId { get; }

    public ulong CanonicalSearchSequence { get; }

    public ImmutableArray<CovenantSnapshotCandidate> Candidates { get; }

    public CovenantDigest Digest { get; }

    public SnapshotDigestInput ToDigestInput() =>
        _digestInput;

    private static CovenantGenerationId ValidateGeneration(CovenantGenerationId value)
    {
        try
        {
            return new CovenantGenerationId(value.Value);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new ArgumentException("The Covenant dataset generation is uninitialized or invalid.", nameof(value), exception);
        }
    }

    private static Guid? ValidateCampaign(Guid? value)
    {
        if (value is { } present)
        {
            CovenantValidation.RequireNonEmpty(present, nameof(value));
        }

        return value;
    }

    private static ImmutableArray<CovenantSnapshotCandidate> FreezeCandidates(
        ImmutableArray<CovenantSnapshotCandidate> candidates)
    {
        if (candidates.IsDefault)
        {
            throw new ArgumentException("The Covenant snapshot candidate vector must be initialized.", nameof(candidates));
        }

        if (candidates.Length > CovenantLimits.MaxActiveSnapshotRows)
        {
            throw new ArgumentException("The active Covenant snapshot exceeds its row bound.", nameof(candidates));
        }

        CovenantSnapshotCandidate[] frozen = candidates.ToArray();
        HashSet<ulong> searchDocumentIds = [];
        HashSet<Guid> versionIds = [];
        Dictionary<ScopedCandidateKey, Guid> entryByScopedKey = [];
        Dictionary<Guid, ScopedCandidateKey> scopedKeyByEntry = [];
        HashSet<(ScopedCandidateKey ScopedKey, CovenantLane Lane)> scopedLaneHeads = [];

        foreach (CovenantSnapshotCandidate candidate in frozen)
        {
            ArgumentNullException.ThrowIfNull(candidate, nameof(candidates));

            if (candidate.Operation != CovenantOperation.Set)
            {
                throw new ArgumentException("A live Covenant snapshot can contain only Set heads.", nameof(candidates));
            }

            ScopedCandidateKey scopedKey = new(
                candidate.Scope,
                candidate.CampaignId,
                candidate.NormalizedKey.Value);

            if (!searchDocumentIds.Add(candidate.SearchDocumentId)
                || !versionIds.Add(candidate.VersionId))
            {
                throw new ArgumentException("A live Covenant snapshot must contain distinct head identities.", nameof(candidates));
            }

            if (entryByScopedKey.TryGetValue(scopedKey, out Guid scopedEntryId)
                && scopedEntryId != candidate.EntryId)
            {
                throw new ArgumentException(
                    "A scoped Covenant key must identify exactly one entry.",
                    nameof(candidates));
            }

            if (scopedKeyByEntry.TryGetValue(candidate.EntryId, out ScopedCandidateKey entryScopedKey)
                && entryScopedKey != scopedKey)
            {
                throw new ArgumentException(
                    "A Covenant entry must retain one scoped-key identity.",
                    nameof(candidates));
            }

            entryByScopedKey.TryAdd(scopedKey, candidate.EntryId);
            scopedKeyByEntry.TryAdd(candidate.EntryId, scopedKey);

            if (!scopedLaneHeads.Add((scopedKey, candidate.Lane)))
            {
                throw new ArgumentException(
                    "A live Covenant snapshot must contain one head per scoped key and lane.",
                    nameof(candidates));
            }
        }

        return [.. frozen];
    }
}
