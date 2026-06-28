using System.Text.Json;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

namespace RetroDownfall.Arcanum.Tests.LlamaCpp;

public sealed class TheReliquaryManifestAtomicWriteTests
{

    // W2.5 Fix 3: the cache manifest must be written atomically (temp + flush +
    // File.Move(overwrite)) so a crash between the model File.Move and the
    // manifest write cannot leave a model with a partial/missing manifest.
    // TheReliquary.WriteManifestAtomicAsync reuses SpellAtomicFile (W2.1).
    //
    // Simulating a mid-write crash is impractical here; this test proves the
    // atomic-write shape directly: after a successful write the manifest
    // exists, parses back to the same content, and no temp residue remains in
    // the entry directory. A mid-write-crash integration test is a follow-up.

    [Fact]

    public async Task WriteManifestAtomicAsync_WritesManifestAndLeavesNoTempResidue()
    {

        string entryDir = Path.Combine(Path.GetTempPath(), $"reliquary-manifest-{Guid.NewGuid():N}");

        string manifestPath = Path.Combine(entryDir, "manifest.json");

        try
        {

            DateTimeOffset now = DateTimeOffset.UtcNow;

            GgufModelManifest manifest = new()
            {

                SourceUrl = "https://example.com/models/test.gguf",

                Etag = "\"etag-abc\"",

                Sha256 = "deadbeef",

                DownloadedAt = now,

                LastAccessedAt = now,

                Size = 4096,

                Verified = true,

            };

            await TheReliquary.WriteManifestAtomicAsync(manifestPath, manifest, CancellationToken.None);

            Assert.True(File.Exists(manifestPath));

            Assert.Empty(Directory.GetFiles(entryDir, "*.tmp"));

            string json = await File.ReadAllTextAsync(manifestPath);

            GgufModelManifest? parsed = JsonSerializer.Deserialize(json, LlamaCppJsonContext.Default.GgufModelManifest);

            Assert.NotNull(parsed);

            Assert.Equal(manifest.SourceUrl, parsed!.SourceUrl);

            Assert.Equal(manifest.Sha256, parsed.Sha256);

            Assert.Equal(manifest.Etag, parsed.Etag);

            Assert.Equal(manifest.Size, parsed.Size);

            Assert.Equal(manifest.Verified, parsed.Verified);

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
