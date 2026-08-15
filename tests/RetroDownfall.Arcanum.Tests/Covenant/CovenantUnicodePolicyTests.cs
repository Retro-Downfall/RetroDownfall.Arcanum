using System.Buffers.Binary;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CovenantUnicodePolicyTests
{
    private const string NormalizationTestSha256 = "5019FFD530751A741900C849C0E010332F142A3612234639BD200B82138A87DB";

    private static readonly (int Start, int End)[] FormatRanges =
    [
        (0x00ad, 0x00ad),
        (0x0600, 0x0605),
        (0x061c, 0x061c),
        (0x06dd, 0x06dd),
        (0x070f, 0x070f),
        (0x0890, 0x0891),
        (0x08e2, 0x08e2),
        (0x180e, 0x180e),
        (0x200b, 0x200f),
        (0x202a, 0x202e),
        (0x2060, 0x2064),
        (0x2066, 0x206f),
        (0xfeff, 0xfeff),
        (0xfff9, 0xfffb),
        (0x110bd, 0x110bd),
        (0x110cd, 0x110cd),
        (0x13430, 0x1343f),
        (0x1bca0, 0x1bca3),
        (0x1d173, 0x1d17a),
        (0xe0001, 0xe0001),
        (0xe0020, 0xe007f)
    ];

    [Fact]
    public void Generated_table_counts_match_the_unicode_17_sources()
    {
        Assert.Equal(2_081, CovenantUnicodePolicyV1.CanonicalDecompositionCount);
        Assert.Equal(968, CovenantUnicodePolicyV1.NonzeroCombiningClassCount);
        Assert.Equal(1_120, CovenantUnicodePolicyV1.FullCompositionExclusionCount);
        Assert.Equal(961, CovenantUnicodePolicyV1.CompositionPairCount);
        Assert.Equal(170, CovenantUnicodePolicyV1.FormatScalarCount);
        Assert.Equal(21, CovenantUnicodePolicyV1.FormatRangeCount);
    }

    [Fact]
    public void Every_unicode_17_format_scalar_is_rejected_and_adjacent_scalars_are_allowed()
    {
        ICovenantCompiler compiler = new CovenantCompiler();
        int rejected = 0;

        foreach ((int start, int end) in FormatRanges)
        {
            for (int scalar = start; scalar <= end; scalar++)
            {
                Assert.True(CovenantUnicodePolicyV1.IsFormatScalar(scalar));

                string authored = string.Concat("a", char.ConvertFromUtf32(scalar), "b");

                Assert.Throws<ArgumentException>(() => compiler.Compile("format.scalar", authored));
                rejected++;
            }

            AssertAdjacentAllowed(compiler, start - 1);
            AssertAdjacentAllowed(compiler, end + 1);
        }

        Assert.Equal(170, rejected);
    }

    [Fact]
    public void Corpus_format_oracle_rejects_same_count_range_identity_substitution()
    {
        ulong[] expectedRanges = FormatRanges
            .Select(range => ((ulong)range.Start << 21) | (uint)range.End)
            .ToArray();
        ulong[] substitutedRanges = expectedRanges.ToArray();

        substitutedRanges[0] = ((ulong)0x00ae << 21) | 0x00ae;

        Assert.Equal(21, substitutedRanges.Length);
        Assert.Equal(170, CountFormatScalars(expectedRanges));
        Assert.Equal(170, CountFormatScalars(substitutedRanges));
        Assert.True(CovenantCompilerCorpus.HasExpectedFormatRanges(expectedRanges));
        Assert.False(CovenantCompilerCorpus.HasExpectedFormatRanges(substitutedRanges));
        Assert.Equal(
            "6DF8AF9322877261DB229070C3949BD7D8BEA11E379797EFDBF0283842C8EF65",
            CovenantCompilerCorpus.ComputeExpectedFormatRangeIdentityHash().ToString());
    }

    private static int CountFormatScalars(IEnumerable<ulong> ranges) =>
        ranges.Sum(range => checked((int)(range & 0x1fffff) - (int)(range >> 21) + 1));

    [Theory]
    [InlineData(0x061c)]
    [InlineData(0x200e)]
    [InlineData(0x200f)]
    [InlineData(0x202a)]
    [InlineData(0x202b)]
    [InlineData(0x202c)]
    [InlineData(0x202d)]
    [InlineData(0x202e)]
    [InlineData(0x2066)]
    [InlineData(0x2067)]
    [InlineData(0x2068)]
    [InlineData(0x2069)]
    public void Directional_marks_overrides_and_isolates_are_rejected(int scalar)
    {
        ICovenantCompiler compiler = new CovenantCompiler();
        string authored = string.Concat("left", char.ConvertFromUtf32(scalar), "right");

        Assert.Throws<ArgumentException>(() => compiler.Compile("bidi.example", authored));
    }

    [Fact]
    public void NormalizeToNfc_handles_recursive_singleton_decomposition_and_composition()
    {
        Assert.Equal("\u00c5", CovenantUnicodePolicyV1.NormalizeToNfc("\u212b"));
        Assert.Equal("Caf\u00e9", CovenantUnicodePolicyV1.NormalizeToNfc("Cafe\u0301"));
    }

    [Fact]
    public void NormalizeToNfc_keeps_full_composition_exclusions_decomposed()
    {
        Assert.Equal("\u0308\u0301", CovenantUnicodePolicyV1.NormalizeToNfc("\u0344"));
        Assert.Equal("\u0f71\u0f72", CovenantUnicodePolicyV1.NormalizeToNfc("\u0f73"));
    }

    [Fact]
    public void NormalizeToNfc_stably_reorders_combining_marks_and_honors_blocking()
    {
        Assert.Equal("\u0316\u0301\u0300", CovenantUnicodePolicyV1.NormalizeToNfc("\u0301\u0316\u0300"));
        Assert.Equal("\u0301\u0300", CovenantUnicodePolicyV1.NormalizeToNfc("\u0301\u0300"));
        Assert.Equal("A\u0305\u030a", CovenantUnicodePolicyV1.NormalizeToNfc("A\u0305\u030a"));
    }

    [Fact]
    public void NormalizeToNfc_handles_leading_nonstarters_supplementary_scalars_and_long_sequences()
    {
        string longSequence = "A" + new string('\u0305', 1_000) + "\u030a";

        Assert.Equal("\u0316A", CovenantUnicodePolicyV1.NormalizeToNfc("\u0316A"));
        Assert.Equal("\U0001f600", CovenantUnicodePolicyV1.NormalizeToNfc("\U0001f600"));
        Assert.Equal(longSequence, CovenantUnicodePolicyV1.NormalizeToNfc(longSequence));
    }

    [Fact]
    public void NormalizeToNfc_composes_hangul_at_each_algorithmic_boundary()
    {
        Assert.Equal("\uac00", CovenantUnicodePolicyV1.NormalizeToNfc("\u1100\u1161"));
        Assert.Equal("\uac01", CovenantUnicodePolicyV1.NormalizeToNfc("\u1100\u1161\u11a8"));
        Assert.Equal("\uac01", CovenantUnicodePolicyV1.NormalizeToNfc("\uac00\u11a8"));
        Assert.Equal("\u1100\u1160", CovenantUnicodePolicyV1.NormalizeToNfc("\u1100\u1160"));
        Assert.Equal("\ud7a3\u11c3", CovenantUnicodePolicyV1.NormalizeToNfc("\ud7a3\u11c3"));
    }

    [Theory]
    [InlineData("\ufb03")]
    [InlineData("\u2460")]
    [InlineData("\u00a0")]
    [InlineData("\u2163")]
    public void NormalizeToNfc_preserves_compatibility_distinctions(string value)
    {
        Assert.Equal(value, CovenantUnicodePolicyV1.NormalizeToNfc(value));
    }

    [Fact]
    public void NormalizeToNfc_rejects_malformed_utf16()
    {
        string[] malformed =
        [
            new('\ud800', 1),
            new('\udfff', 1),
            string.Concat("a", new string('\ud800', 1), "b")
        ];

        foreach (string value in malformed)
        {
            Assert.Throws<ArgumentException>(() => CovenantUnicodePolicyV1.NormalizeToNfc(value));
        }
    }

    [Theory]
    [InlineData("Cafe\u0301")]
    [InlineData("\u212b")]
    [InlineData("\u1100\u1161\u11a8")]
    [InlineData("\u0301\u0316\u0300")]
    public void NormalizeToNfc_is_idempotent(string value)
    {
        string normalized = CovenantUnicodePolicyV1.NormalizeToNfc(value);

        Assert.Equal(normalized, CovenantUnicodePolicyV1.NormalizeToNfc(normalized));
    }

    [Fact]
    public void Complete_unicode_17_normalization_corpus_conforms_to_nfc()
    {
        byte[] corpus = ReadNormalizationCorpus();
        int offset = 0;

        Assert.Equal("ARCUNFC1", Encoding.ASCII.GetString(corpus.AsSpan(offset, 8)));
        offset += 8;

        Assert.Equal(NormalizationTestSha256, Convert.ToHexString(corpus.AsSpan(offset, 32)));
        offset += 32;

        int caseCount = checked((int)BinaryPrimitives.ReadUInt32BigEndian(corpus.AsSpan(offset, sizeof(uint))));
        offset += sizeof(uint);

        for (int index = 0; index < caseCount; index++)
        {
            int lineNumber = checked((int)BinaryPrimitives.ReadUInt32BigEndian(corpus.AsSpan(offset, sizeof(uint))));
            offset += sizeof(uint);
            string c1 = ReadUtf8Field(corpus, ref offset);
            string c2 = ReadUtf8Field(corpus, ref offset);
            string c3 = ReadUtf8Field(corpus, ref offset);
            string c4 = ReadUtf8Field(corpus, ref offset);
            string c5 = ReadUtf8Field(corpus, ref offset);

            AssertNfc(c2, c1, lineNumber, "c1");
            AssertNfc(c2, c2, lineNumber, "c2");
            AssertNfc(c2, c3, lineNumber, "c3");
            AssertNfc(c4, c4, lineNumber, "c4");
            AssertNfc(c4, c5, lineNumber, "c5");
        }

        Assert.Equal(20_034, caseCount);
        Assert.Equal(corpus.Length, offset);
    }

    private static void AssertAdjacentAllowed(ICovenantCompiler compiler, int scalar)
    {
        if (scalar is < 0 or > 0x10ffff || scalar is >= 0xd800 and <= 0xdfff)
        {
            return;
        }

        Assert.False(CovenantUnicodePolicyV1.IsFormatScalar(scalar));

        string authored = string.Concat("a", char.ConvertFromUtf32(scalar), "b");
        CovenantCompiledContent compiled = compiler.Compile("format.adjacent", authored);

        Assert.Equal(authored, compiled.AuthoredContent);
    }

    private static byte[] ReadNormalizationCorpus()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "Covenant",
            "Unicode17",
            "NormalizationTest.nfc.bin");

        return File.ReadAllBytes(path);
    }

    private static string ReadUtf8Field(byte[] corpus, ref int offset)
    {
        int length = BinaryPrimitives.ReadUInt16BigEndian(corpus.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        string value = new UTF8Encoding(false, true).GetString(corpus.AsSpan(offset, length));
        offset = checked(offset + length);

        return value;
    }

    private static void AssertNfc(string expected, string input, int lineNumber, string column)
    {
        string actual = CovenantUnicodePolicyV1.NormalizeToNfc(input);

        Assert.True(
            string.Equals(expected, actual, StringComparison.Ordinal),
            $"Unicode 17 NFC mismatch at line {lineNumber}, {column}. Expected {ToScalars(expected)}, actual {ToScalars(actual)}.");
    }

    private static string ToScalars(string value)
    {
        StringBuilder result = new();

        foreach (Rune rune in value.EnumerateRunes())
        {
            if (result.Length > 0)
            {
                result.Append(' ');
            }

            result.Append(rune.Value.ToString("X4"));
        }

        return result.ToString();
    }
}
