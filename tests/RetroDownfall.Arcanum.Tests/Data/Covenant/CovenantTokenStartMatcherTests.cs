using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The token-prefix rule the canonical search fallback shares with the full-text index.
/// </summary>
public sealed class CovenantTokenStartMatcherTests
{
    [Theory]
    [InlineData("sport", "sport", true)]
    [InlineData("sports and games", "sport", true)]
    [InlineData("a sport", "spor", true)]
    [InlineData("A SPORT", "sport", true)]
    [InlineData("a sport", "ort", false)]
    [InlineData("sport", "port", false)]
    [InlineData("one,sport", "sport", true)]
    [InlineData("(sport)", "sport", true)]
    [InlineData("\"sport\"", "sport", true)]
    [InlineData("tab\tsport", "sport", true)]
    [InlineData("line\nsport", "sport", true)]
    public void A_term_matches_only_where_a_word_begins(string value, string term, bool expected) =>
        Assert.Equal(expected, CovenantTokenStartMatcher.Contains(value, term));

    [Theory]
    [InlineData("global.sport", "sport", false)]
    [InlineData("global_sport", "sport", false)]
    [InlineData("global-sport", "sport", false)]
    [InlineData("global.sport", "global.sp", true)]
    [InlineData("global.sport", "global", true)]
    public void The_index_declared_token_characters_do_not_start_a_new_word(string value, string term, bool expected) =>
        Assert.Equal(expected, CovenantTokenStartMatcher.Contains(value, term));

    [Theory]
    [InlineData("naïve sport", "sport", true)]
    [InlineData("naïvesport", "sport", false)]
    [InlineData("日本sport", "sport", false)]
    [InlineData("日本 sport", "sport", true)]
    [InlineData("😀sport", "sport", true)]
    [InlineData("😀 sport", "sport", true)]
    public void Non_ASCII_letters_belong_to_a_word_and_symbols_do_not(string value, string term, bool expected) =>
        Assert.Equal(expected, CovenantTokenStartMatcher.Contains(value, term));

    [Theory]
    [InlineData("किताब", "ताब", true)]
    [InlineData("किताब", "ब", true)]
    [InlineData("किताब", "किताब", true)]
    [InlineData("हिंदी", "दी", true)]
    [InlineData("ab\u0903sport", "sport", true)]
    [InlineData("abc\u20DDdef", "def", true)]
    [InlineData("ab\u0332sport", "sport", true)]
    public void Spacing_enclosing_and_other_marks_the_index_does_not_keep_inside_a_word_separate_words(
        string value,
        string term,
        bool expected) =>
        Assert.Equal(expected, CovenantTokenStartMatcher.Contains(value, term));

    [Theory]
    [InlineData("ab\u0308sport", "sport", false)]
    [InlineData("nai\u0308vesport", "sport", false)]
    [InlineData("x\u0301\u0308sport", "sport", false)]
    [InlineData("ab\u0323sport", "sport", false)]
    [InlineData(" \u0301sport", "sport", true)]
    [InlineData("a \u0301\u0308sport", "sport", true)]
    public void A_combining_diacritic_the_index_keeps_continues_the_word_it_rides_on_and_never_starts_one(
        string value,
        string term,
        bool expected) =>
        Assert.Equal(expected, CovenantTokenStartMatcher.Contains(value, term));

    [Fact]
    public void A_later_occurrence_at_a_word_start_is_found_after_an_earlier_mid_word_one()
    {
        Assert.False(CovenantTokenStartMatcher.Contains("support", "port"));

        Assert.True(CovenantTokenStartMatcher.Contains("support port", "port"));
    }

    [Fact]
    public void A_term_that_opens_with_a_separator_is_matched_as_written()
    {
        // There is no token boundary to honour in front of a separator, so the substring it refines is
        // kept as it was.
        Assert.True(CovenantTokenStartMatcher.Contains("a*e", "*e"));

        Assert.False(CovenantTokenStartMatcher.Contains("everything", "*e"));

        // A combining mark the index does not keep inside a word is a separator in front of a term too.
        Assert.True(CovenantTokenStartMatcher.Contains("किताब", "\u093Eब"));
    }

    [Theory]
    [InlineData(null, "term")]
    [InlineData("value", null)]
    [InlineData("", "term")]
    [InlineData("value", "")]
    public void A_missing_or_empty_side_never_matches(string? value, string? term) =>
        Assert.False(CovenantTokenStartMatcher.Contains(value, term));

    [Fact]
    public void A_lone_surrogate_before_a_term_separates_like_any_other_non_token()
    {
        Assert.True(CovenantTokenStartMatcher.Contains("\ud83dsport", "sport"));

        Assert.True(CovenantTokenStartMatcher.Contains("\ude00sport", "sport"));
    }
}
