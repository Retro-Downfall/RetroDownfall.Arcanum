using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private static bool IsExactConfiguredAsyncSequenceType(ITypeSymbol? type) =>
            IsExactFrameworkType(type, "System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable`1",
                typeof(System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable<>).Assembly.GetName());

        // These exact members store an enumerable/token/continuation flag; none invokes its
        // enumerator. The later enumeration still owes the underlying source's proof.
        private static bool IsExactAsyncSequenceWrapperMethod(IMethodSymbol method)
        {
            IMethodSymbol definition = (method.ReducedFrom ?? method).OriginalDefinition;

            if (definition.Parameters.Any(static parameter => parameter.RefKind != RefKind.None)
                || !IsExactConfiguredAsyncSequenceType(definition.ReturnType)
                || definition.IsVirtual || definition.IsAbstract)
            {
                return false;
            }

            bool FlagOrToken(ITypeSymbol type) => definition.Name switch
            {
                "ConfigureAwait" => type.SpecialType == SpecialType.System_Boolean && HasExactSpecialTypeIdentity(type),
                "WithCancellation" => IsExactCancellationToken(type),
                _ => false,
            };

            if (definition.IsStatic)
            {
                return definition.Arity == 1
                    && IsExactFrameworkType(definition.ContainingType, "System.Threading.Tasks.TaskAsyncEnumerableExtensions",
                        typeof(System.Threading.Tasks.TaskAsyncEnumerableExtensions).Assembly.GetName())
                    && definition.Parameters is [{ Type: INamedTypeSymbol { TypeArguments: [var element] } sequence }, { Type: { } option }]
                    && IsExactFrameworkType(sequence, "System.Collections.Generic.IAsyncEnumerable`1", typeof(IAsyncEnumerable<>).Assembly.GetName())
                    && definition.TypeParameters is [var parameter]
                    && SymbolEqualityComparer.Default.Equals(element, parameter)
                    && definition.ReturnType is INamedTypeSymbol { TypeArguments: [var returned] }
                    && SymbolEqualityComparer.Default.Equals(returned, parameter)
                    && FlagOrToken(option);
            }

            return definition.Arity == 0 && IsExactConfiguredAsyncSequenceType(definition.ContainingType)
                && definition.Parameters is [{ Type: { } value }]
                && SymbolEqualityComparer.Default.Equals(definition.ReturnType, definition.ContainingType)
                && FlagOrToken(value);
        }

        private static bool TryExactAsyncSequenceWrapperSource(IMethodSymbol method, SyntaxNode? node, SemanticModel? model,
            out ExpressionSyntax source)
        {
            source = null!;

            if (!IsExactAsyncSequenceWrapperMethod(method) || node is not InvocationExpressionSyntax call
                || model?.GetOperation(call) is not IInvocationOperation invocation
                || !invocation.Arguments.All(ArgumentBoundaryUsesOnlyBuiltInConversions)) return false;

            if ((!method.IsStatic || method.ReducedFrom is not null)
                && call.Expression is MemberAccessExpressionSyntax { Expression: { } receiver })
            {
                source = receiver;

                return true;
            }

            if (call.ArgumentList.Arguments.FirstOrDefault()?.Expression is not { } argument) return false;

            source = argument;

            return true;
        }

        private static bool IsReviewedAsyncSequenceWrapperOperation(IMethodSymbol method, SyntaxNode? node, SemanticModel? model) =>
            TryExactAsyncSequenceWrapperSource(method, node, model, out _);
    }
}
