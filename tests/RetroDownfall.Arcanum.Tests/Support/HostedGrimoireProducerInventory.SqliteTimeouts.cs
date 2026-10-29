using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // The pinned provider stores these scalar settings. Receiver dispatch still goes
        // through ReviewedProviderMemberIsClosed. CommandTimeout reads are deliberately
        // absent: their fallback can call the connection's virtual DefaultTimeout getter.
        private static bool IsExactSqliteTimeoutSetting(IPropertySymbol property, bool mutation) =>
            !property.IsStatic && !property.IsIndexer && property.Parameters.IsEmpty
            && property.Type.SpecialType == SpecialType.System_Int32
            && property.GetMethod is not null && property.SetMethod is not null
            && (property.Name == "DefaultTimeout"
                && IsExactFrameworkType(property.ContainingType, "Microsoft.Data.Sqlite.SqliteConnection", typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName())
                || mutation && property.Name == "CommandTimeout"
                && (IsExactFrameworkType(property.ContainingType, "Microsoft.Data.Sqlite.SqliteCommand", typeof(Microsoft.Data.Sqlite.SqliteCommand).Assembly.GetName())
                    || IsExactFrameworkType(property.ContainingType, "System.Data.Common.DbCommand", typeof(System.Data.Common.DbCommand).Assembly.GetName())));

        // This changes native connection state, so it is a database site, never a pure
        // support call. Normal traversal still evaluates its arguments and Work lifetime.
        private static bool IsExactSqliteBusyTimeout(IMethodSymbol method) =>
            method is { IsStatic: true, Arity: 0, Name: "sqlite3_busy_timeout", MethodKind: MethodKind.Ordinary }
            && method.ReturnType.SpecialType == SpecialType.System_Int32
            && method.Parameters is [{ RefKind: RefKind.None } handle, { RefKind: RefKind.None, Type.SpecialType: SpecialType.System_Int32 }]
            && IsExactFrameworkType(method.ContainingType, "SQLitePCL.raw", typeof(SQLitePCL.raw).Assembly.GetName())
            && IsExactFrameworkType(handle.Type, "SQLitePCL.sqlite3", typeof(SQLitePCL.sqlite3).Assembly.GetName());

        // Component.Dispose dispatches Dispose(bool) and then Disposed handlers. An exact
        // runtime type alone therefore cannot certify it. Only a fresh, nonescaping command
        // with these reviewed uses can have no handler attached before its compiler cleanup.
        private bool IsExactLocalSqliteCommandCleanup(AuthoredMember member, ExpressionSyntax? resource, ITypeSymbol exactType, IMethodSymbol cleanup)
        {
            if (!IsExactFrameworkType(exactType, "Microsoft.Data.Sqlite.SqliteCommand", typeof(Microsoft.Data.Sqlite.SqliteCommand).Assembly.GetName())
                || cleanup is not { IsStatic: false, Name: "Dispose", Parameters.Length: 0, ReturnsVoid: true }
                || !IsExactFrameworkType(cleanup.ContainingType, "System.ComponentModel.Component", typeof(System.ComponentModel.Component).Assembly.GetName())
                || resource is not InvocationExpressionSyntax factory
                || factory.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable }
                || variable.Parent is not VariableDeclarationSyntax { Variables.Count: 1, Parent: LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } }
                || member.Model.GetDeclaredSymbol(variable) is not ILocalSymbol local
                || member.Model.GetSymbolInfo(factory).Symbol is not IMethodSymbol factoryMethod
                || !IsExactSqliteCommandFactorySlot(factoryMethod)) return false;

            CleanupProvenanceContext proof = new();

            CleanupProvenance? origin = ExactTrustedSqliteFactoryProvenance(member, factory, factoryMethod, proof);

            if (origin is not { Complete: true, Candidates: [CleanupTypeCandidate candidate] }
                || !IsExactFrameworkType(candidate.Type, "Microsoft.Data.Sqlite.SqliteCommand", typeof(Microsoft.Data.Sqlite.SqliteCommand).Assembly.GetName())) return false;

            foreach (IdentifierNameSyntax use in PerUnitVisited(member.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>(), proof))
            {
                if (!SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(use).Symbol, local)) continue;

                if (use.Ancestors().TakeWhile(ancestor => ancestor != member.Syntax).Any(static ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                    || use.Parent is not MemberAccessExpressionSyntax access || access.Expression != use) return false;

                ISymbol? used = member.Model.GetSymbolInfo(access).Symbol;

                if (used is IPropertySymbol property
                    && access.Parent is AssignmentExpressionSyntax assignment && assignment.Left == access && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && property.Name is "CommandText" or "CommandTimeout"
                    && (IsExactFrameworkType(property.ContainingType, "Microsoft.Data.Sqlite.SqliteCommand", typeof(Microsoft.Data.Sqlite.SqliteCommand).Assembly.GetName())
                        || IsExactFrameworkType(property.ContainingType, "System.Data.Common.DbCommand", typeof(System.Data.Common.DbCommand).Assembly.GetName()))) continue;

                if (used is IMethodSymbol { IsStatic: false, Name: "ExecuteScalar", Arity: 0, Parameters.Length: 0 } execute
                    && access.Parent is InvocationExpressionSyntax call && call.Expression == access
                    && (IsExactFrameworkType(execute.ContainingType, "Microsoft.Data.Sqlite.SqliteCommand", typeof(Microsoft.Data.Sqlite.SqliteCommand).Assembly.GetName())
                        || IsExactFrameworkType(execute.ContainingType, "System.Data.Common.DbCommand", typeof(System.Data.Common.DbCommand).Assembly.GetName()))) continue;

                return false;
            }

            return proof.RemainingWork > 0;
        }
    }
}
