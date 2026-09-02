using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CovenantResetOfflineTransitionPayloadV1))]
[JsonSerializable(typeof(HealthyCatalogFactoryErasureOfflineTransitionPayloadV1))]
[JsonSerializable(typeof(GrimoireOfflineTransitionBinding))]
[JsonSerializable(typeof(GrimoireOfflineTransitionLifecycle))]
[JsonSerializable(typeof(GrimoireOfflineTransitionClosingEvidence))]
[JsonSerializable(typeof(GrimoireOfflineTransitionVerificationEvidence))]
[JsonSerializable(typeof(GrimoireOfflineTransitionReconciliationEvidence))]
[JsonSerializable(typeof(GrimoireOfflineTransitionBlocker))]
[JsonSerializable(typeof(CovenantResetBlockerResolutionEvidence))]
[JsonSerializable(typeof(HealthyCatalogFactoryErasureBlockerResolutionEvidence))]
[JsonSerializable(typeof(GrimoireOfflineTransitionBeforeStateEvidence))]
[JsonSerializable(typeof(GrimoireOfflineTransitionReplacementEvidence))]
[JsonSerializable(typeof(GrimoireOfflineTransitionEpochTuple))]
[JsonSerializable(typeof(GrimoireOfflineTransitionKind))]
[JsonSerializable(typeof(GrimoireOfflineTransitionState))]
[JsonSerializable(typeof(GrimoireOfflineTransitionTerminalIntent))]
[JsonSerializable(typeof(GrimoireOfflineTransitionHandlerOutcome))]
[JsonSerializable(typeof(GrimoireOfflineTransitionReconciliationStep))]
[JsonSerializable(typeof(CovenantResetPhase))]
[JsonSerializable(typeof(CovenantDigest))]
internal sealed partial class GrimoireOfflineTransitionLifecycleJsonContext : JsonSerializerContext;
