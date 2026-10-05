using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

/// <summary>
/// The CODEX.md read cache is bounded, validated by a file stamp and evicts the least recently used entry.
/// These tests drive a small private cache with synthetic stamps, so the eviction order and the validation
/// are pinned exactly and cannot be disturbed by other tests that share the process-wide cache.
/// </summary>
public sealed class CodexReadCacheTests
{
    private static readonly CodexFileStamp Stamp = new(new FileHandleIdentity(1, 10), Length: 5, MtimeUtcTicks: 100);

    private static CodexCacheKey Key(string name, long maxSizeBytes = 4096) => new(name, maxSizeBytes);

    [Fact]
    public void A_cache_must_hold_at_least_one_entry()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodexReadCache(0));
    }

    [Fact]
    public void The_least_recently_used_entry_is_the_one_evicted()
    {
        CodexReadCache cache = new(capacity: 3);

        cache.Store(Key("a"), Stamp, "body a");

        cache.Store(Key("b"), Stamp, "body b");

        cache.Store(Key("c"), Stamp, "body c");

        // Reading "a" makes it the most recently used, so "b" is now the oldest.
        Assert.Equal("body a", cache.TryGet(Key("a"), Stamp));

        cache.Store(Key("d"), Stamp, "body d");

        Assert.Equal(3, cache.Count);

        Assert.True(cache.Contains(Key("a")));

        Assert.False(cache.Contains(Key("b")));

        Assert.True(cache.Contains(Key("c")));

        Assert.True(cache.Contains(Key("d")));

        // The next eviction takes "c" (untouched since it was stored), not the entry just added.
        cache.Store(Key("e"), Stamp, "body e");

        Assert.False(cache.Contains(Key("c")));

        Assert.True(cache.Contains(Key("a")));

        Assert.True(cache.Contains(Key("d")));

        Assert.True(cache.Contains(Key("e")));
    }

    [Fact]
    public void Storing_an_existing_key_replaces_it_and_counts_as_a_use_without_growing_the_cache()
    {
        CodexReadCache cache = new(capacity: 2);

        cache.Store(Key("a"), Stamp, "first");

        cache.Store(Key("b"), Stamp, "body b");

        cache.Store(Key("a"), Stamp, "second");

        Assert.Equal(2, cache.Count);

        // "a" was re-stored last, so adding "c" evicts "b".
        cache.Store(Key("c"), Stamp, "body c");

        Assert.Equal("second", cache.TryGet(Key("a"), Stamp));

        Assert.False(cache.Contains(Key("b")));
    }

    [Fact]
    public void Contains_does_not_count_as_a_use()
    {
        CodexReadCache cache = new(capacity: 2);

        cache.Store(Key("a"), Stamp, "body a");

        cache.Store(Key("b"), Stamp, "body b");

        Assert.True(cache.Contains(Key("a")));

        cache.Store(Key("c"), Stamp, "body c");

        Assert.False(cache.Contains(Key("a")));
    }

    [Fact]
    public void A_different_size_limit_is_a_different_entry()
    {
        CodexReadCache cache = new(capacity: 4);

        cache.Store(Key("a", maxSizeBytes: 4096), Stamp, "body a");

        Assert.Null(cache.TryGet(Key("a", maxSizeBytes: 1024), Stamp));

        Assert.Equal("body a", cache.TryGet(Key("a", maxSizeBytes: 4096), Stamp));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("volume")]
    [InlineData("length")]
    [InlineData("mtime")]
    public void An_entry_whose_stamp_moved_in_any_one_part_is_a_miss_and_is_dropped(string changed)
    {
        CodexReadCache cache = new(capacity: 4);

        cache.Store(Key("a"), Stamp, "body a");

        CodexFileStamp moved = changed switch
        {
            // Same length and mtime on a new inode: a replacement that restored the old mtime.
            "identity" => Stamp with { Identity = new FileHandleIdentity(1, 11) },
            "volume" => Stamp with { Identity = new FileHandleIdentity(2, 10) },
            "length" => Stamp with { Length = 6 },
            _ => Stamp with { MtimeUtcTicks = 101 },
        };

        Assert.Null(cache.TryGet(Key("a"), moved));

        Assert.False(cache.Contains(Key("a")));

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void An_unchanged_stamp_is_a_hit()
    {
        CodexReadCache cache = new(capacity: 4);

        cache.Store(Key("a"), Stamp, "body a");

        Assert.Equal("body a", cache.TryGet(Key("a"), Stamp with { }));
    }
}
