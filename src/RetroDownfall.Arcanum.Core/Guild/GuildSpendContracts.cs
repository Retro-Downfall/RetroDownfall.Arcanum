using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

public sealed record GuildSpendAmountsRequest(long? InputTokens, long? OutputTokens, long? CachedTokens, long? ReasoningTokens,
    [property: JsonConverter(typeof(ExactNullableDecimalJsonConverter))] decimal? Usd);

public sealed record GuildSpendFacts(long InputTokens, long OutputTokens, long CachedTokens, long ReasoningTokens,
    decimal Usd, bool UnknownUsd, bool UnknownTokens, bool Complete);

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildSpendOrigin>))]
public enum GuildSpendOrigin
{
    [JsonStringEnumMemberName("localResponse")]
    LocalResponse = 0,

    [JsonStringEnumMemberName("remoteReport")]
    RemoteReport = 1,

    [JsonStringEnumMemberName("operatorAttested")]
    OperatorAttested = 2,

    [JsonStringEnumMemberName("providerReported")]
    ProviderReported = 3,
}

public sealed record GuildSpendCorrectionRequest(Guid IdempotencyKey, long ExpectedRevision, GuildSpendOrigin? Origin,
    GuildSpendAmountsRequest? Amounts, string? EvidenceDigest);

public sealed record GuildSpendLocalTokensDto(long InputTokens, long OutputTokens, long CachedTokens, long ReasoningTokens);

public sealed record GuildSpendEvidenceFactsDto(decimal KnownUsd, long KnownTokens, bool UnknownUsd, bool UnknownTokens,
    bool Complete, GuildSpendLocalTokensDto? LocalTokens);

public sealed record GuildSpendReceiptDto(Guid ReceiptId, Guid QuestId, Guid EffectId, GuildSpendOrigin Origin,
    string? EvidenceDigest, GuildSpendEvidenceFactsDto Facts, long EvidenceRevision, long EventSequence, DateTimeOffset AcceptedAt);

public sealed record GuildSpendCorrectionResultDto(GuildSpendReceiptDto Receipt, bool Replayed);

public sealed record GuildSpendRefreshRequest(Guid IdempotencyKey);

public sealed record GuildSpendRefreshResultDto(Guid OperationId, GuildSpendReceiptDto? Receipt, bool Replayed, bool InFlight,
    bool RequiresAttention, string? ErrorCode);

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildSpendEffectKind>))]
public enum GuildSpendEffectKind
{
    [JsonStringEnumMemberName("localProvider")]
    LocalProvider = 0,

    [JsonStringEnumMemberName("remote")]
    Remote = 1,
}

public sealed record GuildSpendSourceDto(Guid EffectId, Guid ReceiptId, long EvidenceRevision, GuildSpendOrigin Origin);

public sealed record GuildSpendEffectDto(Guid QuestId, Guid EffectId, GuildSpendEffectKind Kind, GuildSpendEvidenceFactsDto Facts,
    long EvidenceRevision, Guid? LastReceiptId, string? OperatorIneligibility, string? ProviderIneligibility);

public sealed record GuildSpendEffectPageDto(ImmutableArray<GuildSpendEffectDto> Items, string? NextCursor, long SnapshotSequence, string StateConsistency = "livePerPage");

public sealed record GuildSpendReceiptPageDto(ImmutableArray<GuildSpendReceiptDto> Items, string? NextCursor, long SnapshotSequence, string StateConsistency = "livePerPage");
