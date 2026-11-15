using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // These exact getters read framework response state. This predicate alone grants
        // neither provider-result provenance nor resource/transport ownership.
        private static bool IsExactHttpResponseStatusProperty(IPropertySymbol property) =>
            property is { IsStatic: false, IsIndexer: false, GetMethod.IsVirtual: false }
            && property.Name is "StatusCode" or "IsSuccessStatusCode"
            && IsExactFrameworkType(property.ContainingType, "System.Net.Http.HttpResponseMessage",
                typeof(HttpResponseMessage).Assembly.GetName());

        private static bool IsReadOnlyHttpResponseStatusUse(IPropertySymbol property,
            MemberAccessExpressionSyntax access) =>
            IsExactHttpResponseStatusProperty(property)
            && !access.AncestorsAndSelf().TakeWhile(static node => node is not StatementSyntax)
                .Any(node => node switch
                {
                    AssignmentExpressionSyntax assignment => assignment.Left.Span.Contains(access.Span),
                    PrefixUnaryExpressionSyntax prefix =>
                        (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression))
                        && prefix.Operand.Span.Contains(access.Span),
                    PostfixUnaryExpressionSyntax postfix =>
                        (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression))
                        && postfix.Operand.Span.Contains(access.Span),
                    _ => false,
                });
    }
}
