using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestGroupPackerTests
{
    private static readonly long Fixed = LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Adjudication);

    private static long Block(LongRestSourceLeaf leaf) => LongRestPromptLayout.AdjudicationMemoryBytes(leaf);

    // A budget that holds the fixed prompt and exactly <blocks> equal blocks of this content size.
    private static int BudgetFor(int blocks, int contentBytes) => checked((int)(Fixed + blocks * Block(LongRestPackingData.Leaf(0, new string('x', contentBytes)))));

    private static ImmutableArray<LongRestSourceLeaf> Neighbors(int count, int contentBytes, int first = 1) =>
        [.. Enumerable.Range(first, count).Select(index => LongRestPackingData.Leaf(index, new string('y', contentBytes)))];

    [Fact]
    public void The_pack_limit_is_the_three_groups_the_merged_neighbors_always_made()
    {
        Assert.Equal(3, LongRestGroupPacker.MaximumPacksPerAnchor);

        Assert.Equal(32, LongRestDiscoveryLimits.MergedNeighbors);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    public void A_group_that_already_fits_is_the_ranked_neighbors_in_the_same_chunks_the_grouping_policy_made(int neighborCount)
    {
        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, "anchor content");

        ImmutableArray<LongRestSourceLeaf> neighbors = Neighbors(neighborCount, 40);

        LongRestCandidate[] candidates = [.. neighbors.Select(leaf => new LongRestCandidate(leaf.Target, LongRestDiscoveryLane.ExactHash, 0))];

        ImmutableArray<LongRestCandidate> merged = LongRestDiscoveryPolicy.MergeCandidates(anchor.Target, candidates).Value;

        ImmutableArray<LongRestCandidateGroup> groups = LongRestDiscoveryPolicy.GroupCandidates(anchor.Target, merged).Value;

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, LongRestDiscoveryLimits.ProviderInputUtf8Bytes).Value;

        Assert.Equal(groups.Select(group => group.Targets.ToArray()), packing.Packs.Select(pack => pack.Select(leaf => leaf.Target).ToArray()));

        Assert.Equal(0, packing.SkippedOversize);

        Assert.Equal(0, packing.SkippedPackLimit);
    }

    [Fact]
    public void An_oversized_group_splits_into_packs_that_each_start_with_the_anchor_and_keep_the_ranked_order()
    {
        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 1500));

        ImmutableArray<LongRestSourceLeaf> neighbors = Neighbors(5, 1500);

        // The fixed prompt, the anchor and two neighbours fit; a third neighbour does not.
        int budget = BudgetFor(3, 1500);

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, budget).Value;

        Assert.Equal(3, packing.Packs.Length);

        Assert.All(packing.Packs, pack => Assert.Equal(anchor.Target, pack[0].Target));

        Assert.Equal([[0, 1, 2], [0, 3, 4], [0, 5]], packing.Packs.Select(pack => pack.Select(leaf => leaf.InsertionSequence - 1).ToArray()));

        Assert.Equal(0, packing.SkippedOversize);

        Assert.Equal(0, packing.SkippedPackLimit);

        Assert.All(packing.Packs, pack => Assert.True(Fixed + pack.Sum(Block) <= budget));
    }

    [Fact]
    public void An_anchor_keeps_at_most_three_packs_and_counts_the_neighbors_beyond_them()
    {
        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 1500));

        // Seven neighbours at two per pack would need four packs.
        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, Neighbors(7, 1500), BudgetFor(3, 1500)).Value;

        Assert.Equal(3, packing.Packs.Length);

        Assert.Equal(1, packing.SkippedPackLimit);

        Assert.Equal(0, packing.SkippedOversize);

        Assert.Equal(6, packing.Packs.Sum(pack => pack.Length - 1));
    }

    [Fact]
    public void A_neighbor_that_cannot_fit_beside_the_anchor_is_skipped_and_counted_and_the_rest_still_pack()
    {
        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 1500));

        LongRestSourceLeaf huge = LongRestPackingData.Leaf(2, new string('z', 4000));

        ImmutableArray<LongRestSourceLeaf> neighbors = [LongRestPackingData.Leaf(1, new string('y', 1500)), huge, LongRestPackingData.Leaf(3, new string('y', 1500))];

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, BudgetFor(3, 1500)).Value;

        Assert.Equal(1, packing.SkippedOversize);

        Assert.Equal(0, packing.SkippedPackLimit);

        Assert.DoesNotContain(packing.Packs.SelectMany(pack => pack), leaf => leaf.Target == huge.Target);

        Assert.Equal([[0, 1, 3]], packing.Packs.Select(pack => pack.Select(leaf => leaf.InsertionSequence - 1).ToArray()));
    }

    [Fact]
    public void An_exact_duplicate_of_the_anchor_that_does_not_fit_is_packed_with_the_other_duplicates_outside_the_budget()
    {
        string same = "1".PadLeft(64, '0');

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 1500), same);

        // Two oversize duplicates, one oversize stranger and two neighbours that fit.
        ImmutableArray<LongRestSourceLeaf> neighbors =
        [
            LongRestPackingData.Leaf(1, new string('d', 6000), same),
            LongRestPackingData.Leaf(2, new string('y', 1500)),
            LongRestPackingData.Leaf(3, new string('s', 6000)),
            LongRestPackingData.Leaf(4, new string('d', 6000), same),
            LongRestPackingData.Leaf(5, new string('y', 1500)),
        ];

        int budget = BudgetFor(3, 1500);

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, budget).Value;

        Assert.Equal([[0, 2, 5], [0, 1, 4]], packing.Packs.Select(pack => pack.Select(leaf => leaf.InsertionSequence - 1).ToArray()));

        // The first pack renders within the budget; the duplicate-only pack is committed without a request and is exempt from it.
        Assert.True(Fixed + packing.Packs[0].Sum(Block) <= budget);

        Assert.True(Fixed + packing.Packs[1].Sum(Block) > budget);

        Assert.All(packing.Packs[1], leaf => Assert.Equal(same, leaf.Snapshot.ContentHash));

        Assert.Equal(1, packing.SkippedOversize);

        Assert.Equal(0, packing.SkippedPackLimit);
    }

    [Fact]
    public void Duplicate_packs_count_toward_the_three_packs_an_anchor_keeps()
    {
        string same = "1".PadLeft(64, '0');

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 1500), same);

        // Six neighbours that fit make three packs, so two oversize duplicates have no pack left.
        ImmutableArray<LongRestSourceLeaf> neighbors = [.. Neighbors(6, 1500), LongRestPackingData.Leaf(7, new string('d', 6000), same), LongRestPackingData.Leaf(8, new string('d', 6000), same)];

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, BudgetFor(3, 1500)).Value;

        Assert.Equal(3, packing.Packs.Length);

        Assert.Equal(2, packing.SkippedPackLimit);

        Assert.Equal(0, packing.SkippedOversize);
    }

    [Fact]
    public void An_anchor_that_cannot_fit_alone_still_packs_its_exact_duplicates_and_counts_the_rest()
    {
        string same = "1".PadLeft(64, '0');

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 6000), same);

        ImmutableArray<LongRestSourceLeaf> neighbors = [LongRestPackingData.Leaf(1, new string('d', 6000), same), LongRestPackingData.Leaf(2, "stranger")];

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, LongRestPromptLayout.MinimumInputUtf8Bytes).Value;

        Assert.Equal([[0, 1]], packing.Packs.Select(pack => pack.Select(leaf => leaf.InsertionSequence - 1).ToArray()));

        Assert.Equal(1, packing.SkippedOversize);
    }

    [Fact]
    public void An_anchor_that_cannot_fit_alone_leaves_no_pack_and_every_neighbor_counts_as_skipped()
    {
        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, new string('x', 6000));

        LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, Neighbors(4, 100), LongRestPromptLayout.MinimumInputUtf8Bytes).Value;

        Assert.Empty(packing.Packs);

        Assert.Equal(4, packing.SkippedOversize);
    }

    [Fact]
    public void No_neighbors_means_no_pack()
    {
        LongRestGroupPacking packing = LongRestGroupPacker.Pack(LongRestPackingData.Leaf(0, "anchor"), [], LongRestDiscoveryLimits.ProviderInputUtf8Bytes).Value;

        Assert.Empty(packing.Packs);

        Assert.Equal(0, packing.SkippedOversize + packing.SkippedPackLimit);
    }

    [Fact]
    public void Packing_refuses_missing_content_unbounded_neighbors_and_budgets_outside_the_recipe_bounds()
    {
        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, "anchor");

        Assert.True(LongRestGroupPacker.Pack(anchor with { Content = null }, Neighbors(2, 10), 8192).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, [LongRestPackingData.Leaf(1, "n") with { Content = null }], 8192).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(33, 10), 8192).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, default, 8192).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(1, 10), 4095).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(1, 10), 32769).IsFailure);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(1, 10), 4096).IsSuccess);

        Assert.True(LongRestGroupPacker.Pack(anchor, Neighbors(1, 10), 32768).IsSuccess);

        Assert.Equal(ErrorCodes.LongRest.InvalidRequest, LongRestGroupPacker.Pack(anchor, Neighbors(33, 10), 8192).Error.Code);

        Assert.True(LongRestGroupPacker.Pack(anchor, [LongRestPackingData.Leaf(1, "\ud800")], 8192).IsFailure);
    }

    [Fact]
    public void Packing_is_deterministic_and_never_reorders_or_repeats_a_neighbor()
    {
        Random random = new(20261014);

        LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, LongRestPackingData.Content(random, 900));

        ImmutableArray<LongRestSourceLeaf> neighbors = [.. Enumerable.Range(1, 30).Select(index => LongRestPackingData.Leaf(index, LongRestPackingData.Content(random, random.Next(50, 1600))))];

        LongRestGroupPacking first = LongRestGroupPacker.Pack(anchor, neighbors, 6000).Value;

        LongRestGroupPacking second = LongRestGroupPacker.Pack(anchor, neighbors, 6000).Value;

        Assert.Equal(first.Packs.Select(pack => pack.Select(leaf => leaf.Target).ToArray()), second.Packs.Select(pack => pack.Select(leaf => leaf.Target).ToArray()));

        long[] ranked = [.. first.Packs.SelectMany(pack => pack.Skip(1)).Select(leaf => leaf.InsertionSequence)];

        Assert.Equal(ranked.Order(), ranked);

        Assert.Equal(ranked.Distinct().Count(), ranked.Length);
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
    public void Every_pack_stays_within_the_budget_and_every_neighbor_is_packed_or_counted(int budget)
    {
        Random random = new(budget);

        for (int round = 0; round < 40; round++)
        {
            int anchorBytes = random.Next(1, budget / 2);

            LongRestSourceLeaf anchor = LongRestPackingData.Leaf(0, LongRestPackingData.Content(random, anchorBytes));

            int count = random.Next(0, 33);

            ImmutableArray<LongRestSourceLeaf> neighbors = [.. Enumerable.Range(1, count)
                .Select(index => LongRestPackingData.Leaf(index, LongRestPackingData.Content(random, random.Next(1, Math.Max(2, budget / (round % 4 + 1))))))];

            LongRestGroupPacking packing = LongRestGroupPacker.Pack(anchor, neighbors, budget).Value;

            Assert.InRange(packing.Packs.Length, 0, LongRestGroupPacker.MaximumPacksPerAnchor);

            foreach (ImmutableArray<LongRestSourceLeaf> pack in packing.Packs)
            {
                Assert.Equal(anchor.Target, pack[0].Target);

                Assert.InRange(pack.Length, 2, LongRestDiscoveryLimits.Targets);

                Assert.True(Fixed + pack.Sum(Block) <= budget, "A pack exceeds the input budget.");
            }

            Assert.Equal(count, packing.Packs.Sum(pack => pack.Length - 1) + packing.SkippedOversize + packing.SkippedPackLimit);
        }
    }
}
