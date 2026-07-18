using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.TheForge.Core.Models.Trials;

namespace RetroDownfall.TheForge.Core.Serialization;

/// <summary>Source-generated JSON for The Forge-local Trial suite store.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TrialSuiteStoreDocument))]
[JsonSerializable(typeof(TrialSuiteRecord))]
[JsonSerializable(typeof(TrialSuiteItemRecord))]
[JsonSerializable(typeof(TrialSuiteRunRecord))]
[JsonSerializable(typeof(TrialSuiteRunResultRecord))]
[JsonSerializable(typeof(IReadOnlyList<TrialSuiteRecord>))]
[JsonSerializable(typeof(IReadOnlyList<TrialSuiteItemRecord>))]
[JsonSerializable(typeof(IReadOnlyList<TrialSuiteRunRecord>))]
[JsonSerializable(typeof(IReadOnlyList<TrialSuiteRunResultRecord>))]
[JsonSerializable(typeof(Trial))]
[JsonSerializable(typeof(Inquisitor))]
[JsonSerializable(typeof(RegexInquisitor))]
[JsonSerializable(typeof(JsonSchemaInquisitor))]
[JsonSerializable(typeof(SemanticInquisitor))]
[JsonSerializable(typeof(IReadOnlyList<Inquisitor>))]
[JsonSerializable(typeof(InquisitorVerdict))]
[JsonSerializable(typeof(IReadOnlyList<InquisitorVerdict>))]
[JsonSerializable(typeof(TrialTargetKind))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
public partial class TheForgeTrialSuitesJsonContext : JsonSerializerContext;
