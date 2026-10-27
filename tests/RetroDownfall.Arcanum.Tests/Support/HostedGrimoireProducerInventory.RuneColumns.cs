using Microsoft.CodeAnalysis;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Pinned Terminal.Gui delegates this exact Rune overload to Wcwidth's
        // numeric tables under a lock. No supplied formatter, callback or I/O runs.
        // Argument and authored conversion traversal remain independent.
        private static bool IsReviewedRuneColumnCalculation(IMethodSymbol method)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            return definition is { IsStatic: true, MethodKind: MethodKind.Ordinary, Arity: 0, Name: "GetColumns", ReturnType.SpecialType: SpecialType.System_Int32, Parameters: [{ RefKind: RefKind.None } rune] }
                && IsExactFrameworkType(definition.ContainingType,
                    "Terminal.Gui.Text.RuneExtensions", typeof(Terminal.Gui.Text.RuneExtensions).Assembly.GetName())
                && IsExactFrameworkType(rune.Type, "System.Text.Rune", typeof(System.Text.Rune).Assembly.GetName());
        }
    }
}
