using System.Collections.Immutable;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The exact scoped identity one mutation targets.
/// </summary>
/// <remarks>
/// <paramref name="IdentityDigest"/> is supplied by the caller that canonicalized the request rather
/// than recomputed here. The kernel persists it as durable evidence of what the operator or agent
/// actually asked for; recomputing it from the parsed fields would only prove that the kernel agrees
/// with itself.
/// </remarks>
public sealed record CovenantMutationTarget(
    CovenantOperationScope Scope,
    CovenantKey NormalizedKey,
    string AuthoredKey,
    CovenantLane Lane,
    CovenantDigest IdentityDigest);

/// <summary>
/// The compiled artifact a <c>Set</c> appends. A retirement carries none.
/// </summary>
public sealed record CovenantMutationArtifact(
    string AuthoredContent,
    string CompiledContent,
    CovenantDigest AuthoredHash,
    CovenantDigest RenderedHash,
    int CompiledByteCost,
    int RequiredFenceLength,
    int CompilerPolicyVersion,
    int RendererPolicyVersion);

/// <summary>
/// One immutable attachment-provenance leaf, exactly as it will be stored.
/// </summary>
public sealed record CovenantMutationProvenanceLeaf(
    int Ordinal,
    Guid AttachmentId,
    Guid AttachmentVersionIdentity,
    string LogicalKey,
    CovenantDigest ContentHash,
    CovenantMaterializationSourceRange SourceRangeKind,
    long? SourceStart,
    long? SourceEnd,
    Guid? SourceTurnId,
    string? MaterializationReference);

/// <summary>
/// The authenticated evidence that permits one mutation.
/// </summary>
public sealed record CovenantMutationAuthorization(
    CovenantDigest RequestIdempotencyDigest,
    CovenantDigest AuthorizationDigest,
    CovenantDigest FinalMutationDigest,
    CovenantDigest ResponseReceiptDigest,
    CovenantAuthorizationMode Mode,
    CovenantDigest? WardReceiptDigest,
    CovenantDigest? PreflightBodyDigest);

/// <summary>
/// One validated mutation, carrying everything the kernel needs and nothing it has to re-derive.
/// </summary>
/// <remarks>
/// Validation happens in the constructor so an invalid intent cannot reach a transaction at all. The
/// kernel therefore has no "is this well formed" branch and no separate authenticated-command
/// parameter: if an intent exists, its shape is already sound, and everything left to check is
/// state the database owns.
/// </remarks>
public sealed class CovenantMutationIntent
{

    public CovenantMutationIntent(
        Guid mutationId,
        CovenantMutationKind kind,
        CovenantOperation operation,
        CovenantOrigin origin,
        CovenantMutationTarget target,
        long expectedLaneRevision,
        bool reactivate,
        long expectedKeyEpoch,
        CovenantMutationArtifact? artifact,
        ImmutableArray<CovenantMutationProvenanceLeaf> provenance,
        CovenantMutationAuthorization authorization,
        Guid? sourceTurnId,
        string? sourceToolCallId,
        CovenantDigest? basePlanDigest,
        CovenantDigest? admissionReceiptDigest)
    {

        ArgumentNullException.ThrowIfNull(target);

        ArgumentNullException.ThrowIfNull(authorization);

        MutationId = CovenantValidation.RequireNonEmpty(mutationId, nameof(mutationId));

        Kind = kind is >= CovenantMutationKind.OperatorSet and <= CovenantMutationKind.AgentRetire
            ? kind
            : throw new ArgumentOutOfRangeException(nameof(kind));

        Operation = operation is CovenantOperation.Set or CovenantOperation.Retire
            ? operation
            : throw new ArgumentOutOfRangeException(nameof(operation));

        Origin = origin is >= CovenantOrigin.Operator and <= CovenantOrigin.AgentApproved
            ? origin
            : throw new ArgumentOutOfRangeException(nameof(origin));

        Target = target;

        if (expectedLaneRevision < 0)
        {

            throw new ArgumentOutOfRangeException(nameof(expectedLaneRevision));

        }

        ExpectedLaneRevision = expectedLaneRevision;

        Reactivate = reactivate;

        if (expectedKeyEpoch < 0)
        {

            throw new ArgumentOutOfRangeException(nameof(expectedKeyEpoch));

        }

        ExpectedKeyEpoch = expectedKeyEpoch;

        if ((operation == CovenantOperation.Set) != (artifact is not null))
        {

            throw new ArgumentException(
                "A Set carries exactly one compiled artifact and a Retire carries none.",
                nameof(artifact));

        }

        Artifact = artifact;

        if (provenance.IsDefault)
        {

            throw new ArgumentException("The provenance vector must be initialized.", nameof(provenance));

        }

        if (provenance.Length > CovenantLimits.MaxAttachmentSourcesPerAgentMutation)
        {

            throw new ArgumentException("The provenance vector exceeds its bound.", nameof(provenance));

        }

        Provenance = provenance;

        Authorization = authorization;

        // Global Proposed is unrepresentable by construction rather than by a database CHECK the
        // caller might learn about only at commit time.
        if (target.Scope.Kind == CovenantScope.Global && target.Lane == CovenantLane.Proposed)
        {

            throw new ArgumentException("Global Covenant content cannot use the Proposed lane.", nameof(target));

        }

        bool agentAuthored = origin is CovenantOrigin.AgentProposed or CovenantOrigin.AgentApproved;

        if (agentAuthored
            && (sourceTurnId is not { } turn
                || turn == Guid.Empty
                || string.IsNullOrEmpty(sourceToolCallId)
                || basePlanDigest is null))
        {

            throw new ArgumentException(
                "An agent-authored mutation must name the turn, tool call, and base plan it came from.",
                nameof(sourceTurnId));

        }

        if (!agentAuthored && (sourceToolCallId is not null || basePlanDigest is not null))
        {

            throw new ArgumentException(
                "An operator-authored mutation has no tool call or base plan to borrow.",
                nameof(sourceToolCallId));

        }

        SourceTurnId = sourceTurnId;

        SourceToolCallId = sourceToolCallId;

        BasePlanDigest = basePlanDigest;

        AdmissionReceiptDigest = admissionReceiptDigest;

    }

    public Guid MutationId { get; }

    public CovenantMutationKind Kind { get; }

    public CovenantOperation Operation { get; }

    public CovenantOrigin Origin { get; }

    public CovenantMutationTarget Target { get; }

    public long ExpectedLaneRevision { get; }

    public bool Reactivate { get; }

    public long ExpectedKeyEpoch { get; }

    public CovenantMutationArtifact? Artifact { get; }

    public ImmutableArray<CovenantMutationProvenanceLeaf> Provenance { get; }

    public CovenantMutationAuthorization Authorization { get; }

    public Guid? SourceTurnId { get; }

    public string? SourceToolCallId { get; }

    public CovenantDigest? BasePlanDigest { get; }

    public CovenantDigest? AdmissionReceiptDigest { get; }

}

/// <summary>
/// One sealed batch of validated intents plus the generation facts they all share.
/// </summary>
/// <remarks>
/// Sealed and immutable: the kernel receives a batch or it receives nothing. There is no overload
/// that takes a loose authenticated command or a compiled artifact beside it, because a second
/// parameter is a second thing that could disagree with the first.
/// </remarks>
public sealed class CovenantMutationBatch
{

    public CovenantMutationBatch(
        Guid datasetGeneration,
        long expectedKeyReclamationEpoch,
        long expectedCampaignRegistryEpoch,
        DateTimeOffset committedAtUtc,
        ImmutableArray<CovenantMutationIntent> intents)
    {

        DatasetGeneration = CovenantValidation.RequireNonEmpty(datasetGeneration, nameof(datasetGeneration));

        ExpectedKeyReclamationEpoch = CovenantValidation.RequirePositive(
            expectedKeyReclamationEpoch,
            nameof(expectedKeyReclamationEpoch));

        ExpectedCampaignRegistryEpoch = CovenantValidation.RequirePositive(
            expectedCampaignRegistryEpoch,
            nameof(expectedCampaignRegistryEpoch));

        CommittedAtUtc = committedAtUtc;

        if (intents.IsDefault)
        {

            throw new ArgumentException("The mutation intent vector must be initialized.", nameof(intents));

        }

        if (intents.Length > CovenantLimits.MaxStagedMutationsPerTurn)
        {

            throw new ArgumentException(
                "A Covenant mutation batch exceeds the staged-mutation bound for one turn.",
                nameof(intents));

        }

        HashSet<Guid> mutationIds = [];

        HashSet<(CovenantScope Scope, Guid Campaign, string Key, CovenantLane Lane)> targets = [];

        foreach (CovenantMutationIntent intent in intents)
        {

            ArgumentNullException.ThrowIfNull(intent, nameof(intents));

            if (!mutationIds.Add(intent.MutationId))
            {

                throw new ArgumentException("A batch cannot contain the same mutation ID twice.", nameof(intents));

            }

            if (!targets.Add((
                intent.Target.Scope.Kind,
                intent.Target.Scope.CampaignId ?? Guid.Empty,
                intent.Target.NormalizedKey.Value,
                intent.Target.Lane)))
            {

                throw new ArgumentException(
                    "A batch cannot contain two mutations for the same scope, key, and lane.",
                    nameof(intents));

            }

        }

        Intents = intents;

    }

    public Guid DatasetGeneration { get; }

    public long ExpectedKeyReclamationEpoch { get; }

    public long ExpectedCampaignRegistryEpoch { get; }

    public DateTimeOffset CommittedAtUtc { get; }

    public ImmutableArray<CovenantMutationIntent> Intents { get; }

}

/// <summary>
/// The durable outcome of one mutation.
/// </summary>
/// <remarks>
/// A <see cref="CovenantMutationOutcome.NoChange"/> receipt is written and returned exactly like an
/// applied one. Recording only the mutations that changed something would make a replay of a
/// deliberate no-op indistinguishable from a mutation that never arrived.
/// </remarks>
public sealed record CovenantMutationReceipt(
    Guid MutationId,
    CovenantMutationOutcome Outcome,
    CovenantMutationKind Kind,
    CovenantOperationScope Scope,
    string NormalizedKey,
    CovenantLane Lane,
    Guid EntryId,
    Guid? ResultingVersionId,
    long? ResultingLaneRevision,
    CovenantDigest RequestIdempotencyDigest,
    CovenantDigest FinalMutationDigest,
    CovenantDigest ResponseReceiptDigest,
    bool Replayed);
