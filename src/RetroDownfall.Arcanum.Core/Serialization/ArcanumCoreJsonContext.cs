using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Conclave.PlanStep))]
[JsonSerializable(typeof(List<Conclave.PlanStep>))]
[JsonSerializable(typeof(Conclave.ApprenticeCheckpoint))]
[JsonSerializable(typeof(Tower.CampaignSettings))]
[JsonSerializable(typeof(Tower.SkillMetadata))]
[JsonSerializable(typeof(Tower.CampaignExportDto))]
[JsonSerializable(typeof(Tower.CampaignExportSpellDto))]
[JsonSerializable(typeof(Tower.CampaignExportScriptDto))]
[JsonSerializable(typeof(Tower.PromptExportDto))]
[JsonSerializable(typeof(Sanctum.SanctumConfig))]
[JsonSerializable(typeof(Sanctum.SanctumMode))]
[JsonSerializable(typeof(Sanctum.NetworkPolicy))]
[JsonSerializable(typeof(Sanctum.ResourceLimits))]
[JsonSerializable(typeof(JsonDocument))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Tower.SessionExportPayload))]
[JsonSerializable(typeof(Storage.Entities.Session))]
[JsonSerializable(typeof(Storage.Entities.Entry))]
[JsonSerializable(typeof(List<Storage.Entities.Entry>))]
[JsonSerializable(typeof(Storage.SanctumBreachDetails))]
[JsonSerializable(typeof(Weave.SagaExtractionResponse))]

public partial class ArcanumCoreJsonContext : JsonSerializerContext;
