namespace RetroDownfall.Arcanum.Core.Primitives;

public sealed record ListPageResult<T>(
    T[] Items,
    bool HasMore,
    int? NextOffset = null,
    DateTimeOffset? NextBeforeUpdatedAt = null);
