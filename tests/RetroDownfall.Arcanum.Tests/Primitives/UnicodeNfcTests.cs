using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Primitives;

/// <summary>
/// <see cref="UnicodeNfc"/> is the product's own canonical composition, so it must not depend on the globalization library that the
/// shipping executable runs without. Every expected string here is written out as scalar values, never produced with
/// <see cref="string.Normalize()"/>, which would agree with the code under test whatever either did.
/// </summary>
public sealed class UnicodeNfcTests
{
    [Theory]
    [InlineData("café", "café")]
    [InlineData("café", "café")]
    [InlineData("Å", "Å")]
    [InlineData("Å", "Å")]
    [InlineData("̈́", "̈́")]
    [InlineData("ཱི", "ཱི")]
    [InlineData("α\u0313\u0300\u0345", "\u1f82")]
    [InlineData("̖́̀", "̖́̀")]
    [InlineData("́̀", "́̀")]
    [InlineData("̖A", "̖A")]
    [InlineData("가", "가")]
    [InlineData("각", "각")]
    [InlineData("각", "각")]
    [InlineData("ᄀᅠ", "ᄀᅠ")]
    [InlineData("\U0001f600", "\U0001f600")]
    [InlineData("ﬃ", "ﬃ")]
    [InlineData("①", "①")]
    [InlineData("", "")]
    [InlineData("/Users/u/plain ascii", "/Users/u/plain ascii")]
    public void The_normal_form_is_composed_decomposed_reordered_and_composed_again_from_the_unicode_17_tables(string input, string expected) =>
        Assert.Equal(expected, UnicodeNfc.Normalize(input));

    [Fact]
    public void Two_spellings_of_one_name_normalize_to_the_same_string_and_the_form_is_stable()
    {
        string composed = "/Users/u/Café/Ångström";

        string decomposed = "/Users/u/Café/Ångström";

        Assert.NotEqual(composed, decomposed);

        Assert.Equal(composed, UnicodeNfc.Normalize(decomposed));

        Assert.Equal(composed, UnicodeNfc.Normalize(composed));

        Assert.Equal(composed, UnicodeNfc.Normalize(UnicodeNfc.Normalize(decomposed)));
    }

    [Fact]
    public void A_string_that_is_not_well_formed_utf16_is_refused_and_null_is_not_a_string()
    {
        foreach (string malformed in (string[])["\ud800", "\udfff", "a\ud800b", "café\udc00"])
        {
            ArgumentException refused = Assert.Throws<ArgumentException>(() => UnicodeNfc.Normalize(malformed));

            Assert.IsType<EncoderFallbackException>(refused.InnerException);
        }

        Assert.Throws<ArgumentNullException>(() => UnicodeNfc.Normalize(null!));
    }

    // The size cap belongs to the Covenant, which bounds its authored text before it asks; a registered path has no such limit here.
    [Fact]
    public void The_helper_has_no_size_cap_and_the_covenant_keeps_its_own()
    {
        string large = string.Concat(Enumerable.Repeat("é", CovenantLimits.MaxAuthoredContentBytes));

        Assert.True(Encoding.UTF8.GetByteCount(large) > CovenantLimits.MaxAuthoredContentBytes);

        string expected = string.Concat(Enumerable.Repeat("é", CovenantLimits.MaxAuthoredContentBytes));

        Assert.Equal(expected, UnicodeNfc.Normalize(large));

        Assert.Throws<ArgumentException>(() => CovenantUnicodePolicyV1.NormalizeToNfc(large));

        Assert.Equal("café", CovenantUnicodePolicyV1.NormalizeToNfc("café"));
    }

    // Where the host has a globalization library, its answer is a second opinion for characters old enough that every Unicode version
    // agrees on them. Under invariant globalization string.Normalize gives its input back, so there is nothing to compare with there.
    [SkippableFact]
    public void Where_the_host_can_normalize_it_agrees_on_a_corpus_of_established_characters()
    {
        Skip.If(
            !string.Equals("e\u0301".Normalize(NormalizationForm.FormC), "\u00e9", StringComparison.Ordinal),
            "Host globalization does not normalize.");

        string[] pieces =
        [
            "a", "e", "o", "n", "A", "E", "Z", " ", "/", "̀", "́", "̂", "̃", "̈", "̊", "̖", "̣",
            "̧", "ͅ", "é", "Å", "Å", "Ạ", "ḋ", "ᄀ", "ᅡ", "ᆨ", "가", "각", "ཱ",
            "ི", "ཱི", "̈́", "क़", "Ω", "\U0001d15e", "\U0001f600",
        ];

        Random random = new(20261016);

        for (int round = 0; round < 2_000; round++)
        {
            string value = string.Concat(Enumerable.Range(0, random.Next(0, 12)).Select(_ => pieces[random.Next(pieces.Length)]));

            Assert.Equal(value.Normalize(NormalizationForm.FormC), UnicodeNfc.Normalize(value));
        }
    }

    [Fact]
    public void Every_canonical_decomposition_record_preserves_equivalence_and_scalar_order()
    {
        int visited = 0;

        foreach (ulong record in CovenantUnicode17Tables.CanonicalDecompositions)
        {
            int scalar = checked((int)(record >> 13));

            int offset = checked((int)((record >> 1) & 0x0fff));

            int length = checked((int)(record & 1) + 1);

            string original = char.ConvertFromUtf32(scalar);

            string decomposition = char.ConvertFromUtf32(checked((int)CovenantUnicode17Tables.DecompositionScalars[offset]));

            if (length == 2)
            {
                decomposition += char.ConvertFromUtf32(checked((int)CovenantUnicode17Tables.DecompositionScalars[offset + 1]));
            }

            string normalized = UnicodeNfc.Normalize(original);

            Assert.Equal(UnicodeNfc.Normalize(decomposition), normalized);

            Assert.Equal(normalized, UnicodeNfc.Normalize(normalized));

            visited++;
        }

        Assert.Equal(2_081, visited);
    }

    [Fact]
    public void Every_hangul_syllable_matches_its_ordered_algorithmic_jamo_sequence()
    {
        for (int index = 0; index < 11_172; index++)
        {
            string syllable = char.ConvertFromUtf32(0xac00 + index);

            string jamo = char.ConvertFromUtf32(0x1100 + index / (21 * 28))
                + char.ConvertFromUtf32(0x1161 + index % (21 * 28) / 28);

            if (index % 28 != 0)
            {
                jamo += char.ConvertFromUtf32(0x11a7 + index % 28);
            }

            Assert.Equal(syllable, UnicodeNfc.Normalize(jamo));

            Assert.Equal(syllable, UnicodeNfc.Normalize(syllable));
        }
    }
}
