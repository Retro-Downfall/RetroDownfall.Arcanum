using System.Collections.Immutable;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Serialization;

using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.LongRest;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<LongRestSweepScopeKind>))]
public enum LongRestSweepScopeKind : byte
{
    Global = 1,

    Campaign = 2,
}

public sealed record LongRestSweepRequest(LongRestSweepScopeKind Scope, Guid? CampaignId);

public readonly record struct LongRestSweepScope
{
    private LongRestSweepScope(LongRestSweepScopeKind kind, Guid? campaignId, CovenantDigest? partitionDigest)
    {
        Kind = kind;

        CampaignId = campaignId;

        ProtectedPartitionDigest = partitionDigest;

        IsInitialized = true;
    }

    public LongRestSweepScopeKind Kind { get; }

    public Guid? CampaignId { get; }

    public CovenantDigest? ProtectedPartitionDigest { get; }

    public bool IsInitialized { get; }

    public static Result<LongRestSweepScope> Create(LongRestSweepRequest request, CovenantDigest? partitionDigest = null)
    {
        if (request is null
            || request.Scope is not (LongRestSweepScopeKind.Global or LongRestSweepScopeKind.Campaign)
            || (request.Scope == LongRestSweepScopeKind.Global && request.CampaignId is not null)
            || (request.Scope == LongRestSweepScopeKind.Campaign && (request.CampaignId is null || request.CampaignId == Guid.Empty))
            || partitionDigest is { IsValid: false })
        {
            return Result<LongRestSweepScope>.Failure(new Error(ErrorCodes.LongRest.InvalidRequest, "A sweep requires exact Global or one nonempty Campaign scope and a valid optional partition digest."));
        }

        return Result<LongRestSweepScope>.Success(new LongRestSweepScope(request.Scope, request.CampaignId, partitionDigest));
    }
}

public sealed record LongRestConfigurationStamp(Guid BootId, long Epoch, CovenantDigest ValueDigest)
{
    public bool IsValid => BootId != Guid.Empty && Epoch >= 0 && ValueDigest.IsValid;
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<LongRestSweepPhase>))]
public enum LongRestSweepPhase : byte
{
    Capture = 1,

    Hierarchy = 2,

    Publish = 3,

    Discover = 4,

    Adjudicate = 5,

    Completed = 6,

    Invalidated = 7,

    Deferred = 8,

    Retired = 9,
}

public enum LongRestWorkKind : byte
{
    SourcePage = 1,

    HierarchyFold = 2,

    Anchor = 3,
}

public enum LongRestDispatchAttemptState : byte
{
    Prepared = 1,

    Disclosed = 2,

    Sent = 3,

    Accepted = 4,

    Invalid = 5,

    Failed = 6,
}

/// <summary>How an in-frame settlement closes a physical attempt whose SDK call ended without a result and without cancellation.</summary>
public enum LongRestUnresolvedSend : byte
{
    /// <summary>
    /// The call may have reached the provider, so usage and billing are unknown. The attempt settles as failed with no
    /// completion evidence and counts toward the Unknown-Usage Cap. Usage is never estimated or written as zero.
    /// </summary>
    UsageUnknown = 1,

    /// <summary>
    /// The provider could not be reached before any request byte left the process (name resolution, connection or TLS
    /// failure). The attempt settles as failed with no completion evidence and does not count toward the cap.
    /// </summary>
    NotSent = 2,
}

/// <summary>Content-free owner and source facts; canonical roots are digests, never host paths.</summary>
public sealed record LongRestSweepFence(
    CanonicalCampaignContext CanonicalScope,
    long InsertionHighWatermark,
    Guid DatasetGeneration,
    long AdmissionGeneration,
    long RuntimeAuthorityGeneration,
    long CapabilityGeneration,
    LongRestConfigurationStamp Configuration);

public sealed record LongRestSweep(
    Guid OperationId,
    Guid GenerationId,
    LongRestSweepScope Scope,
    LongRestSweepPhase Phase,
    long Revision,
    string? CommittedItemCursor,
    CovenantDigest? CommittedWorkDigest);

/// <summary>
/// One worker discovery step: the next clean sweep it may drive, if any, and the earliest instant a parked sweep that is
/// still cooling becomes readmittable. The candidate is a hint only; readmission re-proves everything atomically.
/// </summary>
public sealed record LongRestRunnableSweep(Guid? OperationId, DateTimeOffset? NextDueAt);

public sealed record LongRestSweepProgress(
    Guid OperationId,
    Guid GenerationId,
    LongRestSweepScopeKind Scope,
    Guid? CampaignId,
    LongRestSweepPhase Phase,
    long Revision,
    string? CommittedItemCursor,
    string? DeferredReason,
    CovenantDigest? ProtectedPartitionDigest = null);

/// <summary>Only stable committed coordinates enter an encrypted recovery checkpoint.</summary>
public sealed record LongRestSweepCheckpoint(
    int Version,
    Guid OperationId,
    LongRestSweepScopeKind Scope,
    Guid? CampaignId,
    Guid? GenerationId,
    LongRestSweepPhase? Phase,
    long Revision,
    string? CommittedItemCursor,
    CovenantDigest? CommittedWorkDigest,
    CovenantDigest? ProtectedPartitionDigest = null);

public sealed record LongRestWorkIdentity(
    Guid GenerationId,
    Guid OperationId,
    string ItemId,
    LongRestWorkKind Kind,
    LongRestSweepScope Scope,
    LongRestSweepFence Fence,
    CovenantDigest SourceDigest,
    CovenantDigest RecipeDigest);

public sealed record LongRestDispatchAttempt(
    Guid AttemptId,
    LongRestWorkIdentity Work,
    LongRestProviderPayloadKind PayloadKind,
    ulong PhysicalOrdinal,
    long Revision,
    LongRestDispatchAttemptState State,
    CovenantDigest PayloadDigest,
    CovenantDigest DestinationDigest,
    CovenantDigest? DisclosureReceiptDigest);

public sealed record LongRestDispatchCompletion(
    LongRestDispatchAttemptState State,
    LongRestProviderUsage Usage,
    decimal? CostUsd,
    string? FailureCode);

public sealed record LongRestPaidEvidence(
    LongRestDispatchAttempt Attempt,
    long InputTokens,
    long OutputTokens,
    decimal? CostUsd);

public enum LongRestAdjudicationKind : byte
{
    EquivalentObservations = 1,

    Supersession = 2,

    NoChange = 3,
}

public sealed record LongRestAdjudicationDecision(LongRestAdjudicationKind Kind, string? SurvivorVersionId);

public sealed record LongRestProtectedEvidence(
    CovenantDigest PartitionDigest,
    GenerationProvenance Provenance,
    ImmutableArray<CovenantDigest> LabelDigests);

public sealed record LongRestAdjudication(
    LongRestWorkIdentity Work,
    ImmutableArray<LongRestSourceLeaf> Inputs,
    LongRestAdjudicationDecision Decision,
    CovenantDigest PayloadDigest,
    LongRestPaidEvidence? PaidEvidence,
    LongRestProtectedEvidence? ProtectedEvidence);

public sealed record LongRestSweepWork(
    LongRestWorkIdentity Identity,
    long ExpectedSweepRevision,
    ImmutableArray<LongRestSourceLeaf> Inputs,
    LongRestHierarchyFold? Fold,
    LongRestAdjudication? AcceptedAdjudication)
{
    /// <summary>The memories this work acts on, in input order: the leaves every gate in front of its effects re-proves in full.</summary>
    internal string[] MemoryIds()
    {
        string[] ids = new string[Inputs.Length];

        for (int index = 0; index < ids.Length; index++)
        {
            ids[index] = Inputs[index].Snapshot.MemoryId;
        }

        return ids;
    }
}
