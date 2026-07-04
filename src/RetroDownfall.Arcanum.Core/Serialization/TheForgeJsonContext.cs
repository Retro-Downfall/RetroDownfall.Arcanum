using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(TheForge.PlanStep))]
[JsonSerializable(typeof(List<TheForge.PlanStep>))]
[JsonSerializable(typeof(TheForge.ApprenticeCheckpoint))]
[JsonSerializable(typeof(TheForge.CampaignSettings))]
[JsonSerializable(typeof(TheForge.SkillMetadata))]
[JsonSerializable(typeof(TheForge.CampaignExportDto))]
[JsonSerializable(typeof(TheForge.CampaignExportSpellDto))]
[JsonSerializable(typeof(TheForge.CampaignExportScriptDto))]
[JsonSerializable(typeof(TheForge.PromptExportDto))]
[JsonSerializable(typeof(Sanctum.SanctumConfig))]
[JsonSerializable(typeof(Sanctum.SanctumMode))]
[JsonSerializable(typeof(Sanctum.NetworkPolicy))]
[JsonSerializable(typeof(Sanctum.ResourceLimits))]
[JsonSerializable(typeof(JsonDocument))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(TheForge.SessionExportPayload))]
[JsonSerializable(typeof(Storage.Entities.Session))]
[JsonSerializable(typeof(Storage.Entities.Entry))]
[JsonSerializable(typeof(List<Storage.Entities.Entry>))]
[JsonSerializable(typeof(Storage.SanctumBreachDetails))]

public partial class TheForgeJsonContext : JsonSerializerContext;
