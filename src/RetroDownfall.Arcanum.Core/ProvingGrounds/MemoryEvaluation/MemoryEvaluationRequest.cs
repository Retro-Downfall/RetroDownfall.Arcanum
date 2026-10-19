using System.Collections.Immutable;

using System.Text.Json;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Serialization;

using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryEvaluationScopeKind>))]
public enum MemoryEvaluationScopeKind
{
    Global = 1,

    Campaign = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryEvaluationCheckKind>))]
public enum MemoryEvaluationCheckKind
{
    Regex = 1,

    JsonSchema = 2
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemoryEvaluationScope(MemoryEvaluationScopeKind Kind, Guid? CampaignId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemoryEvaluationRequest(
    Guid EvaluationId,
    MemoryEvaluationScope Scope,
    string ProviderName,
    string Model,
    MemoryEvaluationOptions Options,
    MemoryEvaluationWorkload Workload,
    MemoryEvaluationMemory Memory,
    ImmutableArray<MemoryEvaluationCheck> Checks,
    int PairCount = 5);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemoryEvaluationOptions(
    double? Temperature = null,
    double? TopP = null,
    long? Seed = null,
    int MaxOutputTokens = 512,
    ReasoningEffortLevel? ReasoningEffort = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemoryEvaluationWorkload(string SystemPrompt, string UserPrompt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemoryEvaluationMemory(
    ImmutableArray<MemoryEvaluationCovenantTarget> Covenant,
    ImmutableArray<MemoryEvaluationSagaTarget> Saga,
    ImmutableArray<MemoryEvaluationLexiconTarget> Lexicon);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemoryEvaluationCovenantTarget(Guid EntryId, Guid VersionId, CovenantDigest FragmentDigest);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemoryEvaluationSagaTarget(string MemoryId, string VersionId, AnnalContentHashFormat HashFormat, CovenantDigest ContentDigest);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemoryEvaluationLexiconTarget(Guid EntryId, string VersionId, AnnalContentHashFormat HashFormat, CovenantDigest ContentDigest);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MemoryEvaluationRegexCheck), "regex")]
[JsonDerivedType(typeof(MemoryEvaluationJsonSchemaCheck), "jsonSchema")]
public abstract record MemoryEvaluationCheck;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemoryEvaluationRegexCheck(string Pattern, bool ShouldMatch = true, bool IgnoreCase = false) : MemoryEvaluationCheck;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemoryEvaluationJsonSchemaCheck(JsonElement Schema) : MemoryEvaluationCheck
{
    public JsonElement Schema { get; init; } = Schema.ValueKind == JsonValueKind.Undefined ? default : Schema.Clone();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemoryEvaluationGetRequest(Guid EvaluationId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemoryEvaluationDeleteRequest(Guid EvaluationId, CovenantDigest ExpectedReportDigest, bool Confirm);

public sealed record MemoryEvaluationDeleteResult(Guid EvaluationId, bool Deleted);

public sealed record MemoryEvaluationResponse(Guid EvaluationId, Guid OperationId, MemoryEvaluationState State, MemoryEvaluationReason Reason, MemoryEvaluationTerminalReport? Report, CovenantDigest? ReportDigest = null);
