using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// What packing an anchor's neighbours left out. <see cref="SkippedOversize"/> counts neighbours that cannot fit a request
/// with their anchor at all; <see cref="SkippedPackLimit"/> counts neighbours that would fit but found the anchor already holding
/// its <see cref="LongRestGroupPacker.MaximumPacksPerAnchor"/> packs.
/// </summary>
public sealed record LongRestGroupPacking(ImmutableArray<ImmutableArray<LongRestSourceLeaf>> Packs, int SkippedOversize, int SkippedPackLimit);

/// <summary>
/// Splits an anchor's discovered neighbours into adjudication packs that each render within the recipe's input budget. Packing is
/// deterministic and anchor-first: every pack starts with the anchor, takes neighbours in their ranked order while the next one
/// fits, and a neighbour that does not fit the open pack opens the next pack with the anchor. A neighbour that cannot fit a
/// request with its anchor is skipped and counted, unless it is an exact duplicate of the anchor: a pack of nothing but exact
/// duplicates is committed locally without a request, so the budget does not apply to it and such a neighbour joins the
/// duplicate packs that follow the others. The anchor keeps at most <see cref="MaximumPacksPerAnchor"/> packs that cost a request,
/// which is today's cost envelope: when everything fits, the packs are the ranked neighbours in chunks of fifteen, exactly as before.
/// A duplicate pack costs nothing, so the envelope does not count it and an anchor whose paid packs are full still consolidates its
/// oversize exact duplicates.
/// </summary>
public static class LongRestGroupPacker
{
    /// <summary>The packs that cost a request an anchor may have: its merged neighbours (32) in groups of fifteen beside the anchor.</summary>
    public const int MaximumPacksPerAnchor = (LongRestDiscoveryLimits.MergedNeighbors + LongRestDiscoveryLimits.Targets - 2) / (LongRestDiscoveryLimits.Targets - 1);

    public static Result<LongRestGroupPacking> Pack(LongRestSourceLeaf anchor, ImmutableArray<LongRestSourceLeaf> neighbors, int inputBudgetBytes)
    {
        if (anchor is null || anchor.Content is null || neighbors.IsDefault || neighbors.Length > LongRestDiscoveryLimits.MergedNeighbors
            || neighbors.Any(static neighbor => neighbor is null || neighbor.Content is null)
            || inputBudgetBytes is < LongRestPromptLayout.MinimumInputUtf8Bytes or > LongRestDiscoveryLimits.ProviderInputUtf8Bytes)
        {
            return Invalid("Packing requires an anchor and at most 32 neighbours with their content, and an input budget within the recipe bounds.");
        }

        try
        {
            long baseBytes = LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Adjudication) + LongRestPromptLayout.AdjudicationMemoryBytes(anchor);

            ImmutableArray<ImmutableArray<LongRestSourceLeaf>>.Builder packs = ImmutableArray.CreateBuilder<ImmutableArray<LongRestSourceLeaf>>();

            List<LongRestSourceLeaf> open = [anchor];

            long used = baseBytes;

            List<LongRestSourceLeaf> duplicates = [];

            int oversize = 0;

            int limit = 0;

            bool closed = false;

            foreach (LongRestSourceLeaf neighbor in neighbors)
            {
                long bytes = LongRestPromptLayout.AdjudicationMemoryBytes(neighbor);

                if (baseBytes > inputBudgetBytes || baseBytes + bytes > inputBudgetBytes)
                {
                    // The maintenance commits a pack whose targets all share the anchor's content hash as exact duplicates, with no
                    // request, so an oversize duplicate is not left out; it is packed below, exempt from the budget.
                    if (neighbor.Snapshot.ContentHash == anchor.Snapshot.ContentHash)
                    {
                        duplicates.Add(neighbor);
                    }
                    else
                    {
                        oversize++;
                    }

                    continue;
                }

                if (closed)
                {
                    limit++;

                    continue;
                }

                if (open.Count < LongRestDiscoveryLimits.Targets && used + bytes <= inputBudgetBytes)
                {
                    open.Add(neighbor);

                    used += bytes;

                    continue;
                }

                packs.Add([.. open]);

                if (packs.Count == MaximumPacksPerAnchor)
                {
                    closed = true;

                    limit++;

                    continue;
                }

                open = [anchor, neighbor];

                used = baseBytes + bytes;
            }

            if (!closed && open.Count > 1)
            {
                packs.Add([.. open]);
            }

            for (int offset = 0; offset < duplicates.Count; offset += LongRestDiscoveryLimits.Targets - 1)
            {
                packs.Add([anchor, .. duplicates.GetRange(offset, Math.Min(LongRestDiscoveryLimits.Targets - 1, duplicates.Count - offset))]);
            }

            return new LongRestGroupPacking(packs.ToImmutable(), oversize, limit);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return Invalid("A packed source is not strict UTF-8.");
        }
    }

    private static Result<LongRestGroupPacking> Invalid(string message) => new Error(ErrorCodes.LongRest.InvalidRequest, message);
}
