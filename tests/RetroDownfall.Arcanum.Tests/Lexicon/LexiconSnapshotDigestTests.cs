using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

public sealed class LexiconSnapshotDigestTests
{

    [Fact]
    public void Canonical_bytes_follow_the_exact_format_2_grammar()
    {

        LexiconCanonicalValue value = Canonical("general", ["alpha"]);

        byte[] expected =
        [
            .. "Arcanum.Lexicon.Snapshot.v2\0"u8,
            0x02,
            0x00, 0x00, 0x00, 0x07,
            .. "general"u8,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x05,
            .. "alpha"u8,
        ];

        Assert.Equal(expected, LexiconSnapshotDigest.Encode(value));

    }

    [Theory]
    [InlineData("general", new[] { "alpha" }, "1D1CA295A1538D2AEE3794E497EF7766CAA7038BD6D953324A16F0D3B4F422EB")]
    [InlineData("A\0B", new[] { "line1\nline2", "unit\u001Fseparator" }, "4BAD0C07880E6A9A8EE2BDA5F0FAA7C7DF5974F95E0AE4112BF4B8EAAFD8D3E1")]
    [InlineData("魔法", new[] { "café", "🧙" }, "5EB4D021B536093A672F565E4ED6FD598BF89DFEB2724E7B88A81FBD832AF1A2")]
    [InlineData("T", new[] { "b", "a" }, "588222A37CE2E0202F36893FD71C56CE258D425DCA1F4988097A1C9CBB93D9B2")]
    [InlineData("T", new[] { "a", "b" }, "4969AEF8A80E6CF3C81C48759341DD3BAE779CDEB3B8D0F2A8DB7EFC74B51396")]
    public void Format_2_snapshot_digest_matches_pinned_vectors(
        string type,
        string[] facts,
        string expected)
    {

        Assert.Equal(expected, LexiconSnapshotDigest.ComputeHex(Canonical(type, facts)));

    }

    [Fact]
    public void One_fact_containing_a_newline_differs_from_two_facts()
    {

        Assert.NotEqual(
            LexiconSnapshotDigest.Compute(Canonical("T", ["a\nb"])),
            LexiconSnapshotDigest.Compute(Canonical("T", ["a", "b"])));

    }

    [Fact]
    public void Type_and_fact_boundaries_cannot_collide()
    {

        Assert.NotEqual(
            LexiconSnapshotDigest.Compute(Canonical("ab", ["c"])),
            LexiconSnapshotDigest.Compute(Canonical("a", ["bc"])));

    }

    [Fact]
    public void Repeated_facts_disappear_on_the_shared_canonical_path_before_digesting()
    {

        LexiconCanonicalValue repeated = Canonical("T", ["alpha", "alpha", "beta", "alpha"]);
        LexiconCanonicalValue unique = Canonical("T", ["alpha", "beta"]);

        Assert.Equal(["alpha", "beta"], repeated.Facts);
        Assert.Equal(
            LexiconSnapshotDigest.Compute(unique),
            LexiconSnapshotDigest.Compute(repeated));

    }

    [Fact]
    public void Unpaired_surrogates_fail_strict_encoding_even_for_a_manually_constructed_value()
    {

        LexiconCanonicalValue invalidType = Manual("bad\ud800", ["fact"]);
        LexiconCanonicalValue invalidFact = Manual("type", ["bad\ud800"]);

        Assert.Throws<EncoderFallbackException>(() => LexiconSnapshotDigest.Encode(invalidType));
        Assert.Throws<EncoderFallbackException>(() => LexiconSnapshotDigest.Encode(invalidFact));

    }

    [Fact]
    public void Unsupported_format_count_and_byte_length_fail_before_writing()
    {

        LexiconCanonicalValue value = Canonical("T", ["fact"]);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LexiconSnapshotDigest.Encode(format: 1, value));

        using MemoryStream countDestination = new();
        using MemoryStream lengthDestination = new();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LexiconSnapshotDigest.WriteFactCount(countDestination, (ulong)uint.MaxValue + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LexiconSnapshotDigest.WriteByteLength(lengthDestination, (ulong)uint.MaxValue + 1));
        Assert.Equal(0, countDestination.Length);
        Assert.Equal(0, lengthDestination.Length);

    }

    [Fact]
    public void Sensitivity_labels_digest_the_same_exposed_canonical_bytes()
    {

        LexiconCanonicalValue value = Canonical("Project", ["ships Friday"]);
        byte[] bytes = LexiconSnapshotDigest.Encode(value);

        CovenantDigest expected = DerivedArtifactContentDigest.ForBytes(bytes);

        Assert.Equal(expected, LexiconSnapshotDigest.ComputeDerivedArtifactContentDigest(value));

    }

    private static LexiconCanonicalValue Canonical(string type, string[] facts)
    {

        Result<LexiconCanonicalValue> result = LexiconValueNormalizer.NormalizeCorrection(
            "snapshot",
            type,
            facts);

        Assert.True(result.IsSuccess);

        return result.Value;

    }

    private static LexiconCanonicalValue Manual(string type, string[] facts) =>
        new(
            "snapshot",
            "SNAPSHOT",
            type,
            facts,
            "[]",
            string.Join('\n', facts));

}
