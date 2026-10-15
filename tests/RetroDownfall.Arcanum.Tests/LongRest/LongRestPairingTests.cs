using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// The Pairing Gate's arithmetic. Every expected number in this file was produced by an independent script (plain Python floats, math.ceil and
// int.bit_length written from the specification), not by this code.
public sealed class LongRestPairingTests
{
    // median, spread (sigma, before the clamp), other leaves -> floor in thousandths
    [Theory]
    [InlineData(0.5, 0.05, 64, 775)] // the closeness clause binds: 0.5 + 0.55 * 0.5
    [InlineData(0.3, 0.12, 63, 720)] // Z = 3.5 below sixty-four leaves
    [InlineData(0.3, 0.12, 64, 744)] // Z = 3.7 from sixty-four
    [InlineData(0.3, 0.12, 127, 744)]
    [InlineData(0.3, 0.12, 128, 768)] // Z = 3.9 from a hundred and twenty-eight
    [InlineData(0.3, 0.12, 255, 768)]
    [InlineData(0.3, 0.12, 256, 792)] // Z = 4.1 from two hundred and fifty-six
    [InlineData(0.3, 0.12, 16, 720)] // the fewest leaves that have a distribution of their own
    [InlineData(0.3, 0.12, 15, 920)] // one fewer: the fallback
    [InlineData(0.9, 0.001, 64, 974)] // a sample that looks degenerate cannot narrow the floor: sigma is held at 0.02
    [InlineData(0.9, 0.02, 64, 974)]
    [InlineData(-0.5, 0.01, 64, 500)] // never below one half
    [InlineData(0.99, 0.05, 64, 1000)] // never above one
    [InlineData(0.284, 0.05, 100, 678)] // a model whose unrelated memories sit near 0.28
    [InlineData(0.5, 0.08, 35, 780)]
    [InlineData(0.0, 0.0, 500, 550)] // no spread at all: the closeness clause alone
    public void The_background_floor_is_the_larger_of_the_chance_and_the_closeness_clauses_rounded_up_to_the_thousandth(double median, double spread, int leaves, int expected) =>
        Assert.Equal(expected, LongRestPairing.BackgroundFloor(median, spread, leaves));

    [Theory]
    [InlineData(double.NaN, 0.05)]
    [InlineData(0.5, double.NaN)]
    [InlineData(double.PositiveInfinity, 0.05)]
    [InlineData(0.5, double.NegativeInfinity)]
    public void A_distribution_that_is_not_a_number_gives_the_highest_floor_and_a_small_generation_the_fallback_whatever_it_says(double median, double spread)
    {
        Assert.Equal(1000, LongRestPairing.BackgroundFloor(median, spread, 64));

        Assert.Equal(LongRestDiscoveryLimits.PairingFallbackPermille, LongRestPairing.BackgroundFloor(median, spread, 15));

        Assert.Equal(LongRestDiscoveryLimits.PairingFallbackPermille, LongRestPairing.BackgroundFloor(0.5, 0.05, 0));

        Assert.Equal(LongRestDiscoveryLimits.PairingFallbackPermille, LongRestPairing.BackgroundFloor(0.5, 0.05, -3));
    }

    [Fact]
    public void The_floor_never_leaves_its_bounds_whatever_the_distribution_is()
    {
        Random random = new(15102026);

        for (int round = 0; round < 5000; round++)
        {
            int floor = LongRestPairing.BackgroundFloor(random.NextDouble() * 4 - 2, random.NextDouble() * 2, random.Next(0, 5000));

            Assert.InRange(floor, LongRestDiscoveryLimits.PairingAbsoluteMinimumPermille, 1000);
        }
    }

    // A positive affine map of every similarity that fixes a perfect match (the common-mode change an anisotropic or differently trained model
    // makes to first order) moves the floor with it, so the same constants draw the same line between the same memories.
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.8)]
    [InlineData(0.5)]
    [InlineData(0.3)]
    public void A_positive_affine_change_of_the_similarity_scale_leaves_every_paired_verdict_unchanged(double scale)
    {
        Random random = new(7 + (int)(scale * 100));

        int disagreements = 0;

        int judged = 0;

        for (int round = 0; round < 400; round++)
        {
            double median = random.NextDouble() * 0.9;

            // The spread is held at 0.02 whatever it measures, and that fixed floor does not scale, so the map is judged where the spread is above it both before and after.
            double spread = 0.02 / scale + random.NextDouble() * 0.1;

            int leaves = random.Next(16, 400);

            double floor = LongRestPairing.BackgroundFloor(median, spread, leaves) / 1000.0;

            // c' = 1 - scale * (1 - c): every similarity is pulled toward (or, for scale < 1, pushed away from) the match at one by the same factor.
            double mapped = LongRestPairing.BackgroundFloor(1 - scale * (1 - median), scale * spread, leaves) / 1000.0;

            for (int probe = 0; probe < 30; probe++)
            {
                double cosine = random.NextDouble() * 2 - 1;

                double image = 1 - scale * (1 - cosine);

                // The floor is rounded to the thousandth and the two clauses clamp it, so a probe within a thousandth of the line may fall either side.
                if (Math.Abs(cosine - floor) < 0.004 || floor <= 0.5 || floor >= 1.0 || mapped <= 0.5 || mapped >= 1.0)
                {
                    continue;
                }

                judged++;

                if ((cosine >= floor) != (image >= mapped))
                {
                    disagreements++;
                }
            }
        }

        Assert.True(judged > 1000, $"judged {judged}");

        Assert.Equal(0, disagreements);
    }

    [Fact]
    public void The_histogram_reads_quantiles_from_the_multiset_of_values_alone()
    {
        LongRestCosineHistogram forward = new();

        LongRestCosineHistogram shuffled = new();

        double[] values = [.. Enumerable.Range(0, 200).Select(index => ((index * 37) % 2001 - 1000) / 1000.0)];

        foreach (double value in values) forward.Add(value);

        foreach (double value in values.OrderBy(static value => Math.Sin(value * 1000 + 3))) shuffled.Add(value);

        Assert.Equal(200, forward.Count);

        Assert.Equal(forward.Median, shuffled.Median);

        Assert.Equal(forward.FirstQuartile, shuffled.FirstQuartile);

        Assert.Equal(forward.ThirdQuartile, shuffled.ThirdQuartile);
    }

    [Fact]
    public void A_quantile_is_the_upper_edge_of_the_bin_that_holds_the_ranked_value()
    {
        LongRestCosineHistogram histogram = new();

        Assert.True(double.IsNaN(histogram.Median));

        histogram.Add(0.0);

        // 0.0 is in the bin [0, 1/512); the edge is its top.
        Assert.Equal(1.0 / 512, histogram.Median);

        histogram.Add(double.NaN);

        histogram.Add(double.PositiveInfinity);

        Assert.Equal(1, histogram.Count);

        histogram.Add(1.0);

        histogram.Add(1.0000000000000002);

        histogram.Add(-1.0);

        histogram.Add(-7.0);

        Assert.Equal(5, histogram.Count);

        // [-1, -1, 0, 1, 1] : the median is the third value, the quartiles the second and the fourth.
        Assert.Equal(1.0 / 512, histogram.Median);

        Assert.Equal(-1.0 + 1.0 / 512, histogram.FirstQuartile);

        Assert.Equal(1.0, histogram.ThirdQuartile);

        Assert.Equal(1.0, histogram.Nth(5));

        Assert.True(double.IsNaN(histogram.Nth(0)));

        Assert.True(double.IsNaN(histogram.Nth(6)));
    }

    // Written by an independent script: 'f' rows are the floor for a grid of medians, spreads and generation sizes, 'q' rows the median and
    // quartiles (as 1-based bin numbers) of sequences of values. The version of this arithmetic is the recipe's PairingRecipeVersion: this
    // digest and that version move together, so changing a constant or a clause without moving the version fails here.
    [Fact]
    public void The_pairing_recipe_version_pins_a_digest_of_the_arithmetics_outputs()
    {
        Assert.Equal(1, LongRestPairing.Version);

        Assert.Equal(LongRestDiscoveryLimits.PairingRecipeVersion, LongRestPairing.Version);

        StringBuilder table = new();

        foreach (int leaves in new[] { 15, 16, 17, 63, 64, 100, 127, 128, 255, 256, 1000 })
        {
            foreach (int median in new[] { -500, 0, 300, 500, 700, 900, 990 })
            {
                foreach (int spread in new[] { 0, 10, 20, 50, 100, 120 })
                {
                    table.Append(CultureInfo.InvariantCulture, $"f {median} {spread} {leaves} {LongRestPairing.BackgroundFloor(median / 1000.0, spread / 1000.0, leaves)}\n");
                }
            }
        }

        table.Append(Quantiles(Enumerable.Range(0, 200).Select(index => ((index * 37) % 2001 - 1000) / 1000.0)));

        foreach (int count in new[] { 1, 2, 3, 4, 5, 16, 17, 64, 65 })
        {
            table.Append(Quantiles(Enumerable.Range(0, count).Select(index => ((index * 53 + 7) % 1999 - 999) / 1000.0)));
        }

        Assert.Equal("8303EBAC979C37F121E9D9A3F58861A7F300A00CBFEBCD36493718CFFFEFA535", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(table.ToString()))));

        static string Quantiles(IEnumerable<double> values)
        {
            LongRestCosineHistogram histogram = new();

            foreach (double value in values) histogram.Add(value);

            static long Bin(double edge) => (long)((edge + 1.0) * 512.0);

            return string.Create(CultureInfo.InvariantCulture, $"q {histogram.Count} {Bin(histogram.Median)} {Bin(histogram.FirstQuartile)} {Bin(histogram.ThirdQuartile)}\n");
        }
    }

    [Fact]
    public void The_recipe_carries_the_pairing_constants_this_arithmetic_uses()
    {
        LongRestRecipeIdentity recipe = new(1, 1, "provider", "model", LongRestPackingData.Digest, "provider", "embedding", LongRestPackingData.Digest, 64);

        Assert.Equal(LongRestPairing.Version, recipe.PairingRecipeVersion);

        Assert.Equal(550, recipe.PairingClosenessPermille);

        Assert.Equal(35, recipe.PairingSpreadTenths);

        Assert.Equal(2, recipe.PairingSpreadStepTenths);

        Assert.Equal(20, recipe.PairingSpreadMinimumThousandths);

        Assert.Equal(64, recipe.PairingSampleLeaves);

        Assert.Equal(16, recipe.PairingMinimumLeaves);

        Assert.Equal(920, recipe.PairingFallbackPermille);

        Assert.Equal(500, recipe.PairingAbsoluteMinimumPermille);

        Assert.Equal(1024, recipe.PairingHistogramBins);

        Assert.Equal(15, recipe.PairFallbackLimit);

        Assert.Equal(200, recipe.LexicalMinimumCoveragePermille);

        Assert.Equal(920, recipe.SummaryMinimumCosinePermille);
    }

    private static ImmutableArray<float> Draw(Random random, int dimensions, double[]? axis = null, double spread = 1.0) =>
        [.. Enumerable.Range(0, dimensions).Select(index => (float)((axis?[index] ?? 0) + (random.NextDouble() * 2 - 1) * spread))];

    private static LongRestBackgroundSampler Observe(ImmutableArray<float> anchor, IReadOnlyList<ImmutableArray<float>> others)
    {
        LongRestBackgroundSampler sampler = new(anchor);

        foreach (ImmutableArray<float> other in others) _ = sampler.Observe(false, other);

        return sampler;
    }

    [Fact]
    public void The_sampler_reads_the_same_floor_the_plain_reference_computes_over_random_generations()
    {
        Random random = new(20261015);

        for (int round = 0; round < 40; round++)
        {
            int dimensions = random.Next(3, 40);

            int count = random.Next(2, 220);

            double[] axis = [.. Enumerable.Range(0, dimensions).Select(_ => random.NextDouble() * 2 - 1)];

            double strength = random.NextDouble() * 3;

            // Leaves in memory id order; the anchor is one of them, and a few have another width, no vector or a vector that cannot be judged.
            List<ImmutableArray<float>> leaves = [.. Enumerable.Range(0, count).Select(index => index % 23 == 22
                ? ImmutableArray.Create(1f, 2f)
                : Draw(random, dimensions, axis.Select(value => value * strength).ToArray()))];

            if (count > 5) leaves[3] = default;

            int anchor = random.Next(count);

            if (leaves[anchor].IsDefaultOrEmpty || leaves[anchor].Length != dimensions) continue;

            LongRestBackgroundSampler sampler = new(leaves[anchor]);

            for (int index = 0; index < leaves.Count; index++) _ = sampler.Observe(index == anchor, leaves[index]);

            int expected = LongRestPairingReference.Floor([.. leaves.Select(static leaf => leaf.IsDefaultOrEmpty ? [] : leaf.ToArray())], anchor);

            Assert.Equal(expected, sampler.FloorPermille());
        }
    }

    [Fact]
    public void The_observation_of_a_leaf_returns_its_cosine_to_the_anchor_and_nothing_for_the_anchor_or_a_leaf_it_cannot_judge()
    {
        LongRestBackgroundSampler sampler = new([1f, 0f]);

        Assert.True(double.IsNaN(sampler.Observe(true, [1f, 0f])));

        Assert.Equal(0.6, sampler.Observe(false, [0.6f, 0.8f]), 6);

        Assert.True(double.IsNaN(sampler.Observe(false, [1f, 0f, 0f])));

        Assert.True(double.IsNaN(sampler.Observe(false, [float.NaN, 0f])));

        Assert.True(double.IsNaN(sampler.Observe(false, [0f, 0f])));

        Assert.True(double.IsNaN(sampler.Observe(false, default)));

        Assert.Equal(1, sampler.Others);
    }

    [Fact]
    public void Identical_vectors_have_no_distribution_to_trust_and_fail_closed_to_the_highest_floor()
    {
        ImmutableArray<float> same = [0.3f, 0.4f, 0.5f, 0.6f];

        LongRestBackgroundSampler sampler = Observe(same, [.. Enumerable.Repeat(same, 40)]);

        Assert.Equal(1000, sampler.FloorPermille());
    }

    [Fact]
    public void Unrelated_random_vectors_set_a_floor_no_unrelated_pair_clears()
    {
        Random random = new(99);

        List<ImmutableArray<float>> leaves = [.. Enumerable.Range(0, 120).Select(_ => Draw(random, 64))];

        LongRestBackgroundSampler sampler = Observe(leaves[0], leaves.Skip(1).ToList());

        int floor = sampler.FloorPermille();

        Assert.InRange(floor, 550, 800);

        LongRestPairingVectors vectors = new();

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, "a", null, leaves[0]);

        for (int index = 1; index < leaves.Count; index++)
        {
            Assert.False(vectors.Paired(anchor, LongRestPackingData.Leaf(index, "n" + index, null, leaves[index]), floor), "an unrelated random vector was paired");
        }
    }

    [Fact]
    public void Vectors_of_another_width_and_vectors_that_cannot_be_judged_take_no_part_in_the_floor()
    {
        Random random = new(5);

        ImmutableArray<float> anchor = Draw(random, 8);

        List<ImmutableArray<float>> usable = [.. Enumerable.Range(0, 30).Select(_ => Draw(random, 8))];

        LongRestBackgroundSampler plain = Observe(anchor, usable);

        List<ImmutableArray<float>> noisy = [.. usable, Draw(random, 5), Draw(random, 12), default, [float.NaN, 1f, 1f, 1f, 1f, 1f, 1f, 1f], [0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f]];

        LongRestBackgroundSampler withNoise = Observe(anchor, noisy);

        Assert.Equal(plain.Others, withNoise.Others);

        Assert.Equal(plain.FloorPermille(), withNoise.FloorPermille());

        // Fewer than sixteen leaves that can be judged: the fallback, whatever else is in the generation.
        Assert.Equal(LongRestDiscoveryLimits.PairingFallbackPermille, Observe(anchor, [.. usable.Take(15), .. Enumerable.Range(0, 50).Select(_ => Draw(random, 5))]).FloorPermille());

        Assert.Equal(1000, Observe(default, usable).FloorPermille());

        Assert.Equal(1000, Observe([0f, 0f], usable).FloorPermille());
    }

    [Fact]
    public void Paired_means_identical_content_or_a_cosine_at_or_above_the_floor_and_a_leaf_that_cannot_be_judged_is_not_paired()
    {
        string same = "AB".PadLeft(64, '0');

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, "anchor", 0, same);

        Assert.True(LongRestPairing.Paired(anchor, LongRestPackingData.Leaf(1, "other", same), 1000));

        Assert.True(LongRestPairing.Paired(anchor, LongRestPackingData.Leaf(2, "other", 0, same.ToLowerInvariant()), 1000));

        Assert.False(LongRestPairing.Paired(anchor, LongRestPackingData.Leaf(3, "far", 90), 500));

        Assert.True(LongRestPairing.Paired(anchor, LongRestPackingData.Leaf(4, "near", 5), 990));

        Assert.False(LongRestPairing.Paired(anchor, LongRestPackingData.Leaf(5, "bare"), 500));

        Assert.False(LongRestPairing.Paired(anchor, LongRestPackingData.Leaf(6, "wide", vector: [1f, 0f, 0f]), 500));

        Assert.False(LongRestPairing.Paired(anchor with { Vector = default }, LongRestPackingData.Leaf(7, "near", 5), 500));
    }

    [Fact]
    public void A_pack_is_cohesive_for_a_candidate_paired_with_every_member_after_the_anchor_and_a_member_of_identical_content_is_exempt()
    {
        string same = "CD".PadLeft(64, '0');

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, "anchor", 0);

        LongRestSourceLeaf member = LongRestPackingData.Leaf(1, "member", 18);

        LongRestSourceLeaf near = LongRestPackingData.Leaf(2, "near", 20);

        LongRestSourceLeaf opposite = LongRestPackingData.Leaf(3, "opposite", -18);

        Assert.True(LongRestPairing.Cohesive([anchor, member], near, 920));

        Assert.False(LongRestPairing.Cohesive([anchor, member], opposite, 920));

        // Identical content is exempt: the candidate is the same memory as the member whatever its vector says.
        Assert.True(LongRestPairing.Cohesive([anchor, LongRestPackingData.Leaf(4, "twin", 18, same)], LongRestPackingData.Leaf(5, "twin", -18, same), 920));

        // The anchor itself is not tested here: the packer has already paired the candidate with it.
        Assert.True(LongRestPairing.Cohesive([anchor], opposite, 1000));
    }

    [Theory]
    [InlineData("The standup is at 9:30.", "A standup happens at 9:30 daily.", true)]
    [InlineData("The standup is at 9:30.", "The standup is at 10:00.", false)]
    [InlineData("The user wears size 43 shoes.", "The user wears size 44 shoes.", false)]
    [InlineData("The server listens on port 9090.", "The server listens on port 3000.", false)]
    [InlineData("A trip to Lisbon in March 2027.", "The user visits Lisbon in March 2027.", true)]
    [InlineData("A trip to Lisbon in March 2027.", "The user visits Lisbon in March 2028.", false)]
    [InlineData("The user writes .NET 10 in C#.", "The user's stack is C# on .NET 10.", true)]
    [InlineData("The user writes .NET 10 in C#.", "The user's stack is C# on .NET 9.", false)]
    [InlineData("The user's phone is an S25.", "The user's phone is an s25 model.", true)]
    [InlineData("The user's phone is an S25.", "The user's phone is an S24.", false)]
    [InlineData("A 4K monitor.", "A monitor of 4K resolution.", true)]
    [InlineData("A 4K monitor.", "A 8K monitor.", false)]
    [InlineData("The dog is a beagle.", "The user owns a beagle.", true)]
    [InlineData("The dog is a beagle.", "The dog is a beagle aged 3.", false)]
    [InlineData("Room 101 and 202.", "Room 101 and 203.", false)]
    [InlineData("Rooms 202 and 101.", "Room 202 and 101 are booked.", true)]
    public void Number_Agreement_requires_the_same_tokens_that_contain_a_digit_in_every_memory(string left, string right, bool agree)
    {
        Assert.Equal(agree, LongRestPairing.NumbersAgree([left, right]));

        Assert.Equal(agree, LongRestPairing.NumbersAgree([right, left]));

        Assert.Equal(agree, LongRestPairing.NumbersAgree(ImmutableArray.Create(LongRestPackingData.Leaf(0, left), LongRestPackingData.Leaf(1, right))));
    }

    [Fact]
    public void Number_Agreement_reads_the_tokens_the_lexical_scorer_reads_and_judges_the_whole_set()
    {
        Assert.Equal(["9", "30"], LongRestPairing.NumberTokens("Standup at 9:30 AM"));

        Assert.Equal(["10"], LongRestPairing.NumberTokens(".NET 10"));

        Assert.Equal(["s25", "4k", "2027"], LongRestPairing.NumberTokens("S25, 4K and 2027 twice 2027"));

        Assert.Empty(LongRestPairing.NumberTokens("no figures here"));

        Assert.True(LongRestPairing.NumbersAgree(["no figures", "none either"]));

        // Every member must agree with the first, not just the first two.
        Assert.False(LongRestPairing.NumbersAgree(["port 80", "port 80 again", "port 81"]));

        Assert.True(LongRestPairing.NumbersAgree(["port 80", "the port is 80", "80 is the port"]));

        Assert.False(LongRestPairing.NumbersAgree(Array.Empty<string>()));

        Assert.False(LongRestPairing.NumbersAgree(default(ImmutableArray<LongRestSourceLeaf>)));

        Assert.False(LongRestPairing.NumbersAgree(ImmutableArray.Create(LongRestPackingData.Leaf(0, "a") with { Content = null }, LongRestPackingData.Leaf(1, "a"))));
    }

    [Fact]
    public void The_fold_check_requires_every_pair_to_be_identical_or_to_clear_the_floor_and_an_equivalence_to_keep_its_numbers()
    {
        string same = "EF".PadLeft(64, '0');

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, "port 80 is open", 0);

        LongRestSourceLeaf near = LongRestPackingData.Leaf(1, "the open port is 80", 5);

        LongRestSourceLeaf far = LongRestPackingData.Leaf(2, "port 80 is closed", 60);

        LongRestSourceLeaf changed = LongRestPackingData.Leaf(3, "port 81 is open", 4);

        ImmutableArray<LongRestSourceLeaf> Set(params LongRestSourceLeaf[] leaves) => [.. leaves];

        LongRestAdjudicationKind equivalent = LongRestAdjudicationKind.EquivalentObservations;

        Assert.True(LongRestPairing.FoldCheck(Set(anchor, near), equivalent, 950));

        Assert.False(LongRestPairing.FoldCheck(Set(anchor, far), equivalent, 950));

        // Every pair, not only the pairs with the anchor.
        Assert.False(LongRestPairing.FoldCheck(Set(anchor, near, LongRestPackingData.Leaf(4, "the open port is 80", -5)), equivalent, 990));

        // Numbers: an equivalence refuses a changed value however near it is; a supersession is the replacement of a value and does not.
        Assert.False(LongRestPairing.FoldCheck(Set(anchor, changed), equivalent, 950));

        Assert.True(LongRestPairing.FoldCheck(Set(anchor, changed), LongRestAdjudicationKind.Supersession, 950));

        Assert.False(LongRestPairing.FoldCheck(Set(anchor, far), LongRestAdjudicationKind.Supersession, 950));

        // Byte-identical memories pass whatever their vectors say; a decision that changes nothing always passes.
        Assert.True(LongRestPairing.FoldCheck(Set(LongRestPackingData.Leaf(5, "twin", 0, same), LongRestPackingData.Leaf(6, "twin", 90, same)), equivalent, 1000));

        Assert.True(LongRestPairing.FoldCheck(Set(anchor, far), LongRestAdjudicationKind.NoChange, 950));

        Assert.True(LongRestPairing.FoldCheck(default, LongRestAdjudicationKind.NoChange, 0));

        // A unit with no recorded floor, or a single memory, is never applied.
        Assert.False(LongRestPairing.FoldCheck(Set(anchor, near), equivalent, 0));

        Assert.False(LongRestPairing.FoldCheck(Set(anchor), equivalent, 950));

        Assert.False(LongRestPairing.FoldCheck(default, equivalent, 950));
    }

    // The label of the pairing_floor counter is a closed vocabulary: a floor is reported by the smallest fixed upper edge that is not under it.
    [Theory]
    [InlineData(500, "600")]
    [InlineData(600, "600")]
    [InlineData(601, "700")]
    [InlineData(700, "700")]
    [InlineData(800, "800")]
    [InlineData(801, "900")]
    [InlineData(900, "900")]
    [InlineData(901, "920")]
    [InlineData(920, "920")]
    [InlineData(921, "950")]
    [InlineData(950, "950")]
    [InlineData(951, "980")]
    [InlineData(980, "980")]
    [InlineData(981, "1000")]
    [InlineData(1000, "1000")]
    public void A_floor_is_reported_in_the_smallest_fixed_bucket_that_is_not_under_it(int floor, string bucket)
    {
        Assert.Equal(bucket, LongRestPairing.FloorBucket(floor));
    }
}
