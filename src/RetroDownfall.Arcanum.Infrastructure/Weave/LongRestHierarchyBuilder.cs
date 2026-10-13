using System.Collections.Immutable;
using System.Diagnostics.Metrics;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>Builds the exact paid fold identity from one verified persisted frontier group.</summary>
internal static class LongRestHierarchyBuilder
{
    internal static readonly Histogram<int> SelectedSummaries = ArcanumMetrics.Meter.CreateHistogram<int>("arcanum.long_rest.discovery.selected_summaries");

    internal static readonly Histogram<int> TraversalFrontier = ArcanumMetrics.Meter.CreateHistogram<int>("arcanum.long_rest.discovery.frontier");

    internal static readonly Histogram<int> ExpandedLeaves = ArcanumMetrics.Meter.CreateHistogram<int>("arcanum.long_rest.discovery.expanded_leaves");

    internal static LongRestHierarchyFold CreateFold(LongRestDiscoveryGeneration generation, int layer, long group,
        ImmutableArray<LongRestHierarchyChild> children)
    {
        CovenantDigest recipe = LongRestDiscoveryHash.Recipe(generation.Recipe).Value;

        CovenantDigest membership = LongRestHierarchy.Membership(children, recipe);

        LongRestWorkIdentity work = new(generation.GenerationId, generation.OperationId, "fold:" + layer.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + group.ToString(System.Globalization.CultureInfo.InvariantCulture),
            LongRestWorkKind.HierarchyFold, generation.Scope, generation.Fence, membership, recipe);

        LongRestHierarchyFold fold = new(work, layer, group, membership, children);

        Result<CovenantDigest> source = LongRestDiscoveryHash.HierarchyFold(fold);

        if (source.IsFailure)
        {
            throw new InvalidDataException();
        }

        return fold with { Work = work with { SourceDigest = source.Value } };
    }
}
