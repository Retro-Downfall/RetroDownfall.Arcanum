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
