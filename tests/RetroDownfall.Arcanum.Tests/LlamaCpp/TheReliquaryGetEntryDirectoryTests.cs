using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

namespace RetroDownfall.Arcanum.Tests.LlamaCpp;

public sealed class TheReliquaryGetEntryDirectoryTests
{

    // W2.5 Fix 3: GetEntryDirectory is the storage boundary and must normalize the
    // raw cache key via LlamaCacheKey.NormalizeModelKey so a caller bypassing
    // LlamaCacheKey.Normalize cannot pass a path-escaping key (e.g. foo/../../../etc)
    // that would resolve outside ModelCacheDirectory. NormalizeModelKey is
    // idempotent for already-normalized keys, so honest callers are unaffected.

    [Fact]

    public void GetEntryDirectory_PathEscapingKey_StaysUnderCacheRoot()
    {

        string rootResolved = Path.GetFullPath(ArcanumPaths.ModelCacheDirectory);

        // Prove the test has teeth: the raw, un-normalized combine escapes the
        // cache root (this is the bug Fix 3 closes at the storage boundary).

        string raw = Path.Combine(ArcanumPaths.ModelCacheDirectory, "foo/../../../etc");

        string rawResolved = Path.GetFullPath(raw);

        Assert.False(
            rawResolved.StartsWith(rootResolved + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"Expected raw combine to escape the cache root, but got '{rawResolved}'.");

        // GetEntryDirectory normalizes, so the resolved path stays under the root.

        string dir = TheReliquary.GetEntryDirectory("foo/../../../etc");

        string resolved = Path.GetFullPath(dir);

        Assert.StartsWith(rootResolved + Path.DirectorySeparatorChar, resolved, StringComparison.Ordinal);

        Assert.NotEqual(rootResolved, resolved);

    }

    [Fact]

    public void GetEntryDirectory_AlreadyNormalizedKey_IsIdempotent()
    {

        // NormalizeModelKey is idempotent, so a key that was already normalized
        // (the common path for honest callers) is unchanged by GetEntryDirectory.

        const string key = "my-model";

        string first = TheReliquary.GetEntryDirectory(key);

        string second = TheReliquary.GetEntryDirectory(LlamaCacheKey.NormalizeModelKey(key));

        Assert.Equal(first, second);

    }

    [Fact]

    public void GetEntryDirectory_EmptyKey_Throws()
    {

        // An empty/all-invalid key must be rejected at the boundary (normalize
        // throws ArgumentException) rather than silently resolving to the cache
        // root itself.

        Assert.Throws<ArgumentException>(() => TheReliquary.GetEntryDirectory(""));

        Assert.Throws<ArgumentException>(() => TheReliquary.GetEntryDirectory("   "));

    }

    [Fact]

    public void GetEntryDirectory_AllInvalidCharacters_Throws()
    {

        // A key made entirely of filesystem-invalid characters sanitizes to empty
        // and is rejected at the boundary.

        Assert.Throws<ArgumentException>(() => TheReliquary.GetEntryDirectory("<>:\"/\\|?*"));

    }

}
