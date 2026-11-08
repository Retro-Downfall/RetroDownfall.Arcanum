using Microsoft.CodeAnalysis;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Stream's protected parameterless framework constructor initializes in-memory state. The derived authored
        // constructor body is still traversed, and every physical stream constructor retains its producer classification.
        private static bool IsExactFrameworkStreamBaseConstructor(IMethodSymbol method) =>
            method is
            {
                MethodKind: MethodKind.Constructor,
                IsStatic: false,
                Arity: 0,
                Parameters.Length: 0,
                DeclaredAccessibility: Accessibility.Protected,
            }
            && IsExactFrameworkType(method.ContainingType, "System.IO.Stream", typeof(Stream).Assembly.GetName());

        // A parameter count reads provider-owned collection state only when the existing provider receiver closure
        // proves which implementation runs. This profile is used solely by the database support-member lane, whose
        // caller independently applies ReviewedProviderMemberIsClosed; an abstract static type alone is insufficient.
        private static bool IsExactParameterCollectionCount(IPropertySymbol property, bool mutation) =>
            !mutation
            && property is
            {
                Name: "Count",
                IsStatic: false,
                IsIndexer: false,
                RefKind: RefKind.None,
                Type.SpecialType: SpecialType.System_Int32,
                Parameters.Length: 0,
                GetMethod: { DeclaredAccessibility: Accessibility.Public, IsStatic: false, Arity: 0, Parameters.Length: 0 },
                SetMethod: null,
            }
            && (IsExactFrameworkType(property.ContainingType, "System.Data.Common.DbParameterCollection",
                    typeof(System.Data.Common.DbParameterCollection).Assembly.GetName())
                || IsExactFrameworkType(property.ContainingType, "Microsoft.Data.Sqlite.SqliteParameterCollection",
                    typeof(Microsoft.Data.Sqlite.SqliteParameterCollection).Assembly.GetName()));
    }
}
