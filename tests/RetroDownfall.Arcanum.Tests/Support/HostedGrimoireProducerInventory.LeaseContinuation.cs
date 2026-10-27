using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // On the path that reaches the Value use, a native null test's terminating
        // body did not execute. Keep its condition and all other statements in the
        // ordinary mutation proof. This affects stability only: traversal still
        // inventories the body's callbacks and effects under their original scope.
        private static bool SymbolIsStableAfterResultFailureGuard(
            AuthoredMember member,
            ILocalSymbol result,
            int after,
            ExpressionSyntax use,
            BlockSyntax block)
        {
            if (SymbolIsStableBetween(member, result, after, use))
            {
                return true;
            }

            IfStatementSyntax[] nullGuards = [.. block.Statements.OfType<IfStatementSyntax>().Where(guard =>
                guard.SpanStart >= after && guard.Span.End <= use.SpanStart
                && guard.Else is null
                && guard.Condition is IsPatternExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax receiver,
                        Name.Identifier.ValueText: "Value",
                    },
                    Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax literal },
                }
                && literal.IsKind(SyntaxKind.NullLiteralExpression)
                && SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(receiver).Symbol, result)
                && DefinitelyTerminates(guard.Statement))];

            // One bounded continuation split; more complex control flow retains the
            // existing conservative answer rather than opening a reachability search.
            return nullGuards is [IfStatementSyntax nullGuard]
                && SymbolIsStableWithin(member, result, after, nullGuard.Statement.SpanStart)
                && SymbolIsStableWithin(member, result, nullGuard.Statement.Span.End, use.SpanStart);
        }
    }
}
