using Microsoft.CodeAnalysis;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Clears the framework collection's private List only. The caller must still
        // prove the receiver with ReviewedProviderMemberIsClosed.
        private static bool IsExactSqliteParameterCollectionClear(IMethodSymbol method) =>
            method is
            {
                Name: "Clear",
                MethodKind: MethodKind.Ordinary,
                IsStatic: false,
                Arity: 0,
                ReturnsVoid: true,
                Parameters.Length: 0,
                DeclaredAccessibility: Accessibility.Public,
            }
            && IsExactFrameworkType(method.ContainingType, "Microsoft.Data.Sqlite.SqliteParameterCollection",
                typeof(Microsoft.Data.Sqlite.SqliteParameterCollection).Assembly.GetName());
    }
}
