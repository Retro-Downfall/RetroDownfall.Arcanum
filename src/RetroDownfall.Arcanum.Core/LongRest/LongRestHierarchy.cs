using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Core.LongRest;

public sealed record LongRestHierarchyInput(LongRestHierarchyChild Child, ImmutableArray<float> Vector);

/// <summary>
/// Deterministic bounded page planning. A one-child group carries the original node. A child too large to sit in any summary request of the
/// recipe's input budget, even with one companion, is never folded: it is carried as a one-child group like any child that found no
/// companion, so the forest still covers it exactly once and no other child is held back by it.
/// </summary>
public static class LongRestHierarchy
{
    /// <summary>
    /// True when <paramref name="child"/> cannot be summarized within <paramref name="inputBudgetBytes"/> at all: even a summary request
    /// that holds nothing else is over the budget. Such a child is carried unsummarized.
    /// </summary>
    public static bool IsOversize(LongRestHierarchyChild child, int inputBudgetBytes)
    {
        ArgumentNullException.ThrowIfNull(child);

        return LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Summary) + LongRestPromptLayout.SummaryObservationBytes(child.Content) > inputBudgetBytes;
    }

    public static Result<ImmutableArray<ImmutableArray<LongRestHierarchyChild>>> PlanPage(
        ImmutableArray<LongRestHierarchyInput> page, int layer, CovenantDigest recipe, int inputBudgetBytes, CancellationToken cancellationToken)
    {
        if (page.IsDefault || page.Length > LongRestDiscoveryLimits.SourcePage || layer is < 1 or > 6 || !recipe.IsValid
            || inputBudgetBytes is < LongRestPromptLayout.MinimumInputUtf8Bytes or > LongRestDiscoveryLimits.ProviderInputUtf8Bytes
            || page.Any(input => input is null || input.Child is null || input.Child.Layer < 0 || input.Child.Layer >= layer)
            || page.Select(input => input.Child.NodeId).Distinct().Count() != page.Length)
        {
            return Invalid("Hierarchy planning requires one bounded exact lower-layer page.");
        }

        try
        {
            List<ImmutableArray<LongRestHierarchyChild>> groups = [];

            foreach (IGrouping<(bool CarriedSource, int Width), LongRestHierarchyInput> partition in page
                .OrderBy(StableId, StringComparer.Ordinal).GroupBy(input => (layer > 1 && input.Child.Layer == 0, input.Vector.Length)))
            {
                LongRestHierarchyInput[] inputs = partition.ToArray();

                if (partition.Key.CarriedSource)
                {
                    groups.AddRange(inputs.Select(input => ImmutableArray.Create(input.Child)));

                    continue;
                }

                Dictionary<string, LongRestHierarchyChild> children = inputs.ToDictionary(StableId, input => input.Child, StringComparer.Ordinal);

                IEnumerable<IReadOnlyList<string>> memberships;

                if (partition.Key.Width == 0)
                {
                    memberships = [children.Keys.ToArray()];
                }
                else
                {
                    CovenantDigest seedDigest = Membership(inputs.Select(input => input.Child), recipe);

                    ulong seed = BinaryPrimitives.ReadUInt64BigEndian(seedDigest.Bytes);

                    SphericalKMeansResult clustered = SphericalKMeans.ClusterLayer(
                        inputs.Select(input => new SphericalKMeansPoint(StableId(input), input.Vector.ToArray())).ToArray(),
                        LongRestDiscoveryLimits.HierarchyChildren, LongRestDiscoveryLimits.SourcePage, seed,
                        partition.Key.Width, cancellationToken: cancellationToken);

                    if (clustered.Rejected.Count != 0)
                    {
                        return Invalid("An unusable persisted vector cannot disappear behind the fold cursor.");
                    }

                    memberships = clustered.Clusters.Select(cluster => cluster.MemberIds);
                }

                List<List<LongRestHierarchyChild>> partitionGroups = [];

                List<LongRestHierarchyChild> oversize = [];

                // A fold is the fixed summary prompt plus one observation block per child, and must render within the budget.
                long fixedBytes = LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Summary);

                foreach (IReadOnlyList<string> membership in memberships)
                {
                    List<LongRestHierarchyChild> current = [];

                    long bytes = fixedBytes;

                    foreach (string member in membership)
                    {
                        LongRestHierarchyChild child = children[member];

                        if (string.IsNullOrWhiteSpace(child.Content))
                        {
                            return Invalid("An empty child cannot disappear behind the fold cursor.");
                        }

                        long count = LongRestPromptLayout.SummaryObservationBytes(child.Content);

                        if (fixedBytes + count > inputBudgetBytes)
                        {
                            // No request can hold it, so it takes no part in the folds around it and is carried on its own.
                            oversize.Add(child);

                            continue;
                        }

                        if (current.Count == LongRestDiscoveryLimits.HierarchyChildren || bytes + count > inputBudgetBytes)
                        {
                            partitionGroups.Add(current);

                            current = [];

                            bytes = fixedBytes;
                        }

                        current.Add(child);

                        bytes += count;
                    }

                    if (current.Count > 0)
                    {
                        partitionGroups.Add(current);
                    }
                }

                for (int index = partitionGroups.Count - 1; index >= 0; index--)
                {
                    if (partitionGroups[index].Count != 1)
                    {
                        continue;
                    }

                    LongRestHierarchyChild single = partitionGroups[index][0];

                    int destination = partitionGroups.FindIndex(group => !ReferenceEquals(group, partitionGroups[index])
                        && group.Count < LongRestDiscoveryLimits.HierarchyChildren
                        && fixedBytes + group.Sum(child => LongRestPromptLayout.SummaryObservationBytes(child.Content))
                            + LongRestPromptLayout.SummaryObservationBytes(single.Content) <= inputBudgetBytes);

                    if (destination >= 0)
                    {
                        partitionGroups[destination].Add(single);

                        partitionGroups.RemoveAt(index);
                    }
                }

                groups.AddRange(partitionGroups.Select(group => group.OrderBy(child => ChildId(child), StringComparer.Ordinal).ToImmutableArray()));

                groups.AddRange(oversize.Select(child => ImmutableArray.Create(child)));
            }

            return groups.OrderBy(group => ChildId(group[0]), StringComparer.Ordinal).ToImmutableArray();
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return Invalid("Hierarchy input is not bounded strict UTF-8 with valid vector identities.");
        }
    }

    public static CovenantDigest Membership(IEnumerable<LongRestHierarchyChild> children, CovenantDigest recipe)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData("Arcanum.LongRest.HierarchyMembership.v1\0"u8);

        LongRestHierarchyChild[] ordered = children.OrderBy(child => child.MembershipDigest.ToString(), StringComparer.Ordinal).ToArray();

        AppendInteger(1);

        AppendDigest(recipe);

        AppendInteger(ordered.Length);

        foreach (LongRestHierarchyChild child in ordered)
        {
            AppendDigest(child.MembershipDigest);
        }

        void AppendInteger(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];

            BinaryPrimitives.WriteInt32BigEndian(bytes, value);

            hash.AppendData(bytes);
        }

        void AppendDigest(CovenantDigest digest)
        {
            AppendInteger(digest.Bytes.Length);

            hash.AppendData(digest.Bytes);
        }

        return new(hash.GetHashAndReset());
    }

    private static string StableId(LongRestHierarchyInput input) => ChildId(input.Child);

    private static string ChildId(LongRestHierarchyChild child) => child.Layer == 0 && child.ExpandedTargets.Length == 1
        ? "source:" + child.ExpandedTargets[0].MemoryId
        : "node:" + child.Layer.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)
            + ":" + child.Group.ToString("D20", System.Globalization.CultureInfo.InvariantCulture)
            + ":" + child.NodeId.ToString("D");

    private static Result<ImmutableArray<ImmutableArray<LongRestHierarchyChild>>> Invalid(string message) =>
        new Error(ErrorCodes.LongRest.InvalidRequest, message);
}
