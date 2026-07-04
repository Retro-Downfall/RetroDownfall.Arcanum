namespace RetroDownfall.Arcanum.Core.Storage;

public sealed record UnseenServantWatermark(
    string JobKey,
    DateTimeOffset LastRunAt,
    int EffectiveIntervalMinutes);
