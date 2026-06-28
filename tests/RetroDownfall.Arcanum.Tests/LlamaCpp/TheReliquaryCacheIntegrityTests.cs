using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

namespace RetroDownfall.Arcanum.Tests.LlamaCpp;

public sealed class TheReliquaryCacheIntegrityTests
{

    // W2.5 Fix 2: a manifest-less / legacy cache entry accepted with NO hash
    // comparison when expectedSha256 is null is a security gap. When
    // RequireModelHash is true, a cache hit with NO verifiable hash (neither the
    // request/pinned sha256 nor a manifest Sha256) must be rejected with
    // Llama.UnverifiedCacheEntry. When RequireModelHash is false the
    // accept-on-no-hash behavior is intentional (operator opted out) and must be
    // preserved. This mirrors GgufModelHashPolicy for the download path.

    [Fact]

    public async Task VerifyCachedModelIntegrityAsync_WhenRequireModelHashTrueAndNoVerifiableHash_Rejects()
    {

        string entryDir = Path.Combine(Path.GetTempPath(), $"reliquary-verify-{Guid.NewGuid():N}");

        try
        {

            Directory.CreateDirectory(entryDir);

            string modelPath = Path.Combine(entryDir, "model.gguf");

            await File.WriteAllBytesAsync(modelPath, new byte[] { 1, 2, 3, 4, 5 });

            // No manifest, no expectedSha256 → nothing to verify against.

            Result result = await TheReliquary.VerifyCachedModelIntegrityAsync(
                entryDir,
                modelPath,
                expectedSha256: null,
                requireModelHash: true,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Llama.UnverifiedCacheEntry", result.Error.Code);

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

    public async Task VerifyCachedModelIntegrityAsync_WhenRequireModelHashFalseAndNoVerifiableHash_Accepts()
    {

        string entryDir = Path.Combine(Path.GetTempPath(), $"reliquary-verify-{Guid.NewGuid():N}");

        try
        {

            Directory.CreateDirectory(entryDir);

            string modelPath = Path.Combine(entryDir, "model.gguf");

            await File.WriteAllBytesAsync(modelPath, new byte[] { 1, 2, 3, 4, 5 });

            Result result = await TheReliquary.VerifyCachedModelIntegrityAsync(
                entryDir,
                modelPath,
                expectedSha256: null,
                requireModelHash: false,
                CancellationToken.None);

            Assert.True(result.IsSuccess);

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

    public async Task VerifyCachedModelIntegrityAsync_WhenManifestHashMatches_AcceptsEvenWhenRequireModelHashTrue()
    {

        // A manifest with a matching Sha256 satisfies RequireModelHash even when
        // the request supplied no expectedSha256 (the manifest hash verifies).

        string entryDir = Path.Combine(Path.GetTempPath(), $"reliquary-verify-{Guid.NewGuid():N}");

        try
        {

            Directory.CreateDirectory(entryDir);

            byte[] modelBytes = { 1, 2, 3, 4, 5 };

            string modelPath = Path.Combine(entryDir, "model.gguf");

            await File.WriteAllBytesAsync(modelPath, modelBytes);

            string hash = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant();

            GgufModelManifest manifest = new()
            {

                SourceUrl = "https://example.com/m.gguf",

                Sha256 = hash,

                DownloadedAt = DateTimeOffset.UtcNow,

                LastAccessedAt = DateTimeOffset.UtcNow,

                Size = modelBytes.Length,

                Verified = true,

            };

            await TheReliquary.WriteManifestAtomicAsync(
                Path.Combine(entryDir, "manifest.json"),
                manifest,
                CancellationToken.None);

            Result result = await TheReliquary.VerifyCachedModelIntegrityAsync(
                entryDir,
                modelPath,
                expectedSha256: null,
                requireModelHash: true,
                CancellationToken.None);

            Assert.True(result.IsSuccess);

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

    public async Task VerifyCachedModelIntegrityAsync_WhenManifestHashMismatches_RejectsWithSha256Mismatch()
    {

        // Existing behavior must be preserved: a manifest hash that does not match
        // the computed hash is rejected with Llama.Sha256Mismatch regardless of
        // RequireModelHash.

        string entryDir = Path.Combine(Path.GetTempPath(), $"reliquary-verify-{Guid.NewGuid():N}");

        try
        {

            Directory.CreateDirectory(entryDir);

            string modelPath = Path.Combine(entryDir, "model.gguf");

            await File.WriteAllBytesAsync(modelPath, new byte[] { 1, 2, 3, 4, 5 });

            GgufModelManifest manifest = new()
            {

                SourceUrl = "https://example.com/m.gguf",

                Sha256 = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef0000",

                DownloadedAt = DateTimeOffset.UtcNow,

                LastAccessedAt = DateTimeOffset.UtcNow,

                Size = 5,

                Verified = true,

            };

            await TheReliquary.WriteManifestAtomicAsync(
                Path.Combine(entryDir, "manifest.json"),
                manifest,
                CancellationToken.None);

            Result result = await TheReliquary.VerifyCachedModelIntegrityAsync(
                entryDir,
                modelPath,
                expectedSha256: null,
                requireModelHash: false,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Llama.Sha256Mismatch", result.Error.Code);

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
