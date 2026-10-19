using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

// These transient contracts are deliberately absent from every JSON context inventory.
public sealed class MemoryEvaluationValidatedRequest
{
    internal MemoryEvaluationValidatedRequest(MemoryEvaluationRequest request)
    {
        Request = request;
    }

    public MemoryEvaluationRequest Request { get; }
}

public sealed record MemoryEvaluationSourceSnapshot(
    ImmutableArray<MemoryEvaluationSourceCoordinate> Coordinates,
    string RenderedMemory,
    CovenantDigest ManifestDigest,
    ImmutableArray<MemoryEvaluationDependencyEdge> Dependencies = default,
    MemoryEvaluationSourceFence? Fence = null,
    GenerationProvenance? Provenance = null,
    ImmutableArray<MemoryEvaluationSourceFragment> Fragments = default,
    DateTimeOffset? AsOfUtc = null)
{
    private readonly ImmutableArray<MemoryEvaluationSourceCoordinate> _coordinates = Copy(Coordinates);

    private readonly ImmutableArray<MemoryEvaluationDependencyEdge> _dependencies = Copy(Dependencies);

    private readonly ImmutableArray<MemoryEvaluationSourceFragment> _fragments = Copy(Fragments);

    public ImmutableArray<MemoryEvaluationSourceCoordinate> Coordinates
    {
        get => _coordinates;

        init => _coordinates = Copy(value);
    }

    public ImmutableArray<MemoryEvaluationDependencyEdge> Dependencies
    {
        get => _dependencies;

        init => _dependencies = Copy(value);
    }

    public ImmutableArray<MemoryEvaluationSourceFragment> Fragments
    {
        get => _fragments;

        init => _fragments = Copy(value);
    }

    private static ImmutableArray<T> Copy<T>(ImmutableArray<T> values) => values.IsDefault ? [] : values.ToArray().ToImmutableArray();
}

public enum MemoryEvaluationPromptPart
{
    System = 1,

    User = 2
}

public sealed record MemoryEvaluationSourceFragment(int SourceIndex, string Text, MemoryEvaluationPromptPart Placement);

public sealed record MemoryEvaluationSourceFence(Guid DatasetGeneration, long AdmissionGeneration, long RuntimeAuthorityGeneration, long CampaignAvailabilityGeneration, CovenantDigest SourceStateDigest);

public sealed record MemoryEvaluationPromptOccurrence(int SourceIndex, MemoryEvaluationPromptPart Placement, int StartUtf8, int LengthUtf8, CovenantDigest ContentDigest);

public sealed record MemoryEvaluationPromptLayout(string SystemPrompt, string UserPrompt, ImmutableArray<MemoryEvaluationPromptOccurrence> Occurrences)
{
    private readonly ImmutableArray<MemoryEvaluationPromptOccurrence> _occurrences = MemoryEvaluationFrozenCollections.Copy(Occurrences);

    public ImmutableArray<MemoryEvaluationPromptOccurrence> Occurrences
    {
        get => _occurrences;

        init => _occurrences = MemoryEvaluationFrozenCollections.Copy(value);
    }
}

public sealed record MemoryEvaluationPromptPair(
    string ControlSystemPrompt,
    string ControlUserPrompt,
    string TreatmentSystemPrompt,
    string TreatmentUserPrompt,
    ImmutableArray<MemoryEvaluationPromptOccurrence> Occurrences,
    CovenantDigest UnchangedSpansDigest)
{
    private readonly ImmutableArray<MemoryEvaluationPromptOccurrence> _occurrences = MemoryEvaluationFrozenCollections.Copy(Occurrences);

    public ImmutableArray<MemoryEvaluationPromptOccurrence> Occurrences
    {
        get => _occurrences;

        init => _occurrences = MemoryEvaluationFrozenCollections.Copy(value);
    }
}

public sealed record MemoryEvaluationExecutionPlan(
    MemoryEvaluationValidatedRequest Request,
    MemoryEvaluationSourceSnapshot Sources,
    MemoryEvaluationPromptPair Prompts,
    CovenantDigest ProviderDigest,
    CovenantDigest ModelDigest,
    CovenantDigest OptionsDigest,
    CovenantDigest DestinationDigest,
    CovenantDigest SensitivityDigest,
    long ConfigurationGeneration,
    ImmutableArray<MemoryEvaluationArm> SendOrder,
    int PolicyVersion = 1)
{
    private readonly ImmutableArray<MemoryEvaluationArm> _sendOrder = MemoryEvaluationFrozenCollections.Copy(SendOrder);

    public ImmutableArray<MemoryEvaluationArm> SendOrder
    {
        get => _sendOrder;

        init => _sendOrder = MemoryEvaluationFrozenCollections.Copy(value);
    }
}

public sealed record MemoryEvaluationSendEvidence(
    int SendIndex,
    int PairIndex,
    MemoryEvaluationArm Arm,
    ProviderCallEnvelope Envelope,
    CovenantDigest PayloadDigest,
    CovenantDigest DestinationDigest,
    CovenantDigest SensitivityDigest);

public sealed record MemoryEvaluationUsage(long? InputTokens, long? OutputTokens, decimal? Cost, long? CachedTokens = null, long? ReasoningTokens = null);

public sealed record MemoryEvaluationArmResult(
    bool IsValid,
    MemoryEvaluationReason Reason,
    ImmutableArray<MemoryEvaluationVerdict> Verdicts,
    CovenantDigest ResponseDigest,
    MemoryEvaluationUsage Usage,
    long ElapsedMilliseconds,
    string? Answer = null,
    bool ModelIdentityVerified = false,
    bool ProviderCompleted = false,
    bool UsageKnown = false)
{
    private readonly ImmutableArray<MemoryEvaluationVerdict> _verdicts = MemoryEvaluationFrozenCollections.Copy(Verdicts);

    public ImmutableArray<MemoryEvaluationVerdict> Verdicts
    {
        get => _verdicts;

        init => _verdicts = MemoryEvaluationFrozenCollections.Copy(value);
    }
}

internal static class MemoryEvaluationFrozenCollections
{
    public static ImmutableArray<T> Copy<T>(ImmutableArray<T> values) => values.IsDefault ? [] : values.ToArray().ToImmutableArray();
}
