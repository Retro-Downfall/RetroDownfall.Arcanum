using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(GrimoireOfflineTransitionPayloadV1))]
[JsonSerializable(typeof(GrimoireOfflineTransitionEnvelopeV1))]
[JsonSerializable(typeof(GrimoireOfflineTransitionAnchorV1))]
[JsonSerializable(typeof(GrimoireOfflineTransitionKind))]
[JsonSerializable(typeof(GrimoireOfflineTransitionAnchorState))]
[JsonSerializable(typeof(CovenantDigest))]
internal sealed partial class GrimoireOfflineTransitionJournalJsonContext : JsonSerializerContext;
