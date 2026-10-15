using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestGroupPackerTests
{
    private const int Floor = 920;

    private static readonly long Fixed = LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Adjudication);

    private static long Block(LongRestSourceLeaf leaf) => LongRestPromptLayout.AdjudicationMemoryBytes(leaf);

    // A budget that holds the fixed prompt and exactly <blocks> equal blocks of this content size.
    private static int BudgetFor(int blocks, int contentBytes) => checked((int)(Fixed + blocks * Block(LongRestPackingData.Leaf(0, new string('x', contentBytes)))));

    // Every leaf at one angle, so all of them are paired with the anchor and with each other: only size and count decide the packs.
    private static ImmutableArray<LongRestSourceLeaf> Neighbors(int count, int contentBytes, int first = 1) =>
        [.. Enumerable.Range(first, count).Select(index => LongRestPackingData.Leaf(index, new string('y', contentBytes), 0))];

    private static LongRestSourceLeaf Anchor(string content = "anchor content", string? hash = null) => LongRestPackingData.Leaf(0, content, 0, hash);

    private static int[][] Indexes(LongRestGroupPacking packing) => [.. packing.Packs.Select(pack => pack.Select(leaf => checked((int)leaf.InsertionSequence) - 1).ToArray())];

    [Fact]
    public void The_pack_limit_is_the_three_groups_the_merged_neighbors_always_made()
    {
        Assert.Equal(3, LongRestGroupPacker.MaximumPacksPerAnchor);

        Assert.Equal(32, LongRestDiscoveryLimits.MergedNeighbors);

        // Three duplicate packs, three clique packs and the pair units: the work item index (two digits) always has room.
        Assert.True(3 + LongRestGroupPacker.MaximumPacksPerAnchor + LongRestDiscoveryLimits.PairFallbackLimit <= 99);
    }

    [Fact]
    public void Paired_neighbors_that_fit_fill_the_first_pack_to_sixteen_before_a_second_opens()
    {
        LongRestGroupPacking packing = LongRestGroupPacker.Pack(Anchor(), Neighbors(31, 40), LongRestDiscoveryLimits.ProviderInputUtf8Bytes, Floor).Value;

        // Two clique packs of fifteen neighbours and one of one, then the pair units the first cliques earn (the limit is fifteen).
        Assert.Equal([15, 15, 1], packing.Packs.Take(3).Select(pack => pack.Length - 1));

        Assert.Equal(LongRestDiscoveryLimits.PairFallbackLimit, packing.Packs.Length - 3);

        Assert.All(packing.Packs.Skip(3), pack => Assert.Equal(2, pack.Length));

        Assert.Equal(0, packing.SkippedOversize + packing.SkippedPackLimit + packing.SkippedUnpaired + packing.SkippedIncohesive);
    }

    [Fact]
    public void An_oversized_group_splits_into_packs_that_each_start_with_the_anchor_and_keep_the_nearest_first_order()
    {
        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 1500), 0);

        // Five neighbours at growing angles: the nearest comes first, whatever the order they were nominated in.
        ImmutableArray<LongRestSourceLeaf> neighbors = [.. new[] { 5, 3, 1, 4, 2 }.Select(index => LongRestPackingData.Leaf(index, new string('y', 1500), index * 0.5))];

        // The fixed prompt, the anchor and two neighbours fit; a third neighbour does not.
        int budget = BudgetFor(3, 1500);

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, budget, Floor).Value;

        // Cliques only: [anchor, 1, 2], [anchor, 3, 4], [anchor, 5]; the pair units come after them.
        Assert.Equal([[0, 1, 2], [0, 3, 4], [0, 5]], Indexes(packing).Take(3));

        Assert.All(packing.Packs.Take(3), pack => Assert.Equal(anchor.Target, pack[0].Target));

        Assert.All(packing.Packs.Take(3), pack => Assert.True(Fixed + pack.Sum(Block) <= budget));

        Assert.Equal(0, packing.SkippedOversize + packing.SkippedPackLimit);
    }

    [Fact]
    public void An_anchor_keeps_at_most_three_clique_packs_and_counts_the_neighbors_beyond_them()
    {
        // Seven neighbours at two per pack would need four packs.
        LongRestGroupPacking packing = LongRestGroupPacker.Pack(Anchor(new string('x', 1500)), Neighbors(7, 1500), BudgetFor(3, 1500), Floor).Value;

        Assert.Equal(1, packing.SkippedPackLimit);

        Assert.Equal(0, packing.SkippedOversize + packing.SkippedIncohesive + packing.SkippedUnpaired);

        Assert.Equal([[0, 1, 2], [0, 3, 4], [0, 5, 6]], Indexes(packing).Take(3));
    }

    [Fact]
    public void A_neighbor_that_cannot_fit_beside_the_anchor_is_skipped_and_counted_and_the_rest_still_pack()
    {
        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 1500), 0);

        LongRestSourceLeaf huge = LongRestPackingData.Leaf(2, new string('z', 6000), 0);

        ImmutableArray<LongRestSourceLeaf> neighbors = [LongRestPackingData.Leaf(1, new string('y', 1500), 0), huge, LongRestPackingData.Leaf(3, new string('y', 1500), 0)];

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, BudgetFor(3, 1500), Floor).Value;

        Assert.Equal(1, packing.SkippedOversize);

        Assert.Equal(0, packing.SkippedPackLimit);

        Assert.DoesNotContain(packing.Packs.SelectMany(pack => pack), leaf => leaf.Target == huge.Target);

        Assert.Equal([0, 1, 3], Indexes(packing)[0]);
    }

    [Fact]
    public void An_exact_duplicate_of_the_anchor_goes_to_a_duplicate_pack_first_whatever_its_size()
    {
        string same = "1".PadLeft(64, '0');

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 1500), 0, same);

        // Two oversize duplicates, one duplicate that would fit, one oversize stranger and two neighbours that fit.
        ImmutableArray<LongRestSourceLeaf> neighbors =
        [
            LongRestPackingData.Leaf(1, new string('d', 6000), 0, same),
            LongRestPackingData.Leaf(2, new string('y', 1500), 0),
            LongRestPackingData.Leaf(3, new string('s', 6000), 0),
            LongRestPackingData.Leaf(4, new string('d', 6000), 0, same),
            LongRestPackingData.Leaf(5, new string('y', 1500), 0),
            LongRestPackingData.Leaf(6, "small duplicate", 0, same),
        ];

        int budget = BudgetFor(3, 1500);

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, budget, Floor).Value;

        // Exact First: the one duplicate-only pack comes before the clique, and holds all three duplicates, the fitting one included.
        Assert.Equal([0, 1, 4, 6], Indexes(packing)[0]);

        Assert.Equal([0, 2, 5], Indexes(packing)[1]);

        Assert.All(packing.Packs[0].Skip(1), leaf => Assert.Equal(same, leaf.Snapshot.ContentHash));

        // The duplicate pack costs no request, so it is exempt from the budget.
        Assert.True(Fixed + packing.Packs[0].Sum(Block) > budget);

        Assert.True(Fixed + packing.Packs[1].Sum(Block) <= budget);

        Assert.Equal(1, packing.SkippedOversize);

        Assert.Equal(0, packing.SkippedPackLimit);
    }

    [Fact]
    public void Duplicate_packs_cost_no_request_so_they_do_not_count_toward_the_three_clique_packs_an_anchor_keeps()
    {
        string same = "1".PadLeft(64, '0');

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 1500), 0, same);

        // Six neighbours that fit make the three clique packs; the two duplicates are committed locally, so they still have a pack of their own.
        ImmutableArray<LongRestSourceLeaf> neighbors = [.. Neighbors(6, 1500), LongRestPackingData.Leaf(7, new string('d', 6000), 0, same), LongRestPackingData.Leaf(8, new string('d', 6000), 0, same)];

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, BudgetFor(3, 1500), Floor).Value;

        Assert.Equal([[0, 7, 8], [0, 1, 2], [0, 3, 4], [0, 5, 6]], Indexes(packing).Take(4));

        Assert.Equal(0, packing.SkippedPackLimit);

        Assert.Equal(0, packing.SkippedOversize);
    }

    [Fact]
    public void More_duplicates_than_one_pack_holds_make_more_duplicate_packs_and_never_a_skipped_candidate()
    {
        string same = "1".PadLeft(64, '0');

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 1500), 0, same);

        // Thirty-two oversize duplicates fill two packs of fifteen and a third of two.
        ImmutableArray<LongRestSourceLeaf> duplicates = [.. Enumerable.Range(100, 32).Select(index => LongRestPackingData.Leaf(index, new string('d', 6000), 0, same))];

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, duplicates, BudgetFor(3, 1500), Floor).Value;

        Assert.Equal([15, 15, 2], packing.Packs.Select(pack => pack.Length - 1));

        Assert.Equal(0, packing.SkippedPackLimit + packing.SkippedOversize + packing.SkippedUnpaired);
    }

    [Fact]
    public void A_duplicate_needs_no_vector_and_a_duplicate_in_another_hash_format_is_not_a_duplicate()
    {
        string same = "1".PadLeft(64, '0');

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, "anchor", 0, same);

        LongRestSourceLeaf bare = LongRestPackingData.Leaf(1, "bare", same);

        LongRestSourceLeaf other = LongRestPackingData.Leaf(2, "other format", 0, same);

        other = other with { Snapshot = other.Snapshot with { ContentHashFormat = Core.Annals.AnnalContentHashFormat.LexiconStructuredSnapshot } };

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, [bare, other], LongRestDiscoveryLimits.ProviderInputUtf8Bytes, Floor).Value;

        // The vectorless duplicate is in the duplicate pack; the other format is judged by its vector like any neighbour.
        Assert.Equal([[0, 1], [0, 2]], Indexes(packing));
    }

    [Fact]
    public void An_anchor_that_cannot_fit_alone_still_packs_its_exact_duplicates_and_counts_the_rest()
    {
        string same = "1".PadLeft(64, '0');

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 6000), 0, same);

        ImmutableArray<LongRestSourceLeaf> neighbors = [LongRestPackingData.Leaf(1, new string('d', 6000), 0, same), LongRestPackingData.Leaf(2, "stranger", 0)];

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, LongRestPromptLayout.MinimumInputUtf8Bytes, Floor).Value;

        Assert.Equal([[0, 1]], Indexes(packing));

        Assert.Equal(1, packing.SkippedOversize);
    }

    [Fact]
    public void An_anchor_that_cannot_fit_alone_leaves_no_pack_and_every_neighbor_counts_as_skipped()
    {
        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 6000), 0);

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, Neighbors(4, 100), LongRestPromptLayout.MinimumInputUtf8Bytes, Floor).Value;

        Assert.Empty(packing.Packs);

        Assert.Equal(4, packing.SkippedOversize);
    }

    [Fact]
    public void No_neighbors_means_no_pack()
    {
        LongRestGroupPacking packing = LongRestGroupPacker.Pack(Anchor("anchor"), [], LongRestDiscoveryLimits.ProviderInputUtf8Bytes, Floor).Value;

        Assert.Empty(packing.Packs);

        Assert.Equal(0, packing.SkippedOversize + packing.SkippedPackLimit + packing.SkippedUnpaired + packing.SkippedIncohesive);
    }

    [Fact]
    public void A_nominee_below_the_floor_or_without_a_vector_is_unpaired_and_never_sent()
    {
        // 90 degrees is a cosine of zero; the floor is 0.92 (about 23 degrees).
        LongRestSourceLeaf anchor = Anchor();

        LongRestSourceLeaf near = LongRestPackingData.Leaf(1, "near", 10);

        LongRestSourceLeaf far = LongRestPackingData.Leaf(2, "far", 90);

        LongRestSourceLeaf bare = LongRestPackingData.Leaf(3, "no vector");

        LongRestSourceLeaf wide = LongRestPackingData.Leaf(4, "wrong width", vector: [1f, 0f, 0f]);

        LongRestSourceLeaf zero = LongRestPackingData.Leaf(5, "no length", vector: [0f, 0f]);

        LongRestSourceLeaf nan = LongRestPackingData.Leaf(6, "not a number", vector: [float.NaN, 0f]);

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, [far, bare, near, wide, zero, nan], LongRestDiscoveryLimits.ProviderInputUtf8Bytes, Floor).Value;

        Assert.Equal([[0, 1]], Indexes(packing));

        Assert.Equal(5, packing.SkippedUnpaired);

        Assert.Equal(0, packing.SkippedIncohesive);

        // An anchor without a usable vector pairs nothing but a duplicate.
        LongRestSourceLeaf lost = LongRestPackingData.Leaf(0, "anchor");

        Assert.Empty(LongRestGroupPacker.Pack(lost, [near, far], LongRestDiscoveryLimits.ProviderInputUtf8Bytes, Floor).Value.Packs);
    }

    [Fact]
    public void The_floor_is_a_cosine_in_thousandths_and_a_nominee_is_paired_at_or_above_it()
    {
        static double Degrees(double cosine) => Math.Acos(cosine) * 180 / Math.PI;

        LongRestSourceLeaf above = LongRestPackingData.Leaf(1, "above", Degrees(0.9205));

        LongRestSourceLeaf below = LongRestPackingData.Leaf(2, "below", Degrees(0.9195));

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(Anchor(), [below, above], LongRestDiscoveryLimits.ProviderInputUtf8Bytes, 920).Value;

        Assert.Equal([[0, 1]], Indexes(packing));

        Assert.Equal(1, packing.SkippedUnpaired);

        // The same two are both paired one thousandth lower, and neither is at the highest floor.
        Assert.Equal([0, 1, 2], Indexes(LongRestGroupPacker.Pack(Anchor(), [below, above], LongRestDiscoveryLimits.ProviderInputUtf8Bytes, 919).Value)[0]);

        Assert.Empty(LongRestGroupPacker.Pack(Anchor(), [below, above], LongRestDiscoveryLimits.ProviderInputUtf8Bytes, 1000).Value.Packs);
    }

    [Fact]
    public void A_chain_of_two_neighbors_that_are_near_the_anchor_but_not_each_other_makes_two_packs()
    {
        // B and C are each 18 degrees from the anchor (cosine 0.951) and 36 degrees from one another (cosine 0.809, below the floor).
        LongRestSourceLeaf anchor = Anchor();

        LongRestSourceLeaf b = LongRestPackingData.Leaf(1, "b", 18);

        LongRestSourceLeaf c = LongRestPackingData.Leaf(2, "c", -18);

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, [b, c], LongRestDiscoveryLimits.ProviderInputUtf8Bytes, Floor).Value;

        Assert.Equal([[0, 1], [0, 2]], Indexes(packing));

        Assert.Equal(0, packing.SkippedIncohesive);
    }

    // The anchor is the first axis. A cone neighbour leans 20 degrees away from it along an axis of its own: each is near the anchor
    // (cosine 0.94) and every two are 28 degrees apart (cosine 0.883), below a floor of 0.92.
    private static ImmutableArray<float> Cone(int axis)
    {
        float[] vector = new float[6];

        vector[0] = (float)Math.Cos(20 * Math.PI / 180);

        vector[axis] = (float)Math.Sin(20 * Math.PI / 180);

        return [.. vector];
    }

    [Fact]
    public void A_neighbor_near_the_anchor_but_near_no_pack_that_has_room_is_incohesive_once_the_anchor_has_its_three_packs()
    {
        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, "anchor", vector: [1f, 0f, 0f, 0f, 0f, 0f]);

        ImmutableArray<LongRestSourceLeaf> cone = [.. Enumerable.Range(1, 4).Select(axis => LongRestPackingData.Leaf(axis, "cone " + axis, vector: Cone(axis)))];

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, cone, LongRestDiscoveryLimits.ProviderInputUtf8Bytes, Floor).Value;

        Assert.Equal([[0, 1], [0, 2], [0, 3]], Indexes(packing));

        Assert.Equal(1, packing.SkippedIncohesive);

        Assert.Equal(0, packing.SkippedPackLimit + packing.SkippedUnpaired + packing.SkippedOversize);
    }

    [Fact]
    public void A_neighbor_that_found_no_room_in_any_pack_is_the_pack_limit_and_not_incohesive()
    {
        // Three neighbours fill the budget of their packs; the fourth is near all of them but no pack has room for it.
        LongRestGroupPacking packing = LongRestGroupPacker.Pack(Anchor(new string('x', 1500)), Neighbors(7, 1500), BudgetFor(3, 1500), Floor).Value;

        Assert.Equal(1, packing.SkippedPackLimit);

        Assert.Equal(0, packing.SkippedIncohesive);
    }

    [Fact]
    public void Every_non_duplicate_pack_is_a_clique_under_the_anchors_floor_over_seeded_random_vectors()
    {
        Random random = new(20261015);

        for (int round = 0; round < 200; round++)
        {
            int dimensions = random.Next(2, 9);

            ImmutableArray<float> Draw(double spread) => [.. Enumerable.Range(0, dimensions).Select(axis => (float)((axis == 0 ? 1.0 : 0.0) + (random.NextDouble() * 2 - 1) * spread))];

            double tightness = random.NextDouble() * 0.9;

            LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, "anchor", null, Draw(0));

            ImmutableArray<LongRestSourceLeaf> neighbors = [.. Enumerable.Range(1, random.Next(1, 33)).Select(index => LongRestPackingData.Leaf(index, "n" + index, null, Draw(tightness)))];

            int floor = random.Next(500, 1000);

            LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, LongRestDiscoveryLimits.ProviderInputUtf8Bytes, floor).Value;

            LongRestPairingVectors vectors = new();

            foreach (ImmutableArray<LongRestSourceLeaf> pack in packing.Packs)
            {
                Assert.Equal(anchor.Target, pack[0].Target);

                for (int left = 0; left < pack.Length; left++)
                {
                    for (int right = left + 1; right < pack.Length; right++)
                    {
                        Assert.True(LongRestPairing.SameContent(pack[left], pack[right]) || vectors.Cosine(pack[left], pack[right]) >= floor / 1000.0,
                            "Every pair in a pack must clear the anchor's floor.");
                    }
                }
            }

            // Every neighbour is in some pack or counted as left out: a pair unit repeats a member of a clique and adds nobody.
            int packed = packing.Packs.SelectMany(pack => pack.Skip(1)).Select(leaf => leaf.Target.MemoryId).Distinct().Count();

            Assert.Equal(neighbors.Length, packed + packing.SkippedOversize + packing.SkippedUnpaired + packing.SkippedIncohesive + packing.SkippedPackLimit);
        }
    }

    [Fact]
    public void Pair_Fallback_pairs_the_anchor_with_every_member_of_each_clique_of_three_or_more_in_clique_order()
    {
        // Two cliques: the neighbours at 0, 1 and 2 degrees are near one another; 40 degrees away and below the floor of a member at -1 would not be.
        LongRestSourceLeaf anchor = Anchor();

        ImmutableArray<LongRestSourceLeaf> neighbors =
        [
            LongRestPackingData.Leaf(1, "n1", 1),
            LongRestPackingData.Leaf(2, "n2", 2),
            LongRestPackingData.Leaf(3, "n3", 3),
        ];

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, LongRestDiscoveryLimits.ProviderInputUtf8Bytes, Floor).Value;

        Assert.Equal([[0, 1, 2, 3], [0, 1], [0, 2], [0, 3]], Indexes(packing));
    }

    [Fact]
    public void A_clique_of_two_has_no_pair_fallback_and_the_fallback_is_bounded_by_the_limit()
    {
        LongRestGroupPacking small = LongRestGroupPacker.Pack(Anchor(), Neighbors(1, 10), LongRestDiscoveryLimits.ProviderInputUtf8Bytes, Floor).Value;

        Assert.Single(small.Packs);

        LongRestGroupPacking large = LongRestGroupPacker.Pack(Anchor(), Neighbors(32, 10), LongRestDiscoveryLimits.ProviderInputUtf8Bytes, Floor).Value;

        Assert.Equal(LongRestDiscoveryLimits.PairFallbackLimit, large.Packs.Count(pack => pack.Length == 2));

        // The pairs follow the clique order: the first fifteen members of the first clique.
        Assert.Equal(Enumerable.Range(1, 15), large.Packs.Where(pack => pack.Length == 2).Select(pack => checked((int)pack[1].InsertionSequence) - 1));

        Assert.True(large.Packs.Length <= 99);
    }

    [Fact]
    public void Packing_refuses_missing_content_unbounded_neighbors_and_budgets_and_floors_outside_the_recipe_bounds()
    {
        LongRestSourceLeaf anchor = Anchor("anchor");

        Assert.True(LongRestGroupPacker.Pack(anchor with { Content = null }, Neighbors(2, 10), 8192, Floor).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, [LongRestPackingData.Leaf(1, "n", 0) with { Content = null }], 8192, Floor).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(33, 10), 8192, Floor).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, default, 8192, Floor).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(1, 10), 4095, Floor).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(1, 10), 32769, Floor).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(1, 10), 4096, Floor).IsSuccess);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(1, 10), 32768, Floor).IsSuccess);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(1, 10), 8192, 499).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(1, 10), 8192, 1001).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(1, 10), 8192, 500).IsSuccess);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(1, 10), 8192, 1000).IsSuccess);

        Assert.Equal(ErrorCodes.LongRest.InvalidRequest, LongRestGroupPacker.Pack(anchor, Neighbors(33, 10), 8192, Floor).Error.Code);

        Assert.True(LongRestGroupPacker.Pack(anchor, [LongRestPackingData.Leaf(1, "\ud800", 0)], 8192, Floor).IsFailure);
    }

    [Fact]
    public void Packing_is_deterministic_and_never_repeats_a_neighbor_within_a_clique_pack()
    {
        Random random = new(20261014);

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, LongRestPackingData.Content(random, 900), 0);

        ImmutableArray<LongRestSourceLeaf> neighbors = [.. Enumerable.Range(1, 30).Select(index => LongRestPackingData.Leaf(index, LongRestPackingData.Content(random, random.Next(50, 1600)), random.NextDouble() * 15))];

        LongRestGroupPacking first = LongRestGroupPacker.Pack(anchor, neighbors, 6000, Floor).Value;

        LongRestGroupPacking second = LongRestGroupPacker.Pack(anchor, neighbors, 6000, Floor).Value;

        Assert.Equal(first.Packs.Select(pack => pack.Select(leaf => leaf.Target).ToArray()), second.Packs.Select(pack => pack.Select(leaf => leaf.Target).ToArray()));

        // A neighbour is placed in at most one clique pack: the pack-order walk below finds no repeat among the first three.
        long[] placed = [.. first.Packs.Take(LongRestGroupPacker.MaximumPacksPerAnchor).SelectMany(pack => pack.Skip(1)).Select(leaf => leaf.InsertionSequence)];

        Assert.Equal(placed.Distinct().Count(), placed.Length);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(5328)]
    [InlineData(8192)]
    [InlineData(16384)]
    [InlineData(29000)]
    [InlineData(30500)]
    [InlineData(32000)]
    [InlineData(32768)]
    public void Every_clique_pack_stays_within_the_budget_and_every_neighbor_is_packed_or_counted(int budget)
    {
        Random random = new(budget);

        for (int round = 0; round < 40; round++)
        {
            int anchorBytes = random.Next(1, budget / 2);

            LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, LongRestPackingData.Content(random, anchorBytes), 0);

            int count = random.Next(0, 33);

            ImmutableArray<LongRestSourceLeaf> neighbors = [.. Enumerable.Range(1, count)
                .Select(index => LongRestPackingData.Leaf(index, LongRestPackingData.Content(random, random.Next(1, Math.Max(2, budget / (round % 4 + 1)))), 0))];

            LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, budget, Floor).Value;

            Assert.InRange(packing.Packs.Length, 0, 3 + LongRestGroupPacker.MaximumPacksPerAnchor + LongRestDiscoveryLimits.PairFallbackLimit);

            foreach (ImmutableArray<LongRestSourceLeaf> pack in packing.Packs)
            {
                Assert.Equal(anchor.Target, pack[0].Target);

                Assert.InRange(pack.Length, 2, LongRestDiscoveryLimits.Targets);

                Assert.True(Fixed + pack.Sum(Block) <= budget, "A pack exceeds the input budget.");
            }

            // Every neighbour is in some pack or counted: the pair units repeat members of the cliques and add nobody.
            int packed = packing.Packs.SelectMany(pack => pack.Skip(1)).Select(leaf => leaf.Target.MemoryId).Distinct().Count();

            Assert.Equal(count, packed + packing.SkippedOversize + packing.SkippedPackLimit + packing.SkippedUnpaired + packing.SkippedIncohesive);
        }
    }
}
