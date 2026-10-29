using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    internal static bool ProbeSchemaReadinessSupport(CSharpCompilation compilation, SyntaxNode node, bool mutation) =>
        new ProducerGraph([compilation], DefaultEvaluationEnvironmentMaximumMembers, DefaultEvaluationEnvironmentMaximumDepth,
            DefaultTraversalMaximumDepth, DefaultMaximumAnalyzedStatesPerRoot, false, sitesOnly: true)
            .ProbeSchemaReadinessSupport(compilation.GetSemanticModel(node.SyntaxTree), node, mutation);

    private sealed partial class ProducerGraph
    {
        // The pinned provider getter returns its stored string. The setter performs pool
        // lookup and virtual State dispatch and is excluded. Receiver closure remains a
        // separate mandatory check at RequiresExternalBoundaryClassification.
        private static bool IsExactSqliteConnectionStringRead(IPropertySymbol property, bool mutation) =>
            !mutation && !property.IsStatic && !property.IsIndexer && property.Parameters.IsEmpty
            && property.Name == "ConnectionString" && property.Type.SpecialType == SpecialType.System_String
            && property.GetMethod is { ReturnsByRef: false, ReturnsByRefReadonly: false, Parameters.Length: 0 }
            && IsExactFrameworkType(property.ContainingType, "Microsoft.Data.Sqlite.SqliteConnection", typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName());

        // Only this nongeneric Int32 overload has no authored comparison or selector.
        // Existing eager enumeration still owns source execution, callbacks and cleanup.
        private static bool IsExactInt32MaxTerminal(IMethodSymbol method) =>
            method is
            {
                IsStatic: true, Name: "Max", Arity: 0, MethodKind: MethodKind.Ordinary,
                ReturnsByRef: false, ReturnsByRefReadonly: false, ReturnType.SpecialType: SpecialType.System_Int32
            }
            && IsExactFrameworkType(method.ContainingType, "System.Linq.Enumerable", typeof(Enumerable).Assembly.GetName())
            && method.Parameters is [
            {
                RefKind: RefKind.None, Type: INamedTypeSymbol
                { TypeArguments: [{ SpecialType: SpecialType.System_Int32 }] } source
            }]
            && IsExactFrameworkType(source, "System.Collections.Generic.IEnumerable`1", typeof(IEnumerable<>).Assembly.GetName());

        internal bool ProbeSchemaReadinessSupport(SemanticModel model, SyntaxNode node, bool mutation) =>
            model.GetSymbolInfo(node).Symbol switch
            {
                IPropertySymbol property => IsReviewedDatabaseSupportMember(property, mutation),
                IMethodSymbol method => IsReviewedSequenceOperation(method, node, model),
                _ => false,
            };
    }
}
