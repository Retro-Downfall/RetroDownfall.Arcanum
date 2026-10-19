using System.Collections.Immutable;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryEvaluationSource>))]
public enum MemoryEvaluationSource
{
    Covenant = 1,

    Saga = 2,

    Lexicon = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryEvaluationCoordinateRole>))]
public enum MemoryEvaluationCoordinateRole
{
    Intervention = 1,

    Dependency = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryEvaluationArm>))]
public enum MemoryEvaluationArm
{
    Control = 1,

    Treatment = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryEvaluationVerdict>))]
public enum MemoryEvaluationVerdict
{
    Fail = 1,

    Pass = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryEvaluationPairRelation>))]
public enum MemoryEvaluationPairRelation
{
    Improved = 1,

    Regressed = 2,

    Equivalent = 3,

    Mixed = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryEvaluationState>))]
public enum MemoryEvaluationState
{
    Prepared = 1,

    Running = 2,

    Completed = 3,

    Interrupted = 4,

    Cancelled = 5
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryEvaluationOutcome>))]
public enum MemoryEvaluationOutcome
{
    Benefit = 1,

    Harm = 2,

    NoMeasurableEffect = 3,

    InsufficientEvidence = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryEvaluationReason>))]
public enum MemoryEvaluationReason
{
    None = 0,
    MixedOutcomes = 1,
    NotPrepared = 2,
    Interrupted = 3,
    Cancelled = 4,
    StaleAuthority = 5,
    StaleSource = 6,
    StaleConfiguration = 7,
    InvalidEvaluation = 8,
    InvalidOutput = 9,
    ModelMismatch = 10,
    ProviderFailure = 11,
    UsageFailure = 12,
    AccountingFailure = 13,
    Erased = 14,
    Incomplete = 15
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryEvaluationSourceLifecycle>))]
public enum MemoryEvaluationSourceLifecycle
{
    Active = 1,

    Retired = 2
}

public sealed record MemoryEvaluationOwnership(Guid DatasetGeneration, MemoryEvaluationScopeKind Scope, Guid? CampaignId);

/// <summary>Exact opaque identity and provenance only; never a source fragment or author-provided label.</summary>
public sealed record MemoryEvaluationSourceCoordinate(
    MemoryEvaluationCoordinateRole Role,
    MemoryEvaluationSource Store,
    string SubjectId,
    string? ClaimId,
    string VersionId,
    int HashFormat,
    CovenantDigest ContentDigest,
    AnnalDependencyRelation? DependencyRelation,
    AnnalOrigin? Origin,
    MemoryEvaluationScopeKind Scope,
    Guid? CampaignId,
    DateTimeOffset? ValidFromUtc,
    DateTimeOffset? ValidToUtc,
    DateTimeOffset? RecordedAtUtc,
    DateTimeOffset? RecordedUntilUtc,
    MemoryEvaluationSourceLifecycle Lifecycle,
    ContentSensitivity Sensitivity,
    string? CanonicalOwnerSessionId,
    string? SourceSessionId,
    Guid? Generation = null,
    int Revision = 1,
    CovenantLane? Lane = null,
    CovenantPlacement? Placement = null,
    CovenantOrigin? CovenantOrigin = null);

public sealed record MemoryEvaluationDependencyEdge(MemoryEvaluationSource Store, string DependentVersionId, string DependencyVersionId, AnnalDependencyRelation Relation, int Ordinal);

public sealed record MemoryEvaluationRegistration(
    Guid EvaluationId,
    Guid OperationId,
    DateTimeOffset CreatedAtUtc,
    MemoryEvaluationOwnership Ownership,
    CovenantDigest RequestDigest,
    CovenantDigest PlanDigest,
    CovenantDigest WorkloadDigest,
    CovenantDigest ProviderDigest,
    CovenantDigest ModelDigest,
    CovenantDigest OptionsDigest,
    CovenantDigest PromptDigest,
    CovenantDigest EvaluatorDigest,
    CovenantDigest ManifestDigest,
    int PairCount,
    int CheckCount,
    int PolicyVersion = 1,
    int FormatVersion = 1,
    ImmutableArray<MemoryEvaluationDependencyEdge> Dependencies = default,
    DateTimeOffset? SourceAsOfUtc = null)
{
    private readonly ImmutableArray<MemoryEvaluationDependencyEdge> _dependencies = MemoryEvaluationFrozenCollections.Copy(Dependencies);

    public ImmutableArray<MemoryEvaluationDependencyEdge> Dependencies
    {
        get => _dependencies;

        init => _dependencies = MemoryEvaluationFrozenCollections.Copy(value);
    }
}

public sealed record MemoryEvaluationCheckAggregate(int CheckIndex, MemoryEvaluationCheckKind Kind, CovenantDigest CheckPolicyDigest, int FailFail, int FailPass, int PassFail, int PassPass);

public sealed record MemoryEvaluationAggregate(
    int CompletedPairs,
    int ValidPairs,
    int InvalidPairs,
    int ImprovedPairs,
    int RegressedPairs,
    int EquivalentPairs,
    int MixedPairs,
    ImmutableArray<MemoryEvaluationCheckAggregate> Checks,
    CovenantDigest ResponseEvidenceDigest,
    MemoryEvaluationReason FailureReason = MemoryEvaluationReason.None)
{
    private readonly ImmutableArray<MemoryEvaluationCheckAggregate> _checks = MemoryEvaluationFrozenCollections.Copy(Checks);

    public ImmutableArray<MemoryEvaluationCheckAggregate> Checks
    {
        get => _checks;

        init => _checks = MemoryEvaluationFrozenCollections.Copy(value);
    }
}

public sealed record MemoryEvaluationDecision(MemoryEvaluationOutcome Outcome, MemoryEvaluationReason Reason);

public sealed record MemoryEvaluationTerminalReport(
    MemoryEvaluationRegistration Registration,
    DateTimeOffset CompletedAtUtc,
    MemoryEvaluationState State,
    MemoryEvaluationDecision Decision,
    MemoryEvaluationAggregate Aggregate,
    ImmutableArray<MemoryEvaluationSourceCoordinate> Sources,
    long? InputTokens,
    long? OutputTokens,
    decimal? Cost,
    long ElapsedMilliseconds,
    ImmutableArray<MemoryEvaluationDependencyEdge> Dependencies = default)
{
    private readonly ImmutableArray<MemoryEvaluationSourceCoordinate> _sources = MemoryEvaluationFrozenCollections.Copy(Sources);

    public ImmutableArray<MemoryEvaluationSourceCoordinate> Sources
    {
        get => _sources;

        init => _sources = MemoryEvaluationFrozenCollections.Copy(value);
    }

    private readonly ImmutableArray<MemoryEvaluationDependencyEdge> _dependencies = MemoryEvaluationFrozenCollections.Copy(Dependencies);

    public ImmutableArray<MemoryEvaluationDependencyEdge> Dependencies
    {
        get => _dependencies;

        init => _dependencies = MemoryEvaluationFrozenCollections.Copy(value);
    }
}

public sealed record MemoryEvaluationStoredEvidence(
    MemoryEvaluationRegistration Registration,
    ImmutableArray<MemoryEvaluationSourceCoordinate> Sources,
    MemoryEvaluationTerminalReport? Report,
    CovenantDigest? ReportDigest)
{
    private readonly ImmutableArray<MemoryEvaluationSourceCoordinate> _sources = MemoryEvaluationFrozenCollections.Copy(Sources);

    public ImmutableArray<MemoryEvaluationSourceCoordinate> Sources
    {
        get => _sources;

        init => _sources = MemoryEvaluationFrozenCollections.Copy(value);
    }
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryEvaluationPhase>))]
public enum MemoryEvaluationPhase
{
    Prepared = 1,

    Sending = 2,

    Evaluating = 3,

    Finalizing = 4
}

/// <summary>The sole serializable execution-progress shape. Contains no executable authority or plaintext.</summary>
public sealed record MemoryEvaluationCheckpoint(
    Guid EvaluationId,
    Guid OperationId,
    CovenantDigest RequestDigest,
    CovenantDigest PlanDigest,
    MemoryEvaluationPhase Phase,
    int PairCount,
    int PhysicalSends,
    MemoryEvaluationAggregate Aggregate,
    long? InputTokens,
    long? OutputTokens,
    decimal? Cost,
    long ElapsedMilliseconds);
