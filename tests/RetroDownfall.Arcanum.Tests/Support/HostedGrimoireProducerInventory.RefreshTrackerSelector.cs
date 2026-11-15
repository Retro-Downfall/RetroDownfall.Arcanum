using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private const string RefreshTrackerType = "RetroDownfall.Arcanum.Infrastructure.Guild.GuildSpendRefreshTracker";

        private const string ReviewedRefreshTrackerSourceHash = "006d091deb7015f191be1616231cc4efffb2f31f4a3d7f8b3a83a3d8c7b1b5b5";

        private readonly Dictionary<Compilation, bool> reviewedRefreshTrackerSelectors = [];

        // This is a closed source invariant, not a generic synchronous Task selector rule.
        // Only TryEnter populates the private sets, with fresh nonnull Entry values. Entry's
        // readonly CTS is never disposed/escaped; its readonly TCS always publishes a Task.
        // The private drain receives only snapshots from those sets. All five selectors
        // in the three reviewed consumers therefore publish a nonnull Task without a
        // synchronous selector failure. A changed body or binding loses this certificate.
        private bool IsReviewedRefreshTrackerSelector(AuthoredMember owner,
            InvocationExpressionSyntax sequence, AuthoredMember callback)
        {
            if (TypeKey(owner.Symbol.ContainingType) != RefreshTrackerType
                || owner.Symbol.ContainingAssembly.Name != "RetroDownfall.Arcanum.Infrastructure"
                || owner.Symbol.Name is not ("CloseRootsScopedAsync" or "CancelOperationAsync" or "CancelAndJoinAsync")
                || callback.Model.Compilation != owner.Model.Compilation
                || callback.Syntax.SyntaxTree != owner.Syntax.SyntaxTree
                || !owner.Syntax.Span.Contains(callback.Syntax.Span)
                || sequence.ArgumentList.Arguments.Last().Expression is not AnonymousFunctionExpressionSyntax lambda
                || lambda.Body != callback.Syntax)
            {
                return false;
            }

            Compilation compilation = owner.Model.Compilation;

            if (reviewedRefreshTrackerSelectors.TryGetValue(compilation, out bool known)) return known;

            bool complete = ProveRefreshTrackerSelectors(compilation);

            reviewedRefreshTrackerSelectors[compilation] = complete;

            return complete;
        }

        private bool ProveRefreshTrackerSelectors(Compilation compilation)
        {
            if (compilation.AssemblyName != "RetroDownfall.Arcanum.Infrastructure"
                || compilation.GetTypeByMetadataName(RefreshTrackerType) is not { IsSealed: true } tracker
                || !SymbolEqualityComparer.Default.Equals(tracker.ContainingAssembly, compilation.Assembly)
                || tracker.DeclaringSyntaxReferences is not [SyntaxReference reference]
                || reference.GetSyntax() is not ClassDeclarationSyntax declaration
                || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(declaration.SyntaxTree.GetText().ToString())))
                    != ReviewedRefreshTrackerSourceHash.ToUpperInvariant()) return false;

            SemanticModel model = compilation.GetSemanticModel(declaration.SyntaxTree);

            System.Reflection.AssemblyName collectionsContract = System.Reflection.Assembly.Load("System.Collections").GetName();

            System.Reflection.AssemblyName memoryContract = System.Reflection.Assembly.Load("System.Memory").GetName();

            HashSet<INamedTypeSymbol> owned = new(SymbolEqualityComparer.Default) { tracker };

            foreach (INamedTypeSymbol nested in tracker.GetTypeMembers()) owned.Add(nested);

            foreach (SyntaxNode syntax in declaration.DescendantNodesAndSelf())
            {
                if (syntax is not (NameSyntax or InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax
                    or BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                    or AssignmentExpressionSyntax or CastExpressionSyntax or AttributeSyntax)) continue;

                if (syntax is IdentifierNameSyntax identifier && model.GetAliasInfo(identifier) is not null) return false;

                if (model.GetOperation(syntax) is Microsoft.CodeAnalysis.Operations.INameOfOperation
                    || syntax is IdentifierNameSyntax { Parent: InvocationExpressionSyntax call } name
                        && call.Expression == name
                        && model.GetOperation(call) is Microsoft.CodeAnalysis.Operations.INameOfOperation) continue;

                SymbolInfo info = model.GetSymbolInfo(syntax);

                if (info.CandidateSymbols.Length != 0) return false;

                if (info.Symbol is not { } symbol)
                {
                    if (syntax is NameSyntax or InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax or AttributeSyntax) return false;

                    continue;
                }

                if (symbol is INamespaceSymbol or ILocalSymbol or IParameterSymbol or ILabelSymbol or ITypeParameterSymbol or IDiscardSymbol) continue;

                INamedTypeSymbol? type = symbol as INamedTypeSymbol ?? symbol.ContainingType;

                if (type is null) return false;

                if (owned.Contains(type.OriginalDefinition)) continue;

                if (IsExactFrameworkType(type, "System.Collections.Generic.CollectionExtensions",
                    collectionsContract)
                    || IsExactFrameworkType(type, "System.MemoryExtensions", memoryContract)) continue;

                Type? expected = TypeKey(type) switch
                {
                    "System.Object" => typeof(object),
                    "System.Boolean" => typeof(bool),
                    "System.Int32" => typeof(int),
                    "System.Guid" => typeof(Guid),
                    "System.Void" => typeof(void),
                    "System.Nullable`1" => typeof(Nullable<>),
                    "System.IDisposable" => typeof(IDisposable),
                    "System.Collections.Generic.Dictionary`2" => typeof(Dictionary<,>),
                    "System.Collections.Generic.HashSet`1" => typeof(HashSet<>),
                    "System.Collections.Generic.IReadOnlyCollection`1" => typeof(IReadOnlyCollection<>),
                    "System.Collections.Generic.IEnumerable`1" => typeof(IEnumerable<>),
                    "System.Linq.Enumerable" => typeof(Enumerable),
                    "System.Threading.Interlocked" => typeof(Interlocked),
                    "System.Threading.CancellationToken" => typeof(CancellationToken),
                    "System.Threading.CancellationTokenSource" => typeof(CancellationTokenSource),
                    "System.Threading.Tasks.Task" => typeof(Task),
                    "System.Threading.Tasks.Task`1" => typeof(Task<>),
                    "System.Threading.Tasks.TaskCompletionSource" => typeof(TaskCompletionSource),
                    "System.Threading.Tasks.TaskCreationOptions" => typeof(TaskCreationOptions),
                    "System.ArgumentOutOfRangeException" => typeof(ArgumentOutOfRangeException),
                    "System.InvalidOperationException" => typeof(InvalidOperationException),
                    _ => null,
                };

                if (expected is null || !IsExactFrameworkType(type, TypeKey(type), expected.Assembly.GetName())) return false;
            }

            return true;
        }
    }
}
