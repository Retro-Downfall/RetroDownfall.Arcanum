using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Select over a real array cannot dispatch an authored enumerator. Only the exact
        // all-task aggregate consumes every produced task; partial terminals grant no join.
        private SyntaxNode? ExactTaskArrayAggregateCompletion(
            AuthoredMember member,
            InvocationExpressionSyntax source,
            bool inheritedCompletion)
        {
            if (member.Model.GetSymbolInfo(source).Symbol is not IMethodSymbol selected
                || selected.Name != "Select"
                || !IsExactFrameworkType(selected.ContainingType, "System.Linq.Enumerable", typeof(Enumerable).Assembly.GetName())
                || selected.TypeArguments is not [_, ITypeSymbol result]
                || !IsExactFrameworkType(result, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName()))
            {
                return null;
            }

            ExpressionSyntax? array = selected.ReducedFrom is not null
                && source.Expression is MemberAccessExpressionSyntax access
                    ? access.Expression
                    : source.ArgumentList.Arguments.FirstOrDefault()?.Expression;

            if (array is null || member.Model.GetTypeInfo(array).Type is not IArrayTypeSymbol)
            {
                return null;
            }

            ExpressionSyntax selector = source.ArgumentList.Arguments.Last().Expression;

            AuthoredMember[] selectors = ResolveCallableTargets(selector, member.Model, member.CallableBindings, member.AbsentCallables);

            if (selectors is not [AuthoredMember callback]
                || !IsExactFrameworkType(callback.Symbol.ReturnType, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName())
                || !(HasStockAsyncPublication(callback)
                    || IsReviewedRefreshTrackerSelector(member, source, callback)))
            {
                return null;
            }

            // Async Task publication catches ordinary body failures in a nonnull task.
            // A synchronous selector may throw or return null after earlier children started.
            SyntaxNode operand = source;

            while (operand.Parent is ParenthesizedExpressionSyntax parenthesized)
            {
                operand = parenthesized;
            }

            if (operand.Parent is not ArgumentSyntax { RefKindKeyword.RawKind: 0 } argument
                || argument.Parent is not ArgumentListSyntax
                {
                    Arguments: [ArgumentSyntax only],
                    Parent: InvocationExpressionSyntax aggregate,
                }
                || only != argument
                || member.Model.GetSymbolInfo(aggregate).Symbol is not IMethodSymbol
                {
                    Name: "WhenAll", IsStatic: true, Arity: 0,
                    Parameters: [IParameterSymbol { RefKind: RefKind.None, Type: INamedTypeSymbol sequence }],
                } all
                || !IsExactFrameworkType(all.ContainingType, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName())
                || TypeKey(sequence) != "System.Collections.Generic.IEnumerable`1"
                || sequence.TypeArguments is not [ITypeSymbol element]
                || !IsExactFrameworkType(element, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName()))
            {
                return null;
            }

            SyntaxNode expression = aggregate;

            while (true)
            {
                if (expression.Parent is ParenthesizedExpressionSyntax parentheses)
                {
                    expression = parentheses;

                    continue;
                }

                if (expression.Parent is MemberAccessExpressionSyntax
                    {
                        Parent: InvocationExpressionSyntax continuation,
                    }
                    && member.Model.GetSymbolInfo(continuation).Symbol is IMethodSymbol method
                    && !method.IsStatic
                    && IsExactFrameworkType(method.ContainingType, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName())
                    && continuation.ArgumentList.Arguments is [ArgumentSyntax option]
                    && (method.Name == "ConfigureAwait"
                            && member.Model.GetConstantValue(option.Expression) is { HasValue: true, Value: bool }
                        || method.Name == "WaitAsync"
                            && IsExactNonCancelableToken(member, option.Expression)))
                {
                    expression = continuation;

                    continue;
                }

                break;
            }

            if (expression.Parent is AwaitExpressionSyntax awaited)
            {
                return awaited;
            }

            return inheritedCompletion && !member.Symbol.IsAsync
                && expression.Parent is ReturnStatementSyntax or ArrowExpressionClauseSyntax
                    ? expression
                    : null;
        }
    }
}
