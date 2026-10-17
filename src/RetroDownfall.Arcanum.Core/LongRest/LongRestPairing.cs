using System.Collections.Immutable;
using System.Text;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// A fixed histogram of cosines over [-1, 1] in <see cref="LongRestDiscoveryLimits.PairingHistogramBins"/> equal bins, and the quantiles read
/// from it. A quantile is the upper edge of the bin that holds the rank-th smallest value, so it is a function of the multiset of values
/// alone: the order they were added in, and the machine, change nothing. The edges are multiples of a power of two and are exact in a double.
/// </summary>
public sealed class LongRestCosineHistogram
{
    private readonly int[] _bins = new int[LongRestDiscoveryLimits.PairingHistogramBins];

    /// <summary>The values added so far. A value that is not a finite number is not a cosine and is not counted.</summary>
    public int Count { get; private set; }

    public void Add(double cosine)
    {
        if (!double.IsFinite(cosine))
        {
            return;
        }

        double clamped = cosine < -1.0 ? -1.0 : cosine > 1.0 ? 1.0 : cosine;

        int bin = (int)((clamped + 1.0) * (LongRestDiscoveryLimits.PairingHistogramBins / 2.0));

        if (bin >= _bins.Length)
        {
            bin = _bins.Length - 1;
        }

        _bins[bin]++;

        Count++;
    }

    /// <summary>The upper edge of the bin holding the <paramref name="rank"/>th smallest value (counting from one); NaN when there is no such value.</summary>
    public double Nth(int rank)
    {
        if (rank < 1 || rank > Count)
        {
            return double.NaN;
        }

        int seen = 0;

        for (int bin = 0; bin < _bins.Length; bin++)
        {
            seen += _bins[bin];

            if (seen >= rank)
            {
                return -1.0 + (bin + 1.0) / (_bins.Length / 2.0);
            }
        }

        return double.NaN;
    }

    /// <summary>The median: the ceil(n / 2)th smallest value.</summary>
    public double Median => Nth((Count + 1) / 2);

    /// <summary>The first quartile: the ceil(n / 4)th smallest value.</summary>
    public double FirstQuartile => Nth((Count + 3) / 4);

    /// <summary>The third quartile: the ceil(3n / 4)th smallest value.</summary>
    public double ThirdQuartile => Nth((3 * Count + 3) / 4);
}

/// <summary>
/// The Pairing Gate: which of the memories the discovery lanes nominate are close enough to an anchor to be put to the model together,
/// and the arithmetic of the Background Floor that decides it. The lanes only nominate; this alone admits. Pure, deterministic, and
/// invariant under a positive affine change of the similarity scale, which is what a differently trained or more anisotropic embedding
/// model does to first order. Every constant is code-owned and bound into the recipe (<see cref="LongRestDiscoveryLimits.PairingRecipeVersion"/>),
/// and a test pins a digest of this arithmetic's outputs to that version.
/// </summary>
/// <remarks>
/// The floor of an anchor is <c>max(median + Z(N) * sigma, median + 0.55 * (1 - median), 0.5)</c>, rounded up to the next thousandth and capped
/// at one, where the median is that of the anchor's cosine to every other leaf of the generation, sigma is the interquartile range of the
/// pairwise cosines of a fixed sample of leaves divided by 1.349 (never below 0.02), and <c>Z(N) = 3.5 + 0.2 * max(0, bitlength(N) - 6)</c>. The
/// first clause keeps a nominee at least as far above the background as chance allows for a corpus of this size; the second keeps it close to the
/// anchor in the scale of the space. A generation of fewer than sixteen other leaves has no distribution of its own and uses 0.92, unless what it
/// has is already collapsed (see <see cref="BackgroundFloor"/>). A floor of one thousand is the gate failing closed: it admits nothing but a
/// byte-identical memory (see <see cref="Clears"/>). Every degenerate input raises the floor to that or leaves a nominee unpaired: the gate loses
/// recall, never precision.
/// </remarks>
public static class LongRestPairing
{
    /// <summary>The version of this arithmetic: the recipe binds it and a test pins a digest of its outputs to it.</summary>
    public const int Version = LongRestDiscoveryLimits.PairingRecipeVersion;

    /// <summary>The interquartile range of a normal distribution in standard deviations.</summary>
    private const double InterquartileSigmas = 1.349;

    /// <summary>The sigma of a sample from its quartiles: the interquartile range over 1.349. NaN when either quartile is.</summary>
    public static double Spread(double firstQuartile, double thirdQuartile) => (thirdQuartile - firstQuartile) / InterquartileSigmas;

    /// <summary>
    /// The Background Floor, in thousandths from 500 to 1000, of an anchor whose median cosine to the <paramref name="leaves"/> other leaves of
    /// its generation is <paramref name="median"/> and whose generation spreads by <paramref name="spread"/> (see <see cref="Spread"/>). A
    /// generation too small to have a distribution uses the fallback; a median or spread that is not a number gives the highest floor.
    /// </summary>
    /// <remarks>
    /// A collapsed generation is judged before its size: when the median is the top edge of the histogram (more than half of the other leaves sit
    /// on the anchor's own direction to within one bin) and the sampled pairwise cosines have no spread at all (their quartiles are one edge), the
    /// space cannot tell the memories apart, and the floor is the highest whatever the count. The fallback of a small generation is a guess about a
    /// sane model and is not applied to a space that has shown it is not one. A generation of more than fifteen other leaves reaches the same floor
    /// through the formula, because a median of one puts both clauses at or above it.
    /// </remarks>
    public static int BackgroundFloor(double median, double spread, int leaves)
    {
        if (leaves > 0 && median >= 1.0 && spread <= 0.0)
        {
            return 1000;
        }

        if (leaves < LongRestDiscoveryLimits.PairingMinimumLeaves)
        {
            return LongRestDiscoveryLimits.PairingFallbackPermille;
        }

        if (!double.IsFinite(median) || !double.IsFinite(spread))
        {
            return 1000;
        }

        double minimumSpread = LongRestDiscoveryLimits.PairingSpreadMinimumThousandths / 1000.0;

        double sigma = spread > minimumSpread ? spread : minimumSpread;

        int doublings = BitLength(leaves) - 6;

        if (doublings < 0)
        {
            doublings = 0;
        }

        double z = (LongRestDiscoveryLimits.PairingSpreadTenths + LongRestDiscoveryLimits.PairingSpreadStepTenths * doublings) / 10.0;

        double chance = median + z * sigma;

        double closeness = median + LongRestDiscoveryLimits.PairingClosenessPermille / 1000.0 * (1.0 - median);

        double floor = chance > closeness ? chance : closeness;

        double minimum = LongRestDiscoveryLimits.PairingAbsoluteMinimumPermille / 1000.0;

        if (floor < minimum)
        {
            floor = minimum;
        }

        double permille = Math.Ceiling(floor * 1000.0);

        return permille >= 1000.0 ? 1000 : (int)permille;
    }

    /// <summary>
    /// The fixed bucket of a floor for the <c>arcanum.long_rest.discovery.pairing_floor</c> counter: the smallest upper edge of 600, 700, 800, 900,
    /// 920, 950, 980 and 1000 thousandths that is not under the floor. A closed vocabulary, so the label is bounded by construction.
    /// </summary>
    public static string FloorBucket(int floorPermille) => floorPermille switch
    {
        <= 600 => "600",
        <= 700 => "700",
        <= 800 => "800",
        <= 900 => "900",
        <= 920 => "920",
        <= 950 => "950",
        <= 980 => "980",
        _ => "1000",
    };

    private static int BitLength(int value)
    {
        int bits = 0;

        for (int rest = value; rest > 0; rest >>= 1)
        {
            bits++;
        }

        return bits;
    }

    /// <summary>True when the two leaves are byte-identical: the same content hash in the same format.</summary>
    public static bool SameContent(LongRestSourceLeaf left, LongRestSourceLeaf right) =>
        left.Snapshot.ContentHashFormat == right.Snapshot.ContentHashFormat
        && string.Equals(left.Snapshot.ContentHash, right.Snapshot.ContentHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="cosine"/> clears a floor of <paramref name="floorPermille"/> thousandths: it is at least the floor, and the floor is
    /// under one thousand. A floor of one thousand is the highest the gate sets, and it means byte-identical content only: the cosine of a vector
    /// with itself comes out a little over one, exactly one or a little under it depending on the width and on rounding, and that must not decide
    /// whether two different memories are put to the model together. A cosine that is not a number clears nothing.
    /// </summary>
    public static bool Clears(double cosine, int floorPermille) => floorPermille < 1000 && cosine >= floorPermille / 1000.0;

    /// <summary>
    /// True when <paramref name="other"/> is paired with <paramref name="anchor"/> at <paramref name="floorPermille"/>: byte-identical, or both
    /// leaves have usable vectors of one width whose cosine is at least the floor. A leaf that cannot be assessed is not paired.
    /// </summary>
    public static bool Paired(LongRestSourceLeaf anchor, LongRestSourceLeaf other, int floorPermille) =>
        new LongRestPairingVectors().Paired(anchor, other, floorPermille);

    /// <summary>
    /// True when <paramref name="candidate"/> may join <paramref name="pack"/> (the anchor first): it is paired, at the anchor's floor, with every
    /// member after the anchor. A member of identical content is exempt.
    /// </summary>
    public static bool Cohesive(IReadOnlyList<LongRestSourceLeaf> pack, LongRestSourceLeaf candidate, int floorPermille) =>
        new LongRestPairingVectors().Cohesive(pack, candidate, floorPermille);

    /// <summary>
    /// The Fold Check: whether a decision of <paramref name="kind"/> may be applied to <paramref name="inputs"/> under the floor the unit recorded.
    /// Every pair of the applied set must be byte-identical or clear the floor, and an equivalence must not join different numbers. A decision that
    /// changes nothing passes. It can only refuse.
    /// </summary>
    public static bool FoldCheck(ImmutableArray<LongRestSourceLeaf> inputs, LongRestAdjudicationKind kind, int floorPermille)
    {
        if (kind == LongRestAdjudicationKind.NoChange)
        {
            return true;
        }

        if (inputs.IsDefaultOrEmpty || inputs.Length < 2 || floorPermille is < LongRestDiscoveryLimits.PairingAbsoluteMinimumPermille or > 1000)
        {
            return false;
        }

        LongRestPairingVectors vectors = new();

        for (int left = 0; left < inputs.Length; left++)
        {
            for (int right = left + 1; right < inputs.Length; right++)
            {
                if (!vectors.Paired(inputs[left], inputs[right], floorPermille))
                {
                    return false;
                }
            }
        }

        return kind != LongRestAdjudicationKind.EquivalentObservations || NumbersAgree(inputs);
    }

    /// <summary>
    /// Number Agreement: the letter/digit tokens that contain a digit, lower-cased by the lexical scorer's tokenisation (<c>9:30</c> gives 9 and
    /// 30; <c>.NET 10</c> gives 10; <c>S25</c>, <c>4K</c> and <c>2027</c> stay whole), are the same set in every memory. A memory without content
    /// disagrees. It never admits anything: it only refuses.
    /// </summary>
    public static bool NumbersAgree(ImmutableArray<LongRestSourceLeaf> inputs)
    {
        if (inputs.IsDefaultOrEmpty)
        {
            return false;
        }

        List<string>? first = null;

        foreach (LongRestSourceLeaf input in inputs)
        {
            if (input.Content is null)
            {
                return false;
            }

            List<string> numbers = NumberTokens(input.Content);

            if (first is null)
            {
                first = numbers;

                continue;
            }

            if (!SameSet(first, numbers))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Number Agreement over the texts of a request, in any order.</summary>
    public static bool NumbersAgree(IReadOnlyList<string> contents)
    {
        List<string>? first = null;

        foreach (string content in contents)
        {
            List<string> numbers = NumberTokens(content);

            if (first is null)
            {
                first = numbers;

                continue;
            }

            if (!SameSet(first, numbers))
            {
                return false;
            }
        }

        return first is not null;
    }

    /// <summary>The distinct lower-cased tokens of <paramref name="content"/> that contain a digit, in first-occurrence order.</summary>
    public static List<string> NumberTokens(string content)
    {
        List<string> tokens = [];

        StringBuilder token = new();

        bool digit = false;

        foreach (Rune rune in content.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                token.Append(Rune.ToLowerInvariant(rune));

                digit |= Rune.IsDigit(rune);

                continue;
            }

            Flush();
        }

        Flush();

        return tokens;

        void Flush()
        {
            if (token.Length == 0)
            {
                return;
            }

            string text = token.ToString();

            token.Clear();

            bool numeric = digit;

            digit = false;

            if (!numeric)
            {
                return;
            }

            for (int index = 0; index < tokens.Count; index++)
            {
                if (string.Equals(tokens[index], text, StringComparison.Ordinal))
                {
                    return;
                }
            }

            tokens.Add(text);
        }
    }

    private static bool SameSet(List<string> left, List<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            bool found = false;

            for (int other = 0; other < right.Count && !found; other++)
            {
                found = string.Equals(left[index], right[other], StringComparison.Ordinal);
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Cosines between the leaves of one packing, computed with the Tapestry's own bit-identical arithmetic
/// (<see cref="SphericalKMeans.NormalizedDirection"/> and <see cref="SphericalKMeans.CosineToDirection"/>). A leaf's direction is normalized once,
/// the first time it is asked for, so a packing of thirty-two neighbours costs thirty-three normalizations, not one per comparison.
/// </summary>
public sealed class LongRestPairingVectors
{
    private readonly Dictionary<string, float[]> _directions = new(StringComparer.Ordinal);

    /// <summary>The cosine between two leaves; NaN when either has no usable vector or the widths differ.</summary>
    public double Cosine(LongRestSourceLeaf left, LongRestSourceLeaf right)
    {
        if (left.Vector.IsDefaultOrEmpty || right.Vector.IsDefaultOrEmpty || left.Vector.Length != right.Vector.Length)
        {
            return double.NaN;
        }

        float[] direction = DirectionOf(left);

        return direction.Length == 0 ? double.NaN : SphericalKMeans.CosineToDirection(direction, right.Vector.AsSpan());
    }

    /// <summary>The unit direction of a leaf's vector, empty when it has none that is usable.</summary>
    public float[] DirectionOf(LongRestSourceLeaf leaf)
    {
        if (_directions.TryGetValue(leaf.Snapshot.MemoryId, out float[]? known))
        {
            return known;
        }

        float[] direction = leaf.Vector.IsDefaultOrEmpty ? [] : SphericalKMeans.NormalizedDirection(leaf.Vector.ToArray());

        _directions[leaf.Snapshot.MemoryId] = direction;

        return direction;
    }

    public bool Paired(LongRestSourceLeaf anchor, LongRestSourceLeaf other, int floorPermille) =>
        LongRestPairing.SameContent(anchor, other) || LongRestPairing.Clears(Cosine(anchor, other), floorPermille);

    public bool Cohesive(IReadOnlyList<LongRestSourceLeaf> pack, LongRestSourceLeaf candidate, int floorPermille)
    {
        for (int index = 1; index < pack.Count; index++)
        {
            if (!Paired(pack[index], candidate, floorPermille))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Observes the leaves of one generation, in memory id order, from one anchor's point of view, and reads the anchor's Background Floor from what
/// it saw: the median of the anchor's cosine to every other leaf with a usable vector of the anchor's width (eligible or not, so consolidation
/// during the sweep never moves it) and the spread of the pairwise cosines of the first
/// <see cref="LongRestDiscoveryLimits.PairingSampleLeaves"/> such leaves, which is at most 2,016 dot products and 64 held directions whatever the
/// anchor. Both are read from fixed-bin histograms, so the floor is a function of the generation's own frozen vectors and nothing else.
/// </summary>
/// <remarks>
/// <para>The spread does not depend on the anchor at all: a leaf joins the sample when its vector is usable and has the anchor's width, which is
/// a fact about the leaf and the width, and the anchor is itself such a leaf. Every anchor of a generation with vectors of one width therefore
/// reads the same spread, and a caller that has read it once may hand it to the sampler of every later anchor of that generation and width
/// (the <c>knownSpread</c> of the constructor): the sampler then builds no sample and computes no pairwise cosine, and reads the same floor, bit for bit.</para>
///
/// <para>The pairs are cosines of unit directions, taken with <see cref="SphericalKMeans.DirectionCosine"/>; the direction of each held leaf was
/// normalized once when the leaf joined the sample, and <see cref="SphericalKMeans.CosineToDirection"/> would normalize the incoming vector
/// again for each held direction to arrive at the same bits.</para>
/// </remarks>
public sealed class LongRestBackgroundSampler
{
    private readonly float[] _anchor;

    private readonly double? _knownSpread;

    private readonly LongRestCosineHistogram _background = new();

    private readonly LongRestCosineHistogram _spread = new();

    private readonly float[][] _sample;

    private int _sampled;

    /// <param name="anchorVector">The anchor's vector.</param>
    /// <param name="knownSpread">
    /// The spread (see <see cref="LongRestPairing.Spread"/>) that an earlier anchor of the same generation and vector width read from the same
    /// frozen leaves, or <see langword="null"/> when no anchor has: the sampler then reads it from the leaves it observes.
    /// </param>
    public LongRestBackgroundSampler(ImmutableArray<float> anchorVector, double? knownSpread = null)
    {
        _anchor = anchorVector.IsDefaultOrEmpty ? [] : SphericalKMeans.NormalizedDirection(anchorVector.ToArray());

        _knownSpread = knownSpread;

        _sample = knownSpread is null ? new float[LongRestDiscoveryLimits.PairingSampleLeaves][] : [];
    }

    /// <summary>The anchor's unit direction, empty when its vector is unusable: nothing can then be paired with it but a byte-identical memory.</summary>
    public float[] AnchorDirection => _anchor;

    /// <summary>The other leaves with a usable vector of the anchor's width seen so far.</summary>
    public int Others => _background.Count;

    /// <summary>
    /// Observes one leaf of the generation. Returns its cosine to the anchor, or NaN when the leaf has no usable vector of the anchor's width
    /// (it then takes no part in the floor) or is the anchor itself.
    /// </summary>
    public double Observe(bool isAnchor, ImmutableArray<float> vector)
    {
        if (_anchor.Length == 0 || vector.IsDefaultOrEmpty || vector.Length != _anchor.Length)
        {
            return double.NaN;
        }

        double cosine = isAnchor ? 1.0 : SphericalKMeans.CosineToDirection(_anchor, vector.AsSpan());

        if (!double.IsFinite(cosine))
        {
            return double.NaN;
        }

        if (!isAnchor)
        {
            _background.Add(cosine);
        }

        if (_sampled < _sample.Length)
        {
            float[] direction = isAnchor ? _anchor : SphericalKMeans.NormalizedDirection(vector.ToArray());

            for (int held = 0; held < _sampled; held++)
            {
                _spread.Add(SphericalKMeans.DirectionCosine(_sample[held], direction));
            }

            _sample[_sampled++] = direction;
        }

        return isAnchor ? double.NaN : cosine;
    }

    /// <summary>The anchor's Background Floor in thousandths (500 to 1000); 1000 when the anchor has no usable vector.</summary>
    public int FloorPermille() => FloorPermille(out _);

    /// <summary>The anchor's Background Floor in thousandths (500 to 1000); 1000 when the anchor has no usable vector.</summary>
    /// <param name="spread">
    /// The spread of the generation's sample that the floor was read from: the one the sampler was given, or the one it read from the leaves it
    /// observed; <see langword="null"/> when the anchor has no usable vector, because it then reads no sample.
    /// </param>
    public int FloorPermille(out double? spread)
    {
        spread = _anchor.Length == 0 ? null : _knownSpread ?? LongRestPairing.Spread(_spread.FirstQuartile, _spread.ThirdQuartile);

        return spread is { } known ? LongRestPairing.BackgroundFloor(_background.Median, known, _background.Count) : 1000;
    }
}
