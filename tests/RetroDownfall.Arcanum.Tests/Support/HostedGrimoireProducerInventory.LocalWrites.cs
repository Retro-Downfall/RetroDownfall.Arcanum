using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    // What can write a local. The recovery evaluator knows a local as its initializer and the plain assignments before the use;
    // that is the local's value only when nothing else writes it. A write through a reference (an out, ref or in argument, a
    // ref local, an address, Interlocked), a compound or coalescing assignment, a deconstruction, an increment, a write in a
    // local function (which may run before it is declared) or a write that a loop or a jump carries back to the use is a write
    // the evaluator does not model, so the local is then not a known value at all. This only removes knowledge, never adds it.
    private sealed partial class ProducerGraph
    {
        private sealed record RecoveryLocalWrites(bool Opaque, bool Jumps, IReadOnlyList<AssignmentExpressionSyntax> Assignments);

        private readonly Dictionary<ISymbol, RecoveryLocalWrites> recoveryLocalWrites = new(SymbolEqualityComparer.Default);

        // True when the plain assignments listed are all the writes the local has that can reach this use.
        private bool RecoveryLocalAssignments(
            AuthoredMember member,
            ILocalSymbol local,
            VariableDeclaratorSyntax declarator,
            SyntaxNode scope,
            ExpressionSyntax use,
            out IReadOnlyList<AssignmentExpressionSyntax> assignments)
        {
            RecoveryLocalWrites writes = RecoveryLocalWritesOf(member, local, declarator, scope);

            assignments = writes.Assignments;

            if (writes.Opaque)
            {
                return false;
            }

            if (writes.Assignments.Count == 0)
            {
                return true;
            }

            // A use inside a lambda or local function sees the variable when the callable runs, not where the text writes it.
            if (use.Ancestors().Any(ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax
                && !ancestor.Span.Contains(declarator.Span)))
            {
                return false;
            }

            // A write after the use in the text reaches the use only around a loop or by a jump.
            foreach (AssignmentExpressionSyntax assignment in writes.Assignments)
            {
                if (assignment.SpanStart < use.SpanStart)
                {
                    continue;
                }

                if (writes.Jumps
                    || use.Ancestors().Any(ancestor => ancestor is ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                        && scope.Span.Contains(ancestor.Span)
                        && ancestor.Span.Contains(assignment.Span)))
                {
                    return false;
                }
            }

            return true;
        }

        private RecoveryLocalWrites RecoveryLocalWritesOf(AuthoredMember member, ILocalSymbol local, VariableDeclaratorSyntax declarator, SyntaxNode scope)
        {
            if (recoveryLocalWrites.TryGetValue(local, out RecoveryLocalWrites? cached))
            {
                return cached;
            }

            SemanticModel model = member.Model;

            bool opaque = scope.SyntaxTree != model.SyntaxTree;

            bool jumps = false;

            List<AssignmentExpressionSyntax> assignments = [];

            IEnumerable<SyntaxNode> nodes = opaque ? Enumerable.Empty<SyntaxNode>() : scope.DescendantNodesAndSelf();

            foreach (SyntaxNode node in nodes)
            {
                if (node is GotoStatementSyntax or LabeledStatementSyntax)
                {
                    jumps = true;
                }

                if (node is AssignmentExpressionSyntax assignment && AssignmentTargetWritesSymbol(model, assignment.Left, local))
                {
                    // A plain assignment to the bare name outside any local function is the one write the evaluator models.
                    if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        && StripParentheses(assignment.Left) is IdentifierNameSyntax
                        && !assignment.Ancestors().Any(ancestor => ancestor is LocalFunctionStatementSyntax && !ancestor.Span.Contains(declarator.Span)))
                    {
                        assignments.Add(assignment);
                    }
                    else
                    {
                        opaque = true;
                    }
                }
                else if (node is not AssignmentExpressionSyntax && NodeWritesSymbolThroughReference(model, node, local))
                {
                    opaque = true;
                }
                else if (node is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression } prefix
                    && SyntaxReferencesSymbol(model, prefix.Operand, local)
                    || node is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression or (int)SyntaxKind.PostDecrementExpression } postfix
                    && SyntaxReferencesSymbol(model, postfix.Operand, local))
                {
                    opaque = true;
                }

                if (opaque)
                {
                    break;
                }
            }

            RecoveryLocalWrites writes = new(opaque, jumps, assignments);

            recoveryLocalWrites[local] = writes;

            return writes;
        }

        private static ExpressionSyntax StripParentheses(ExpressionSyntax expression) =>
            expression is ParenthesizedExpressionSyntax parenthesized ? StripParentheses(parenthesized.Expression) : expression;

        // True when the node hands the symbol out by reference: a ref, out or in argument (written or implicit), a ref expression or
        // an address. The callee, or whatever it reaches through the reference, may then overwrite the variable.
        private static bool NodeWritesSymbolThroughReference(SemanticModel model, SyntaxNode node, ISymbol symbol)
        {
            switch (node)
            {
                case ArgumentSyntax argument:
                    return MentionsName(argument.Expression, symbol.Name)
                        && ArgumentIsByReference(model, argument)
                        && SyntaxReferencesSymbol(model, argument.Expression, symbol);
                case RefExpressionSyntax reference:
                    return MentionsName(reference.Expression, symbol.Name)
                        && SyntaxReferencesSymbol(model, reference.Expression, symbol);
                case PrefixUnaryExpressionSyntax address when address.IsKind(SyntaxKind.AddressOfExpression):
                    return MentionsName(address.Operand, symbol.Name)
                        && SyntaxReferencesSymbol(model, address.Operand, symbol);
                default:
                    return false;
            }
        }

        // A cheap syntactic filter in front of the semantic queries: a reference to the symbol spells its name.
        private static bool MentionsName(SyntaxNode node, string name) =>
            node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(identifier => identifier.Identifier.ValueText == name);

        private static bool ArgumentIsByReference(SemanticModel model, ArgumentSyntax argument)
        {
            if (argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword)
            {
                return true;
            }

            // An implicit in or ref readonly argument carries no keyword; the called parameter's reference kind says it.
            if (argument.Parent?.Parent is not { } call)
            {
                return false;
            }

            IEnumerable<IArgumentOperation>? arguments = model.GetOperation(call) switch
            {
                IInvocationOperation invocation => invocation.Arguments,
                IObjectCreationOperation creation => creation.Arguments,
                IPropertyReferenceOperation property => property.Arguments,
                null => null,
                _ => [],
            };

            // A call the operation tree cannot show is treated as passing by reference.
            return arguments is null
                || arguments.Any(candidate => (candidate.Syntax == argument || candidate.Value.Syntax == argument.Expression)
                    && candidate.Parameter is { RefKind: not RefKind.None });
        }
    }
}
