using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// A floor of one thousand is the Pairing Gate failing closed: it admits a memory only when it is byte-identical to the anchor. These tests are the
// collapsed embedding space (every input answered with one direction, as a broken or constant endpoint does) at the widths real models use, where
// the cosine of a vector with itself comes out a little over one, exactly one or a little under one depending on the width and on rounding; none of
// that may decide whether two different memories are put to the model together.
public sealed class LongRestPairingCollapseTests
{
    private static ImmutableArray<float> Collapsed(int width) => width switch
    {
        4 => [0.3f, 0.4f, 0.5f, 0.6f],

        768 => [.. Enumerable.Repeat(0.036f, 768)],

        1024 => [.. Enumerable.Range(0, 1024).Select(index => (float)Math.Sin(index * 0.37))],

        _ => [.. Enumerable.Range(0, width).Select(index => (float)Math.Cos(index * 1.13) * 0.01f)],
    };

    private static LongRestBackgroundSampler Observe(ImmutableArray<float> anchor, IReadOnlyList<ImmutableArray<float>> others)
    {
        LongRestBackgroundSampler sampler = new(anchor);

        _ = sampler.Observe(true, anchor);

        foreach (ImmutableArray<float> other in others) _ = sampler.Observe(false, other);

        return sampler;
    }

    [Theory]
    [InlineData(4)]
    [InlineData(768)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void A_collapsed_generation_sets_the_highest_floor_and_pairs_nothing_but_byte_identical_content(int width)
    {
        ImmutableArray<float> same = Collapsed(width);

        int floor = Observe(same, [.. Enumerable.Repeat(same, 40)]).FloorPermille();

        Assert.Equal(1000, floor);

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, "The user's dog is a beagle.", null, same);

        LongRestSourceLeaf other = LongRestPackingData.Leaf(1, "The project is released under the MIT license.", null, same);

        LongRestSourceLeaf another = LongRestPackingData.Leaf(2, "The user flies to Lisbon in March.", null, same);

        LongRestSourceLeaf twin = LongRestPackingData.Leaf(3, "The user's dog is a beagle.", LongRestPackingData.Hash, same);

        LongRestPairingVectors vectors = new();

        Assert.False(vectors.Paired(anchor, other, floor));

        Assert.False(vectors.Paired(other, anchor, floor));

        Assert.False(vectors.Paired(other, another, floor));

        Assert.True(vectors.Paired(anchor, twin, floor));

        // The facade the rest of the code reads it through says the same.
        Assert.False(LongRestPairing.Paired(anchor, other, floor));

        Assert.True(LongRestPairing.Paired(anchor, twin, floor));

        // A pack is a clique of paired memories, so two different memories never share one; a member's byte-identical twin may.
        Assert.False(LongRestPairing.Cohesive([anchor, other], another, floor));

        Assert.True(LongRestPairing.Cohesive([anchor, other], LongRestPackingData.Leaf(4, "The project is released under the MIT license.", other.Snapshot.ContentHash, same), floor));

        // Packing nominees of one direction: the duplicate packs, and nothing else.
        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, [other, another, twin], LongRestDiscoveryLimits.ProviderInputUtf8Bytes, floor).Value;

        Assert.Equal([[anchor.Snapshot.MemoryId, twin.Snapshot.MemoryId]], packing.Packs.Select(static pack => pack.Select(static leaf => leaf.Snapshot.MemoryId).ToArray()).ToArray());

        Assert.Equal(2, packing.SkippedUnpaired);

        Assert.Equal(0, packing.SkippedIncohesive);

        // The Fold Check refuses every application to different memories and lets a byte-identical pair through.
        Assert.False(LongRestPairing.FoldCheck([anchor, other], LongRestAdjudicationKind.EquivalentObservations, floor));

        Assert.False(LongRestPairing.FoldCheck([anchor, other], LongRestAdjudicationKind.Supersession, floor));

        Assert.False(LongRestPairing.FoldCheck([anchor, other, another], LongRestAdjudicationKind.EquivalentObservations, floor));

        Assert.True(LongRestPairing.FoldCheck([anchor, twin], LongRestAdjudicationKind.EquivalentObservations, floor));
    }

    // However few memories the generation has: a handful of different memories that all sit on the anchor's own point are evidence enough that the
    // space has collapsed, and the fallback floor of a small generation, which is only a guess about a sane model, is not applied to it.
    [Theory]
    [InlineData(4, 1)]
    [InlineData(4, 2)]
    [InlineData(4, 3)]
    [InlineData(768, 8)]
    [InlineData(768, 15)]
    [InlineData(1024, 15)]
    [InlineData(4096, 15)]
    [InlineData(4, 16)]
    [InlineData(768, 17)]
    [InlineData(1024, 40)]
    [InlineData(4096, 40)]
    public void A_collapsed_generation_fails_closed_whether_or_not_it_is_large_enough_to_have_a_distribution(int width, int others)
    {
        ImmutableArray<float> same = Collapsed(width);

        Assert.Equal(1000, Observe(same, [.. Enumerable.Repeat(same, others)]).FloorPermille());
    }

    // Not every small generation is collapsed: a few near-identical memories among others that are far from them are a cluster, and it keeps the
    // fallback floor, so the cluster can still be put to the model.
    [Fact]
    public void A_cluster_among_unrelated_memories_is_not_a_collapsed_generation_and_keeps_the_fallback_floor()
    {
        ImmutableArray<float> anchor = [1f, 0f, 0f, 0f, 0f];

        ImmutableArray<float> variant = [1f, 0.01f, 0f, 0f, 0f];

        ImmutableArray<float>[] others =
        [
            variant, variant, variant, variant,
            [0f, 0f, 1f, 0f, 0f],
            [0f, 0f, 0f, 1f, 0f],
            [0f, 0f, 0f, 0f, 1f],
        ];

        Assert.Equal(LongRestDiscoveryLimits.PairingFallbackPermille, Observe(anchor, others).FloorPermille());
    }

    // The two halves of the collapse signal: the median of the anchor's cosines is in the top bin (it is one, as a bin's upper edge) and the
    // pairwise cosines of the sample have no spread at all (the quartiles are one edge, so the spread is zero).
    [Theory]
    [InlineData(1.0, 0.0, 1, 1000)]
    [InlineData(1.0, 0.0, 2, 1000)]
    [InlineData(1.0, 0.0, 15, 1000)]
    [InlineData(1.0, 0.0, 16, 1000)]
    [InlineData(1.0, 0.0, 500, 1000)]
    [InlineData(1.0, 0.05, 15, 920)] // the sample has a spread: a cluster, not a point
    [InlineData(0.998046875, 0.0, 15, 920)] // the median is one bin under the top
    [InlineData(0.9, 0.0, 15, 920)]
    [InlineData(1.0, 0.0, 0, 920)] // no leaf at all: nothing was seen to call collapsed
    public void The_background_floor_of_a_collapsed_generation_is_the_highest_floor_below_the_minimum_leaves_too(double median, double spread, int leaves, int expected) =>
        Assert.Equal(expected, LongRestPairing.BackgroundFloor(median, spread, leaves));

    // A floor of one thousand clears no cosine: the cosine of a vector with itself is a little over, exactly or a little under one, and a pair of
    // different memories is not made paired by which.
    [Theory]
    [InlineData(1.0000000244, 1000, false)]
    [InlineData(1.0, 1000, false)]
    [InlineData(0.99999996, 1000, false)]
    [InlineData(1.0, 999, true)]
    [InlineData(0.9985, 999, false)]
    [InlineData(0.92, 920, true)]
    [InlineData(0.919999, 920, false)]
    [InlineData(double.NaN, 920, false)]
    [InlineData(double.NaN, 1000, false)]
    public void A_cosine_clears_a_floor_only_below_one_thousand_and_at_or_above_it(double cosine, int floor, bool clears) =>
        Assert.Equal(clears, LongRestPairing.Clears(cosine, floor));
}
