using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LongRestStoredAdjudication))]
[JsonSerializable(typeof(LongRestSourceLeaf))]
[JsonSerializable(typeof(LongRestConfigurationStamp))]
[JsonSerializable(typeof(LongRestStoredSummary))]
[JsonSerializable(typeof(LongRestStoredPaid))]
[JsonSerializable(typeof(LongRestStoredWork))]
[JsonSerializable(typeof(LongRestDispatchCompletion))]
[JsonSerializable(typeof(LongRestSnapshot))]
[JsonSerializable(typeof(LongRestStoredFence))]
[JsonSerializable(typeof(LongRestRecipeIdentity))]
[JsonSerializable(typeof(RetroDownfall.Arcanum.Core.Covenant.ArtifactSensitivityLabel))]
internal sealed partial class LongRestJsonContext : JsonSerializerContext;
