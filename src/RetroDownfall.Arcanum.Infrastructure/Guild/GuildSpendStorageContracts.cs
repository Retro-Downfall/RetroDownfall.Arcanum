using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildInitialBillSnapshot(Guid EffectId, Guid QuestId, Guid DispatchId, Guid SourceId,
    Guid RunId, BillableOperationType OperationType, string Provider, string Model, string Purpose,
    DateTimeOffset StartedAt, DateTimeOffset CompletedAt, long InputTokens, long OutputTokens,
    long ReasoningTokens, long CachedTokens, string PricingSnapshotJson, string ActualCostUsd,
    BillableOperationStatus Status, string? ProviderRequestId);

internal sealed record GuildStoredInitialSpend(GuildInitialBillSnapshot Snapshot, GuildBudgetEffect Outcome, long EventSequence);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuildInitialBillSnapshot))]
[JsonSerializable(typeof(GuildBudgetEffect))]
internal sealed partial class GuildSpendStorageJsonContext : JsonSerializerContext;
