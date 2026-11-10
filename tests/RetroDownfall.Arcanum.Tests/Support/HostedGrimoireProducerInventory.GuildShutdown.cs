using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // The framework base implementation only cancels the owned stopping token. Like the
        // separately reviewed StartAsync/StopAsync calls, it creates no new producer. Authored
        // overrides still enter ordinary source traversal; a virtual receiver is not this rule.
        private static bool IsReviewedBackgroundServiceBaseCleanup(IMethodSymbol method, SyntaxNode? node) =>
            method is { Name: "Dispose", IsStatic: false, ReturnsVoid: true, Parameters.Length: 0 }
            && IsExactFrameworkType(method.ContainingType, "Microsoft.Extensions.Hosting.BackgroundService",
                typeof(Microsoft.Extensions.Hosting.BackgroundService).Assembly.GetName())
            && node is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax }
            };
    }
}
