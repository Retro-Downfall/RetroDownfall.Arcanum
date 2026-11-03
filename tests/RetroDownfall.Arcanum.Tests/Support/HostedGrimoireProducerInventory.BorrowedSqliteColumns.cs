using Microsoft.CodeAnalysis;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // These exact native cursor reads can fetch authoritative payload pages. They are database
        // effects with the ordinary Work requirement, never pure helpers or a general native grant.
        private static bool IsExactBorrowedSqliteColumnRead(IMethodSymbol method)
        {
            if (method is not
                {
                    IsStatic: true,
                    Arity: 0,
                    MethodKind: MethodKind.Ordinary,
                    DeclaredAccessibility: Accessibility.Public,
                    RefKind: RefKind.None,
                    Parameters: [{ RefKind: RefKind.None } statement, { RefKind: RefKind.None, Type.SpecialType: SpecialType.System_Int32 }],
                }
                || method.Locations.Any(static location => location.IsInSource)
                || !IsExactFrameworkType(method.ContainingType, "SQLitePCL.raw", typeof(SQLitePCL.raw).Assembly.GetName())
                || !IsExactFrameworkType(statement.Type, "SQLitePCL.sqlite3_stmt", typeof(SQLitePCL.sqlite3_stmt).Assembly.GetName()))
            {
                return false;
            }

            return method.Name == "sqlite3_column_type" && method.ReturnType.SpecialType == SpecialType.System_Int32
                || method.Name == "sqlite3_column_blob"
                    && method.ReturnType is INamedTypeSymbol { Arity: 1, TypeArguments: [{ SpecialType: SpecialType.System_Byte }] }
                    && IsExactFrameworkType(method.ReturnType, "System.ReadOnlySpan`1", typeof(ReadOnlySpan<>).Assembly.GetName());
        }

        private static bool IsExactBorrowedSqliteStatementHandle(IPropertySymbol property, bool mutation) =>
            !mutation && property is
            {
                Name: "Handle",
                IsStatic: false,
                IsIndexer: false,
                RefKind: RefKind.None,
                Parameters.IsEmpty: true,
                GetMethod.DeclaredAccessibility: Accessibility.Public,
                SetMethod: null,
            }
            && !property.Locations.Any(static location => location.IsInSource)
            && IsExactFrameworkType(property.ContainingType, "Microsoft.Data.Sqlite.SqliteDataReader", typeof(Microsoft.Data.Sqlite.SqliteDataReader).Assembly.GetName())
            && IsExactFrameworkType(property.Type, "SQLitePCL.sqlite3_stmt", typeof(SQLitePCL.sqlite3_stmt).Assembly.GetName());
    }
}
