using System.Collections.Immutable;
using System.Text;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// One document's lexical facts against one query: its length in tokens and how often each query term occurs in it, in the query's
/// term order.
/// </summary>
public sealed record LongRestLexicalDocument(int Length, ImmutableArray<int> Frequencies);

/// <summary>
/// Scoped lexical ranking. Relevance is a pure integer function of one generation's own facts: the number of documents in the
/// generation, how many of them contain each query term, and the document's term frequencies and length. Nothing outside the
/// generation (another scope, another protected partition, a retained generation, a summary row) can move a score, there is no
/// floating point and no library logarithm, so every platform ranks identically.
/// </summary>
/// <remarks>
/// The weight of a term is an approximation of its inverse document frequency, <c>16 * log2((2N + 2) / (2df + 1))</c>, in sixteenths
/// of a bit; the ratio is BM25's <c>1 + (N - df + 0.5) / (df + 0.5)</c> and so never below one, and the weight never negative. It is
/// defined by its integer computation: the ratio is taken to 16 fractional bits by integer division, split into a power of two and a
/// 16-bit mantissa, and the weight is sixteen times the exponent plus the largest step <c>i</c> whose threshold
/// <c>ceil(65536 * 2^(i/16))</c> the mantissa reaches. That is <c>floor(16 * log2(ratio))</c> except where the ratio lies within one unit of
/// the sixteenth bit above a step boundary, where the floored mantissa falls short of the rounded-up threshold and the weight is one step
/// lower (for example 150 documents with 111 of them containing the term weigh 6, where the exact floor is 7). The weight is therefore
/// never above the exact floor and never more than one step below it, and it is the same on every platform. Changing any part of this
/// arithmetic changes what a recipe means, so <see cref="Version"/> moves with it. A term's saturating frequency component is
/// <c>tf * (k1 + 1) / (tf + k1 * (1 - b + b * length / pivot))</c> with <c>k1 = 6/5</c>, <c>b = 3/4</c> and a fixed pivot of
/// <see cref="PivotTokens"/> tokens, so no corpus average enters. A document's score is the sum over the query's terms of weight
/// times that component, in the same units. The candidate score a lane records is its negation: lower is better, as it always was.
/// </remarks>
public static class LongRestLexicalScorer
{
    /// <summary>The fixed document length at which length normalisation is neutral.</summary>
    public const int PivotTokens = 32;

    /// <summary>
    /// The version of the scoring function: the tokenisation, the weight arithmetic and the frequency component, with their constants. The
    /// recipe binds it, so a generation built under one version is never continued under another, and a test pins a digest of the
    /// function's outputs to it: changing the function without moving this number fails that test.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// The FTS5 <c>MATCH</c> expression for one generation's lexical hits: a column filter naming the generation, so that which rows match
    /// is a fact of that generation alone, and a column filter over the content that matches any of the terms, each one quoted as a
    /// phrase. The terms are the letter/digit runs <see cref="QueryTerms"/> yields, so nothing in one can be an FTS5 operator or a quote;
    /// a term that is not exactly letters and digits is refused rather than escaped.
    /// </summary>
    public static string MatchExpression(Guid generation, ImmutableArray<string> terms)
    {
        if (generation == Guid.Empty || terms.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A lexical match requires a generation and at least one term.", nameof(terms));
        }

        foreach (string term in terms)
        {
            if (string.IsNullOrEmpty(term))
            {
                throw new ArgumentException("A lexical match term is a non-empty run of letters and digits.", nameof(terms));
            }

            foreach (Rune rune in term.EnumerateRunes())
            {
                if (!Rune.IsLetterOrDigit(rune))
                {
                    throw new ArgumentException("A lexical match term is a non-empty run of letters and digits.", nameof(terms));
                }
            }
        }

        return "GenerationId : \"" + generation.ToString("D").ToUpperInvariant() + "\" AND Content : ("
            + string.Join(" OR ", terms.Select(static term => "\"" + term + "\"")) + ")";
    }

    /// <summary>
    /// The distinct, lower-cased letter/digit tokens of <paramref name="content"/> that can be queried: first occurrence order, at
    /// most <see cref="LongRestDiscoveryLimits.LexicalTokens"/> of them, each at most
    /// <see cref="LongRestDiscoveryLimits.LexicalTokenUtf8Bytes"/> UTF-8 bytes. A longer token is not a term and does not count
    /// toward the limit.
    /// </summary>
    public static ImmutableArray<string> QueryTerms(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        List<string> terms = [];

        StringBuilder token = new();

        foreach (Rune rune in content.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                token.Append(Rune.ToLowerInvariant(rune));

                continue;
            }

            AddTerm(terms, token);

            if (terms.Count == LongRestDiscoveryLimits.LexicalTokens)
            {
                return [.. terms];
            }
        }

        AddTerm(terms, token);

        return [.. terms];
    }

    /// <summary>
    /// The document's length in tokens (every letter/digit run, whatever its size) and the occurrences of each of
    /// <paramref name="terms"/> among them, compared after the same lower-casing the query terms received.
    /// </summary>
    public static LongRestLexicalDocument Profile(string content, ImmutableArray<string> terms)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (terms.IsDefault)
        {
            throw new ArgumentException("A lexical profile requires its query terms.", nameof(terms));
        }

        int[] frequencies = new int[terms.Length];

        int length = 0;

        StringBuilder token = new();

        foreach (Rune rune in content.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                token.Append(Rune.ToLowerInvariant(rune));

                continue;
            }

            Count();
        }

        Count();

        return new(length, [.. frequencies]);

        void Count()
        {
            if (token.Length == 0)
            {
                return;
            }

            string text = token.ToString();

            token.Clear();

            length++;

            for (int index = 0; index < terms.Length; index++)
            {
                if (string.Equals(terms[index], text, StringComparison.Ordinal))
                {
                    frequencies[index]++;

                    return;
                }
            }
        }
    }

    /// <summary>
    /// The weight of each term in a generation of <paramref name="documents"/> leaves, given the number of those leaves that contain
    /// it. A term in every document weighs about nothing; a term in few weighs a lot.
    /// </summary>
    public static ImmutableArray<int> Weights(long documents, ImmutableArray<int> documentFrequencies)
    {
        if (documentFrequencies.IsDefault)
        {
            throw new ArgumentException("Term weights require document frequencies.", nameof(documentFrequencies));
        }

        int[] weights = new int[documentFrequencies.Length];

        for (int index = 0; index < weights.Length; index++)
        {
            weights[index] = Weight(documents, documentFrequencies[index]);
        }

        return [.. weights];
    }

    /// <summary>The non-negative relevance of <paramref name="document"/> for terms weighing <paramref name="weights"/>.</summary>
    public static long Score(ImmutableArray<int> weights, LongRestLexicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (weights.IsDefault || document.Frequencies.IsDefault || weights.Length != document.Frequencies.Length || document.Length < 0)
        {
            throw new ArgumentException("A lexical score requires one weight and one frequency per term.", nameof(document));
        }

        long lengthFactor = 256 + 768L * document.Length / PivotTokens;

        long total = 0;

        for (int index = 0; index < weights.Length; index++)
        {
            long frequency = document.Frequencies[index];

            if (frequency > 0)
            {
                total += weights[index] * (frequency * 11 * 1024 * 1024 / (5120 * frequency + 6 * lengthFactor));
            }
        }

        return total / 1024;
    }

    private static void AddTerm(List<string> terms, StringBuilder token)
    {
        if (token.Length == 0)
        {
            return;
        }

        string text = token.ToString();

        token.Clear();

        if (Encoding.UTF8.GetByteCount(text) > LongRestDiscoveryLimits.LexicalTokenUtf8Bytes)
        {
            return;
        }

        for (int index = 0; index < terms.Count; index++)
        {
            if (string.Equals(terms[index], text, StringComparison.Ordinal))
            {
                return;
            }
        }

        terms.Add(text);
    }

    private static int Weight(long documents, int frequency)
    {
        if (documents is < 1 or > int.MaxValue || frequency < 0)
        {
            return 0;
        }

        long numerator = 2 * documents + 2;

        long denominator = 2L * frequency + 1;

        if (numerator < denominator)
        {
            return 0;
        }

        long quotient = (numerator << 16) / denominator;

        int exponent = -16;

        for (long rest = quotient; rest > 1; rest >>= 1)
        {
            exponent++;
        }

        long mantissa = quotient >> exponent;

        int fraction = 15;

        while (fraction > 0 && mantissa < Threshold(fraction))
        {
            fraction--;
        }

        return 16 * exponent + fraction;
    }

    // ceil(65536 * 2^(index / 16)), the smallest mantissa whose logarithm reaches index sixteenths of a bit.
    private static int Threshold(int index) => index switch
    {
        0 => 65536,
        1 => 68438,
        2 => 71468,
        3 => 74632,
        4 => 77936,
        5 => 81387,
        6 => 84990,
        7 => 88753,
        8 => 92682,
        9 => 96786,
        10 => 101071,
        11 => 105546,
        12 => 110218,
        13 => 115098,
        14 => 120194,
        _ => 125515,
    };
}
