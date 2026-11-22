using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // A nullable local may carry one task across loop iterations. Overwriting that local
        // is safe only after observing the completed previous value, and the unconditional
        // finally must join whichever value remains. Refuse aliases and all other slot uses.
        private static SyntaxNode? ExactLoopRetainedTaskCompletion(AuthoredMember member, SyntaxNode source)
        {
            if (source.Parent is not AssignmentExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                    Left: { } destination,
                    Right: { } assigned,
                    Parent: ExpressionStatementSyntax assignment,
                }
                || assigned != source
                || member.Model.GetSymbolInfo(destination).Symbol is not ILocalSymbol task
                || !IsExactFrameworkType(task.Type, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName())
                || task.DeclaringSyntaxReferences is not [SyntaxReference declaration]
                || declaration.GetSyntax() is not VariableDeclaratorSyntax
                {
                    Initializer.Value.RawKind: (int)SyntaxKind.NullLiteralExpression,
                    Parent.Parent: LocalDeclarationStatementSyntax local,
                }
                || assignment.Parent is not BlockSyntax
                {
                    Statements: [IfStatementSyntax observe, ExpressionStatementSyntax write],
                    Parent: IfStatementSyntax guard,
                }
                || write != assignment || guard.Else is not null
                || guard.Condition is not BinaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.LogicalOrExpression,
                    Left: IsPatternExpressionSyntax
                    {
                        Expression: { } empty,
                        Pattern: ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression },
                    },
                    Right: MemberAccessExpressionSyntax { Expression: { } ready } completed,
                }
                || member.Model.GetSymbolInfo(completed).Symbol is not IPropertySymbol { Name: "IsCompleted", ContainingType: { } owner }
                || !IsExactFrameworkType(owner, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName())
                || guard.Parent is not BlockSyntax { Parent: WhileStatementSyntax loop } loopBody
                || loopBody.Statements.LastOrDefault() != guard
                || loop.Parent is not BlockSyntax { Statements.Count: 1, Parent: TryStatementSyntax lifetime }
                || lifetime.Parent is not BlockSyntax scope || local.Parent != scope
                || scope.Statements.IndexOf(lifetime) != scope.Statements.IndexOf(local) + 1
                || lifetime.Finally?.Block.Statements is not [IfStatementSyntax drain]
                || ConditionalLoopTaskJoin(member, observe, task) is not { } previous
                || ConditionalLoopTaskJoin(member, drain, task) is not { } last)
                return null;

            return HasOnlyReferences(member, task, destination, empty, ready, previous.Tested, previous.Observed, last.Tested, last.Observed)
                ? last.Join : null;
        }

        private static (ExpressionSyntax Tested, ExpressionSyntax Observed, AwaitExpressionSyntax Join)? ConditionalLoopTaskJoin(
            AuthoredMember member, IfStatementSyntax conditional, ILocalSymbol task)
        {
            if (conditional.Else is not null
                || conditional.Condition is not IsPatternExpressionSyntax
                {
                    Expression: { } tested,
                    Pattern: UnaryPatternSyntax
                    {
                        RawKind: (int)SyntaxKind.NotPattern,
                        Pattern: ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression },
                    },
                }
                || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(tested).Symbol, task)
                || conditional.Statement is not BlockSyntax
                {
                    Statements: [ExpressionStatementSyntax { Expression: AwaitExpressionSyntax joined }],
                }) return null;

            IdentifierNameSyntax[] references = [.. joined.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Where(reference => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(reference).Symbol, task))];

            return references is [IdentifierNameSyntax reference] && IsPrunedTaskDrainAwait(member, reference, joined)
                ? (tested, reference, joined) : null;
        }
    }
}
