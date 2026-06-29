namespace RetroDownfall.Arcanum.Core.Primitives;

public sealed record ListPageResult<T>(
    T[] Items,
    bool HasMore,
    int? NextOffset = null,
    DateTimeOffset? NextBeforeUpdatedAt = null)
{

    private readonly T[] _items = Items ?? [];

    /// <summary>Page items; never null (a null array — e.g. from deserialization — normalizes to empty).</summary>
    public T[] Items
    {

        get => _items;

        init => _items = value ?? [];

    }

}
