using Microsoft.CodeAnalysis;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // These two overloads touch only the object's synchronization state. They
        // grant no Work/Effect or callback completion, and arguments stay traversed.
        private static bool IsReviewedExactMonitorMember(IMethodSymbol method) =>
            method is
            {
                MethodKind: MethodKind.Ordinary,
                IsStatic: true,
                Arity: 0,
                DeclaredAccessibility: Accessibility.Public,
                Parameters: [{ RefKind: RefKind.None, Type.SpecialType: SpecialType.System_Object }],
            }
            && IsExactFrameworkType(method.ContainingType, "System.Threading.Monitor",
                typeof(System.Threading.Monitor).Assembly.GetName())
            && (method.Name == "TryEnter" && method.ReturnType.SpecialType == SpecialType.System_Boolean
                || method.Name == "Exit" && method.ReturnsVoid);
    }
}
