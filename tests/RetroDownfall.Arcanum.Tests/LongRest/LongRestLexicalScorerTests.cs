using System.Collections.Immutable;
using System.Numerics;

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
}
