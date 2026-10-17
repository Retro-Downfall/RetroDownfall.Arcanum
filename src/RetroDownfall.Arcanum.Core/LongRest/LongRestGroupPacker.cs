using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// What packing an anchor's neighbours made, and what it left out. <see cref="Packs"/> are in work order: the duplicate-only packs, then the
/// clique packs, then the pair units. <see cref="SkippedOversize"/> counts neighbours that cannot fit a request with their anchor at all;
/// <see cref="SkippedUnpaired"/> counts nominees the Pairing Gate did not pair with the anchor (below its floor, or without a usable vector to
/// judge); <see cref="SkippedIncohesive"/> counts paired neighbours that fit an open pack by size but are not paired with one of its members;
/// <see cref="SkippedPackLimit"/> counts neighbours that would fit but found the anchor already holding its
/// <see cref="LongRestGroupPacker.MaximumPacksPerAnchor"/> clique packs.
/// </summary>
public sealed record LongRestGroupPacking(
    ImmutableArray<ImmutableArray<LongRestSourceLeaf>> Packs,
    int SkippedOversize,
    int SkippedPackLimit,
    int SkippedUnpaired = 0,
    int SkippedIncohesive = 0);

/// <summary>
/// Splits an anchor's nominated neighbours into the units the sweep decides, deterministically and anchor-first. Nomination is not admission:
/// the Pairing Gate (<see cref="LongRestPairing"/>) pairs a nominee with the anchor only when it is byte-identical or its cosine clears the
/// anchor's Background Floor, so a memory that merely shares words, or a summary's company, never reaches the model beside the anchor.
/// <list type="number">
/// <item>Exact First. Every neighbour byte-identical to the anchor, of any size, goes to duplicate-only packs of at most fifteen: a pack of
/// nothing but duplicates is committed locally with no request, so the budget does not apply to it and the envelope does not count it.</item>
/// <item>Clique packs. The other paired neighbours, nearest first (cosine, then memory id), are placed first-fit into the first open pack that has
/// room (sixteen memories, the byte budget) and in which the neighbour is paired, at the anchor's floor, with every member beside the anchor
/// (a member of identical content is exempt); otherwise it opens a new pack while fewer than <see cref="MaximumPacksPerAnchor"/> exist. An
/// equivalence decision folds every member into one survivor, and nearness to the anchor does not make two neighbours near each other, so every
/// pack is a clique under one calibrated predicate and the survivor the policy chooses is close to every memory it replaces.</item>
/// <item>Pair Fallback. After the clique packs, the anchor is paired with each member of every clique of three or more, in pack order, at most
/// <see cref="LongRestDiscoveryLimits.PairFallbackLimit"/> units. When the clique is applied the pairs' memories are consolidated and the unit is
/// disposed with no request; when the model refuses the clique as a whole, each pair is its own question.</item>
/// </list>
/// A neighbour that cannot fit a request with its anchor is skipped and counted, unless it is byte-identical to the anchor.
/// </summary>
public static class LongRestGroupPacker
{
    /// <summary>The clique packs that cost a request an anchor may have: its merged neighbours (32) in groups of fifteen beside the anchor.</summary>
    public const int MaximumPacksPerAnchor = (LongRestDiscoveryLimits.MergedNeighbors + LongRestDiscoveryLimits.Targets - 2) / (LongRestDiscoveryLimits.Targets - 1);

    public static Result<LongRestGroupPacking> Pack(LongRestSourceLeaf anchor, ImmutableArray<LongRestSourceLeaf> neighbors, int inputBudgetBytes, int floorPermille)
    {
        if (anchor is null || anchor.Content is null || neighbors.IsDefault || neighbors.Length > LongRestDiscoveryLimits.MergedNeighbors
            || neighbors.Any(static neighbor => neighbor is null || neighbor.Content is null)
            || inputBudgetBytes is < LongRestPromptLayout.MinimumInputUtf8Bytes or > LongRestDiscoveryLimits.ProviderInputUtf8Bytes
            || floorPermille is < LongRestDiscoveryLimits.PairingAbsoluteMinimumPermille or > 1000)
        {
            return Invalid("Packing requires an anchor and at most 32 neighbours with their content, an input budget within the recipe bounds and a pairing floor within its bounds.");
        }

        try
        {
            long baseBytes = LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Adjudication) + LongRestPromptLayout.AdjudicationMemoryBytes(anchor);

            LongRestPairingVectors vectors = new();

            List<LongRestSourceLeaf> duplicates = [];

            List<(LongRestSourceLeaf Leaf, double Cosine)> paired = [];

            int oversize = 0;

            int unpaired = 0;

            foreach (LongRestSourceLeaf neighbor in neighbors)
            {
                // Exact First: the maintenance commits a pack whose targets all share the anchor's content as exact duplicates, with no request,
                // so a duplicate is never left out for its size.
                if (LongRestPairing.SameContent(neighbor, anchor))
                {
                    duplicates.Add(neighbor);

                    continue;
                }

                if (baseBytes > inputBudgetBytes || baseBytes + LongRestPromptLayout.AdjudicationMemoryBytes(neighbor) > inputBudgetBytes)
                {
                    oversize++;

                    continue;
                }

                double cosine = vectors.Cosine(anchor, neighbor);

                if (!LongRestPairing.Clears(cosine, floorPermille))
                {
                    unpaired++;

                    continue;
                }

                paired.Add((neighbor, cosine));
            }

            // Nearest first; the memory id settles a tie, so the order is a function of the vectors and the identities alone.
            paired.Sort(static (left, right) =>
            {
                int order = right.Cosine.CompareTo(left.Cosine);

                return order != 0 ? order : StringComparer.Ordinal.Compare(left.Leaf.Snapshot.MemoryId, right.Leaf.Snapshot.MemoryId);
            });

            List<List<LongRestSourceLeaf>> cliques = [];

            List<long> used = [];

            int incohesive = 0;

            int limit = 0;

            foreach ((LongRestSourceLeaf neighbor, _) in paired)
            {
                long bytes = LongRestPromptLayout.AdjudicationMemoryBytes(neighbor);

                bool placed = false;

                bool fitted = false;

                for (int index = 0; index < cliques.Count && !placed; index++)
                {
                    if (cliques[index].Count >= LongRestDiscoveryLimits.Targets || used[index] + bytes > inputBudgetBytes)
                    {
                        continue;
                    }

                    fitted = true;

                    if (vectors.Cohesive(cliques[index], neighbor, floorPermille))
                    {
                        cliques[index].Add(neighbor);

                        used[index] += bytes;

                        placed = true;
                    }
                }

                if (placed)
                {
                    continue;
                }

                if (cliques.Count < MaximumPacksPerAnchor)
                {
                    cliques.Add([anchor, neighbor]);

                    used.Add(baseBytes + bytes);
                }
                else if (fitted)
                {
                    incohesive++;
                }
                else
                {
                    limit++;
                }
            }

            ImmutableArray<ImmutableArray<LongRestSourceLeaf>>.Builder packs = ImmutableArray.CreateBuilder<ImmutableArray<LongRestSourceLeaf>>();

            for (int offset = 0; offset < duplicates.Count; offset += LongRestDiscoveryLimits.Targets - 1)
            {
                packs.Add([anchor, .. duplicates.GetRange(offset, Math.Min(LongRestDiscoveryLimits.Targets - 1, duplicates.Count - offset))]);
            }

            foreach (List<LongRestSourceLeaf> clique in cliques)
            {
                packs.Add([.. clique]);
            }

            int pairs = 0;

            foreach (List<LongRestSourceLeaf> clique in cliques)
            {
                if (clique.Count < 3)
                {
                    continue;
                }

                for (int member = 1; member < clique.Count && pairs < LongRestDiscoveryLimits.PairFallbackLimit; member++)
                {
                    packs.Add([anchor, clique[member]]);

                    pairs++;
                }
            }

            return new LongRestGroupPacking(packs.ToImmutable(), oversize, limit, unpaired, incohesive);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return Invalid("A packed source is not strict UTF-8.");
        }
    }

    private static Result<LongRestGroupPacking> Invalid(string message) => new Error(ErrorCodes.LongRest.InvalidRequest, message);
}
