using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Only the pinned framework infinity field is mechanical. Authored/dynamic getters,
        // conversions, finite values and options receivers still retain their ordinary proof work.
        private static bool IsExactInfiniteSdkNetworkTimeout(IPropertySymbol property, IOperation value)
        {
            if (property is not
                {
                    Name: "NetworkTimeout", IsStatic: false, Parameters.Length: 0,
                    SetMethod: { IsStatic: false, IsVirtual: false, Parameters: [{ RefKind: RefKind.None } parameter], ReturnsVoid: true }
                }
                || !IsExactFrameworkType(property.ContainingType, "System.ClientModel.Primitives.ClientPipelineOptions",
                    typeof(System.ClientModel.Primitives.ClientPipelineOptions).Assembly.GetName())
                || !SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type)
                || !IsSdkTimeoutType(property.Type))
            {
                return false;
            }

            while (value is IConversionOperation { OperatorMethod: null } conversion) value = conversion.Operand;

            return value is IFieldReferenceOperation
            {
                Instance: null,
                Field: { Name: "InfiniteTimeSpan", IsStatic: true, IsReadOnly: true, DeclaredAccessibility: Accessibility.Public } field
            }
                && IsExactFrameworkType(field.ContainingType, "System.Threading.Timeout", typeof(Timeout).Assembly.GetName())
                && IsExactFrameworkType(field.Type, "System.TimeSpan", typeof(TimeSpan).Assembly.GetName())
                && !field.Locations.Any(static location => location.IsInSource);
        }

        private static bool IsSdkTimeoutType(ITypeSymbol type) =>
            IsExactFrameworkType(type, "System.TimeSpan", typeof(TimeSpan).Assembly.GetName())
                || type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments: [ITypeSymbol argument] }
                    && IsExactFrameworkType(argument, "System.TimeSpan", typeof(TimeSpan).Assembly.GetName());
    }
}
