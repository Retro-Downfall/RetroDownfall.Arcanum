using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The keyed correlation label: stable within a key version, rotating with it, never a raw hash.
/// </summary>
public sealed class CovenantDiagnosticTaggerTests
{

    private static readonly byte[] KeyOne = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    private static readonly byte[] KeyTwo = Convert.FromHexString(
        "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F");

    [Fact]
    public void Tag_matches_the_versioned_128_bit_hmac_vector()
    {

        CovenantDigest digest = new(SHA256.HashData(Encoding.UTF8.GetBytes("covenant-entry")));

        CovenantDiagnosticTagger tagger = new(new StubKeySource(KeyOne, keyVersion: 3));

        CovenantDiagnosticTag tag = tagger.Create(digest);

        byte[] expected = HMACSHA256.HashData(KeyOne, digest.Bytes)[..CovenantDiagnosticTag.TagBytes];

        Span<byte> actual = stackalloc byte[CovenantDiagnosticTag.TagBytes];

        tag.WriteTo(actual);

        Assert.Equal(expected, actual.ToArray());
        Assert.Equal(3u, tag.KeyVersion);
        Assert.StartsWith("v3.", tag.ToString(), StringComparison.Ordinal);

    }

    [Fact]
    public void Tag_is_stable_across_taggers_holding_the_same_key_and_version()
    {

        CovenantDigest digest = new(SHA256.HashData(Encoding.UTF8.GetBytes("covenant-entry")));

        CovenantDiagnosticTag first = new CovenantDiagnosticTagger(new StubKeySource(KeyOne, 3)).Create(digest);

        CovenantDiagnosticTag second = new CovenantDiagnosticTagger(new StubKeySource(KeyOne, 3)).Create(digest);

        Assert.True(CovenantDiagnosticTag.FixedTimeEquals(first, second));
        Assert.Equal(first.ToString(), second.ToString());

    }

    [Fact]
    public void Tag_rotates_with_the_key_version_and_never_returns_the_raw_digest()
    {

        CovenantDigest digest = new(SHA256.HashData(Encoding.UTF8.GetBytes("covenant-entry")));

        CovenantDiagnosticTag first = new CovenantDiagnosticTagger(new StubKeySource(KeyOne, 3)).Create(digest);

        CovenantDiagnosticTag rotated = new CovenantDiagnosticTagger(new StubKeySource(KeyTwo, 4)).Create(digest);

        Assert.False(CovenantDiagnosticTag.FixedTimeEquals(first, rotated));
        Assert.Equal(4u, rotated.KeyVersion);

        Span<byte> tagBytes = stackalloc byte[CovenantDiagnosticTag.TagBytes];

        first.WriteTo(tagBytes);

        Assert.False(digest.Bytes.AsSpan(0, CovenantDiagnosticTag.TagBytes).SequenceEqual(tagBytes));
        Assert.DoesNotContain(Convert.ToHexString(tagBytes), digest.ToString(), StringComparison.Ordinal);

    }

    [Fact]
    public void Two_different_digests_under_one_key_produce_different_tags()
    {

        CovenantDiagnosticTagger tagger = new(new StubKeySource(KeyOne, 3));

        CovenantDiagnosticTag first = tagger.Create(new CovenantDigest(SHA256.HashData("a"u8.ToArray())));

        CovenantDiagnosticTag second = tagger.Create(new CovenantDigest(SHA256.HashData("b"u8.ToArray())));

        Assert.False(CovenantDiagnosticTag.FixedTimeEquals(first, second));

    }

    [Fact]
    public void An_unvalidated_digest_or_absent_key_produces_no_tag()
    {

        CovenantDiagnosticTagger tagger = new(new StubKeySource(KeyOne, 3));

        _ = Assert.Throws<ArgumentException>(() => tagger.Create(default));

        CovenantDiagnosticTagger unkeyed = new(new StubKeySource(key: null, keyVersion: 0));

        Assert.Equal(0u, unkeyed.KeyVersion);

        _ = Assert.Throws<InvalidOperationException>(
            () => unkeyed.Create(new CovenantDigest(SHA256.HashData("a"u8.ToArray()))));

    }

    [Fact]
    public void A_default_tag_is_invalid_and_never_compares_equal()
    {

        CovenantDiagnosticTag tag = default;

        Assert.False(tag.IsValid);
        Assert.False(CovenantDiagnosticTag.FixedTimeEquals(tag, tag));

        _ = Assert.Throws<InvalidOperationException>(() => tag.KeyVersion);

    }

    private sealed class StubKeySource(byte[]? key, uint keyVersion) : ICovenantDiagnosticKeySource
    {

        public bool TryCopyDiagnosticKey(Span<byte> destination, out uint version)
        {

            version = 0;

            if (key is null || destination.Length < key.Length)
            {
                return false;
            }

            key.CopyTo(destination);

            version = keyVersion;

            return true;

        }

    }

}
