using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Scoped lexical ranking is a pure integer function of one generation's own facts. The expected numbers below were produced by an
// independent Python implementation of the written formula (integer arithmetic only), not by this code.
public sealed class LongRestLexicalScorerTests
{
    [Theory]
    [InlineData(1, 1, 6)]
    [InlineData(2, 1, 16)]
    [InlineData(2, 2, 4)]
    [InlineData(4, 3, 8)]
    [InlineData(4, 1, 27)]
    [InlineData(10, 1, 45)]
    [InlineData(10, 5, 16)]
    [InlineData(10, 10, 1)]
    [InlineData(41, 41, 0)]
    [InlineData(41, 1, 76)]
    [InlineData(100, 30, 27)]
    [InlineData(128, 17, 46)]
    [InlineData(1000, 10, 105)]
    [InlineData(1000, 500, 16)]
    [InlineData(1000, 1000, 0)]
    [InlineData(100000, 1, 256)]
    [InlineData(100000, 50000, 16)]
    [InlineData(2147483647, 1, 486)]
    [InlineData(2147483647, 2147483647, 0)]
    [InlineData(5, 6, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(7, 0, 64)]
    // Where the ratio lies within one unit of the sixteenth bit above a step boundary the integer arithmetic is one step below the exact
    // floor(16 * log2(ratio)); these rows are such boundaries (the exact floors are 7, 3, 21, 17, 1), found by an exhaustive comparison with
    // an independent big-integer implementation. They pin the definition, so a change that "corrects" it is a change of the scorer version.
    [InlineData(150, 111, 6)]
    [InlineData(159, 140, 2)]
    [InlineData(266, 107, 20)]
    [InlineData(282, 135, 16)]
    [InlineData(341, 327, 0)]
    public void A_term_weighs_the_sixteenths_of_a_bit_its_rarity_is_worth(long documents, int frequency, int expected) =>
        Assert.Equal(expected, Assert.Single(LongRestLexicalScorer.Weights(documents, [frequency])));

    [Fact]
    public void A_weight_is_within_one_sixteenth_of_the_exact_logarithm_and_never_rises_with_document_frequency()
    {
        for (long documents = 1; documents <= 150; documents++)
        {
            int previous = int.MaxValue;

            for (int frequency = 0; frequency <= documents; frequency++)
            {
                int weight = Assert.Single(LongRestLexicalScorer.Weights(documents, [frequency]));

                // The exact floor(16 * log2((2N + 2) / (2df + 1))): the largest k with (2N + 2)^16 >= 2^k * (2df + 1)^16.
                BigInteger numerator = BigInteger.Pow(2 * documents + 2, 16);

                BigInteger denominator = BigInteger.Pow(2 * frequency + 1, 16);

                int exact = 0;

                while (numerator >= (BigInteger.One << (exact + 1)) * denominator)
                {
                    exact++;
                }

                Assert.InRange(exact - weight, 0, 1);

                Assert.True(weight <= previous, $"N={documents} df={frequency}");

                previous = weight;
            }
        }
    }

    [Theory]
    [InlineData(4, 3, 2, 3, 14)]
    [InlineData(4, 3, 1, 3, 12)]
    [InlineData(41, 41, 1, 6, 0)]
    [InlineData(1000, 10, 1, 12, 140)]
    public void A_single_term_scores_its_weight_times_the_saturating_frequency_component(long documents, int frequency, int occurrences, int length, long expected)
    {
        ImmutableArray<int> weights = LongRestLexicalScorer.Weights(documents, [frequency]);

        Assert.Equal(expected, LongRestLexicalScorer.Score(weights, new(length, [occurrences])));
    }

    [Theory]
    [InlineData(1000, new[] { 10, 500 }, new[] { 1, 1 }, 12, 162)]
    [InlineData(1000, new[] { 10, 500 }, new[] { 3, 2 }, 40, 177)]
    [InlineData(128, new[] { 17, 64, 3 }, new[] { 1, 1, 2 }, 9, 230)]
    [InlineData(100000, new[] { 1, 2, 3, 4, 5 }, new[] { 1, 0, 2, 0, 1 }, 25, 874)]
    [InlineData(10, new[] { 1, 1 }, new[] { 0, 0 }, 5, 0)]
    [InlineData(100, new[] { 30, 30, 30, 30 }, new[] { 1, 1, 1, 1 }, 4, 168)]
    public void Terms_add_up_in_one_score_and_an_absent_term_adds_nothing(long documents, int[] frequencies, int[] occurrences, int length, long expected)
    {
        ImmutableArray<int> weights = LongRestLexicalScorer.Weights(documents, [.. frequencies]);

        Assert.Equal(expected, LongRestLexicalScorer.Score(weights, new(length, [.. occurrences])));
    }

    [Fact]
    public void A_longer_document_scores_lower_for_the_same_occurrences_and_more_occurrences_saturate()
    {
        ImmutableArray<int> weights = LongRestLexicalScorer.Weights(1000, [10]);

        long shortDocument = LongRestLexicalScorer.Score(weights, new(5, [1]));

        long pivotDocument = LongRestLexicalScorer.Score(weights, new(LongRestLexicalScorer.PivotTokens, [1]));

        long longDocument = LongRestLexicalScorer.Score(weights, new(500, [1]));

        Assert.True(shortDocument > pivotDocument && pivotDocument > longDocument);

        long once = LongRestLexicalScorer.Score(weights, new(40, [1]));

        long twice = LongRestLexicalScorer.Score(weights, new(40, [2]));

        long many = LongRestLexicalScorer.Score(weights, new(40, [40]));

        long fortyOne = LongRestLexicalScorer.Score(weights, new(40, [41]));

        Assert.True(once < twice && twice < many);

        // BM25 saturates at (k1 + 1) = 2.2 times the weight; 41 occurrences are within a rounding step of 40.
        Assert.InRange(fortyOne - many, 0, 2);

        Assert.True(many <= 105 * 2200 / 1000 + 1);
    }

    [Fact]
    public void A_score_is_a_pure_function_of_the_generations_own_facts()
    {
        ImmutableArray<string> terms = LongRestLexicalScorer.QueryTerms("Opal and Quartz in the opal vault");

        LongRestLexicalDocument document = LongRestLexicalScorer.Profile("an OPAL gem beside quartz, quartz and more", terms);

        ImmutableArray<int> weights = LongRestLexicalScorer.Weights(64, [3, 5, 60, 40, 62, 2]);

        long first = LongRestLexicalScorer.Score(weights, document);

        for (int repeat = 0; repeat < 3; repeat++)
        {
            Assert.Equal(first, LongRestLexicalScorer.Score(LongRestLexicalScorer.Weights(64, [3, 5, 60, 40, 62, 2]),
                LongRestLexicalScorer.Profile("an OPAL gem beside quartz, quartz and more", LongRestLexicalScorer.QueryTerms("Opal and Quartz in the opal vault"))));
        }

        // The same facts in another generation of the same size give the same score; a different size does not.
        Assert.Equal(first, LongRestLexicalScorer.Score(LongRestLexicalScorer.Weights(64, [3, 5, 60, 40, 62, 2]), document));

        Assert.NotEqual(first, LongRestLexicalScorer.Score(LongRestLexicalScorer.Weights(640, [3, 5, 60, 40, 62, 2]), document));
    }

    [Fact]
    public void Query_terms_are_distinct_lower_cased_tokens_in_first_occurrence_order()
    {
        Assert.Equal(["opal", "and", "quartz", "in", "the", "vault"], LongRestLexicalScorer.QueryTerms("Opal and Quartz, in the OPAL vault; opal").ToArray());

        Assert.Equal(["r2", "d2", "c3po"], LongRestLexicalScorer.QueryTerms("R2-D2 / c3po (R2)").ToArray());

        Assert.Empty(LongRestLexicalScorer.QueryTerms(""));

        Assert.Empty(LongRestLexicalScorer.QueryTerms(" ,.;- !?"));

        Assert.Equal(["éclair", "ünder"], LongRestLexicalScorer.QueryTerms("ÉCLAIR Ünder éclair").ToArray());
    }

    [Fact]
    public void Query_terms_stop_at_sixteen_distinct_tokens_and_a_repeated_token_does_not_spend_a_slot()
    {
        string content = string.Join(' ', Enumerable.Range(0, 40).Select(index => "t" + (index % 20)));

        string[] terms = [.. LongRestLexicalScorer.QueryTerms(content)];

        Assert.Equal(16, terms.Length);

        Assert.Equal(Enumerable.Range(0, 16).Select(index => "t" + index), terms);

        string[] repeated = [.. LongRestLexicalScorer.QueryTerms(string.Join(' ', Enumerable.Repeat("same", 50).Concat(Enumerable.Range(0, 15).Select(index => "w" + index))))];

        Assert.Equal(16, repeated.Length);

        Assert.Equal("same", repeated[0]);
    }

    [Fact]
    public void A_token_longer_than_128_utf8_bytes_is_not_a_term_and_does_not_count_toward_the_limit()
    {
        string exact = new('a', 128);

        string over = new('b', 129);

        string twoByte = new('é', 65);

        Assert.Equal([exact, "short"], LongRestLexicalScorer.QueryTerms($"{over} {exact} {twoByte} short").ToArray());
    }

    [Fact]
    public void A_profile_counts_every_token_for_length_and_the_occurrences_of_each_term()
    {
        ImmutableArray<string> terms = LongRestLexicalScorer.QueryTerms("opal quartz absent");

        LongRestLexicalDocument document = LongRestLexicalScorer.Profile("Opal, OPAL; opalescent quartz " + new string('z', 300) + " tail", terms);

        Assert.Equal(6, document.Length);

        Assert.Equal([2, 1, 0], document.Frequencies.ToArray());

        LongRestLexicalDocument empty = LongRestLexicalScorer.Profile("", terms);

        Assert.Equal(0, empty.Length);

        Assert.Equal([0, 0, 0], empty.Frequencies.ToArray());
    }

    [Fact]
    public void The_scorer_refuses_facts_that_do_not_line_up()
    {
        Assert.Throws<ArgumentException>(() => LongRestLexicalScorer.Score(LongRestLexicalScorer.Weights(10, [1, 2]), new(3, [1])));

        Assert.Throws<ArgumentException>(() => LongRestLexicalScorer.Score(default, new(3, [1])));

        Assert.Throws<ArgumentException>(() => LongRestLexicalScorer.Profile("text", default));

        Assert.Throws<ArgumentException>(() => LongRestLexicalScorer.Weights(10, default));

        Assert.Throws<ArgumentNullException>(() => LongRestLexicalScorer.QueryTerms(null!));
    }

    // The generation filter is what bounds the matched set to one generation, and the terms are the only free text in the expression, so the
    // exact text is pinned here: a change to it is a change to which rows can match.
    [Fact]
    public void The_match_expression_names_the_generation_and_quotes_every_term_as_a_phrase()
    {
        Guid generation = Guid.Parse("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");

        Assert.Equal("GenerationId : \"0A1B2C3D-4E5F-4A6B-8C7D-9E0F1A2B3C4D\" AND Content : (\"opal\" OR \"quartz\")",
            LongRestLexicalScorer.MatchExpression(generation, ["opal", "quartz"]));

        // Words that are operators to FTS5 are only phrases here.
        Assert.Equal("GenerationId : \"0A1B2C3D-4E5F-4A6B-8C7D-9E0F1A2B3C4D\" AND Content : (\"and\" OR \"or\" OR \"not\" OR \"near\" OR \"x1y2\" OR \"é\" OR \"日本語\")",
            LongRestLexicalScorer.MatchExpression(generation, ["and", "or", "not", "near", "x1y2", "é", "日本語"]));

        // The generation is the uppercase hyphenated spelling the index stores, whatever the Guid's own casing.
        Assert.StartsWith("GenerationId : \"FFFFFFFF-0000-4000-8000-00000000000A\"", LongRestLexicalScorer.MatchExpression(Guid.Parse("ffffffff-0000-4000-8000-00000000000a"), ["x"]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("two words")]
    [InlineData("a\"b")]
    [InlineData("\"")]
    [InlineData("opal*")]
    [InlineData("^opal")]
    [InlineData("opal-quartz")]
    [InlineData("NEAR(opal")]
    [InlineData("a:b")]
    [InlineData("a\nb")]
    [InlineData("a\u0000b")]
    public void A_term_that_is_not_exactly_letters_and_digits_is_refused_rather_than_escaped(string term)
    {
        Assert.Throws<ArgumentException>(() => LongRestLexicalScorer.MatchExpression(Guid.NewGuid(), ["fine", term]));
    }

    [Fact]
    public void A_match_needs_a_generation_and_a_term()
    {
        Assert.Throws<ArgumentException>(() => LongRestLexicalScorer.MatchExpression(Guid.Empty, ["opal"]));

        Assert.Throws<ArgumentException>(() => LongRestLexicalScorer.MatchExpression(Guid.NewGuid(), []));

        Assert.Throws<ArgumentException>(() => LongRestLexicalScorer.MatchExpression(Guid.NewGuid(), default));
    }

    [Fact]
    public void Whatever_text_the_query_terms_come_from_the_expression_is_the_generation_filter_and_a_list_of_quoted_phrases_only()
    {
        Guid generation = Guid.NewGuid();

        string hostile = "alpha\" OR GenerationId : \"00000000-0000-0000-0000-000000000000\" NEAR(a b) -c ^d e* AND (f) {Content}: g NOT h \u0000 日本語 é x1";

        ImmutableArray<string> terms = LongRestLexicalScorer.QueryTerms(hostile);

        string expression = LongRestLexicalScorer.MatchExpression(generation, terms);

        string prefix = "GenerationId : \"" + generation.ToString("D").ToUpperInvariant() + "\" AND Content : (";

        Assert.StartsWith(prefix, expression, StringComparison.Ordinal);

        Assert.EndsWith(")", expression, StringComparison.Ordinal);

        string[] phrases = expression[prefix.Length..^1].Split(" OR ", StringSplitOptions.None);

        Assert.Equal(terms.Length, phrases.Length);

        Assert.All(phrases, phrase => Assert.Matches("^\"[\\p{L}\\p{N}]+\"$", phrase));

        // Exactly one generation filter, and it is the target's: nothing the text said could add or replace it.
        Assert.Equal(1, expression.Split("GenerationId", StringSplitOptions.None).Length - 1);

        Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", expression, StringComparison.Ordinal);
    }

    // The scoring function is what a recipe means by "lexical score", so its outputs are pinned to Version: this digest and the version move
    // together. If this test fails, the tokenisation, a weight, or the frequency component changed: move Version, re-pin the digest, and
    // expect every recipe digest to move with it.
    [Fact]
    public void The_scorer_version_is_bound_into_the_recipe_and_pins_the_scoring_function()
    {
        CovenantDigestHolder digest = new();

        LongRestRecipeIdentity recipe = new(1, 1, "provider", "model", digest.Value, "provider", "embedding", digest.Value, 64);

        Assert.Equal(LongRestLexicalScorer.Version, recipe.LexicalScorerVersion);

        Assert.NotEqual(LongRestDiscoveryHash.Recipe(recipe).Value, LongRestDiscoveryHash.Recipe(recipe with { LexicalScorerVersion = recipe.LexicalScorerVersion + 1 }).Value);

        Assert.True(LongRestDiscoveryHash.Recipe(recipe with { LexicalScorerVersion = 0 }).IsFailure);

        Assert.True(LongRestDiscoveryHash.Recipe(recipe with { LexicalScorerVersion = -1 }).IsFailure);

        StringBuilder table = new();

        foreach (long documents in new long[] { 1, 2, 3, 4, 5, 7, 10, 16, 17, 31, 32, 33, 64, 100, 128, 150, 159, 255, 256, 266, 282, 341, 1000, 4096, 100000, 2147483647 })
        {
            foreach (long frequency in new long[] { 0, 1, 2, 3, 5, documents / 3, documents / 2, documents - 1, documents }.Where(value => value >= 0 && value <= documents).Distinct())
            {
                table.Append(CultureInfo.InvariantCulture, $"w {documents} {frequency} {Assert.Single(LongRestLexicalScorer.Weights(documents, [(int)frequency]))}\n");
            }
        }

        foreach (int weight in new[] { 1, 16, 64, 256 })
        {
            foreach (int length in new[] { 0, 1, 5, 32, 500 })
            {
                foreach (int occurrences in new[] { 1, 2, 5, 40 })
                {
                    table.Append(CultureInfo.InvariantCulture, $"s {weight} {length} {occurrences} {LongRestLexicalScorer.Score([weight], new(length, [occurrences]))}\n");
                }
            }
        }

        ImmutableArray<string> terms = LongRestLexicalScorer.QueryTerms("Opal, OPAL; quartz  é-ß 日本語 x1y2 İ ǅ " + new string('z', 200) + " tail");

        LongRestLexicalDocument document = LongRestLexicalScorer.Profile("opal OPAL quartz ß 日本語 x1y2 tail tail", terms);

        table.Append(CultureInfo.InvariantCulture, $"t {string.Join('|', terms)} {document.Length} {string.Join(',', document.Frequencies)}\n");

        Assert.Equal(1, LongRestLexicalScorer.Version);

        Assert.Equal("BE661C42894F8FE5E3D21E38FB11EED12BCDC78000BC96FEE140A5C2431039BD", Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(table.ToString()))));
    }

    private sealed class CovenantDigestHolder
    {
        internal RetroDownfall.Arcanum.Core.Covenant.CovenantDigest Value { get; } = new(new byte[32]);
    }
}
