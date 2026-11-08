using Microsoft.CodeAnalysis;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private static bool IsReviewedReferenceComparerProperty(IPropertySymbol property, bool mutation) =>
            !mutation && property is { IsStatic: true, Name: "Instance", Parameters.Length: 0 }
            && IsExactFrameworkType(property.ContainingType, "System.Collections.Generic.ReferenceEqualityComparer",
                typeof(ReferenceEqualityComparer).Assembly.GetName());
    }
}
