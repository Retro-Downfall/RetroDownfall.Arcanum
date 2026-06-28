using System.Text.Json;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

namespace RetroDownfall.Arcanum.Tests.LlamaCpp;

public sealed class TheReliquaryManifestTouchTests
{

    // W2.5 Fix 4: TouchLastAccessedAsync did read-modify-write via
    // File.WriteAllBytesAsync (non-atomic): concurrent touches can clobber each
    // other and a crash mid-write corrupts the manifest. The touch path now
    // reuses WriteManifestAtomicAsync (W2.5 Fix 3: same-directory temp + flush +
    // File.Move(overwrite)) so the manifest is replaced atomically. This test
    // proves the atomic-write shape directly (mirrors W2.5 Fix 3's
    // TheReliquaryManifestAtomicWriteTests): after the touch, the manifest
    // round-trips with the updated LastAccessedAt and no .tmp residue remains.
    // A mid-write-crash integration test is a follow-up (same caveat as W2.5 Fix 3).

    [Fact]

    public async Task TouchManifestLastAccessedAsync_WritesAtomically_NoTmpResidue_RoundTrips()
    {

        string entryDir = Path.Combine(Path.GetTempPath(), $"reliquary-touch-{Guid.NewGuid():N}");

        string manifestPath = Path.Combine(entryDir, "manifest.json");

        try
        {

            DateTimeOffset originalAccessed = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1));

            GgufModelManifest initial = new()
            {

                SourceUrl = "https://example.com/models/test.gguf",

                Etag = "\"etag-abc\"",

                Sha256 = "deadbeef",

                DownloadedAt = originalAccessed,

                LastAccessedAt = originalAccessed,

                Size = 4096,

                Verified = true,

            };

            await TheReliquary.WriteManifestAtomicAsync(manifestPath, initial, CancellationToken.None);

            DateTimeOffset beforeTouch = DateTimeOffset.UtcNow;

            await TheReliquary.TouchManifestLastAccessedAsync(manifestPath, CancellationToken.None);

            // Atomic write leaves no temp residue in the entry directory.

            Assert.Empty(Directory.GetFiles(entryDir, "*.tmp"));

            // Content round-trips; LastAccessedAt advanced, other fields preserved.

            string json = await File.ReadAllTextAsync(manifestPath);

            GgufModelManifest? parsed = JsonSerializer.Deserialize(json, LlamaCppJsonContext.Default.GgufModelManifest);

            Assert.NotNull(parsed);

            Assert.True(parsed!.LastAccessedAt >= beforeTouch);

            Assert.True(parsed.LastAccessedAt > originalAccessed);

            Assert.Equal(initial.SourceUrl, parsed.SourceUrl);

            Assert.Equal(initial.Sha256, parsed.Sha256);

            Assert.Equal(initial.Etag, parsed.Etag);

            Assert.Equal(initial.Size, parsed.Size);

            Assert.Equal(initial.Verified, parsed.Verified);

            Assert.Equal(initial.DownloadedAt, parsed.DownloadedAt);

        }

        finally
        {

            if (Directory.Exists(entryDir))
            {

                Directory.Delete(entryDir, recursive: true);

            }

        }

    }

    [Fact]

    public async Task TouchManifestLastAccessedAsync_WhenManifestMissing_IsNoop()
    {

        // Missing manifest path must not throw (the touch is best-effort).

        string entryDir = Path.Combine(Path.GetTempPath(), $"reliquary-touch-{Guid.NewGuid():N}");

        string manifestPath = Path.Combine(entryDir, "manifest.json");

        try
        {

            Directory.CreateDirectory(entryDir);

            await TheReliquary.TouchManifestLastAccessedAsync(manifestPath, CancellationToken.None);

            Assert.False(File.Exists(manifestPath));

        }

        finally
        {

            if (Directory.Exists(entryDir))
            {

                Directory.Delete(entryDir, recursive: true);

            }

        }

    }

}
