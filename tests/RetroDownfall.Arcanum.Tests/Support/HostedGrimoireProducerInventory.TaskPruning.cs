using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // A local task set may discard a completed member only after observing that exact task.
        // The containing try still joins every retained member in its unconditional finally.
        // No owner names, escaped collections, replacement writes or cancelable joins qualify.
        private SyntaxNode? ExactPrunedTaskCollectionCompletion(AuthoredMember member,
            InvocationExpressionSyntax source, IReadOnlyList<IdentifierNameSyntax> taskReferences)
        {
            if (taskReferences is not [IdentifierNameSyntax taskReference]
                || taskReference.Parent is not ArgumentSyntax { RefKindKeyword.RawKind: 0,
                    Parent.Parent: InvocationExpressionSyntax add }
                || add.ArgumentList.Arguments.Count != 1
                || add.Expression is not MemberAccessExpressionSyntax { Expression: { } addReceiver }
                || member.Model.GetSymbolInfo(add).Symbol is not IMethodSymbol { Name: "Add" } addMethod
                || member.Model.GetSymbolInfo(addReceiver).Symbol is not ILocalSymbol list
                || list.Type is not INamedTypeSymbol { TypeArguments: [ITypeSymbol element] } listType
                || !IsExactFrameworkType(listType.OriginalDefinition, "System.Collections.Generic.List`1", typeof(List<>).Assembly.GetName())
                || !IsExactFrameworkType(element, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName())
                || !SymbolEqualityComparer.Default.Equals(addMethod.ContainingType, listType)
                || list.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax
                {
                    Initializer.Value: BaseObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 0, Initializer: null },
                    Parent.Parent: LocalDeclarationStatementSyntax { Parent: BlockSyntax owner } listDeclaration,
                }
                || source.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() is not { Parent: BlockSyntax sourceBlock } sourceDeclaration
                || add.Parent is not ExpressionStatementSyntax addStatement
                || addStatement.Parent != sourceBlock
                || sourceBlock.Statements.IndexOf(addStatement) != sourceBlock.Statements.IndexOf(sourceDeclaration) + 1)
                return null;

            ExpressionSyntax[] references = SymbolReferences(member, list);

            if (references.Any(reference => reference.Ancestors().TakeWhile(node => node != member.Syntax)
                    .Any(static node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
                return null;

            InvocationExpressionSyntax[] aggregates = references.Select(reference => reference.Parent is ArgumentSyntax
                { Parent.Parent: InvocationExpressionSyntax invocation } ? invocation : null)
                .OfType<InvocationExpressionSyntax>()
                .Where(call => member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                    && Normalize(method) == "System.Threading.Tasks.Task.WhenAll"
                    && IsExactFrameworkType(method.ContainingType, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName()))
                .Distinct().ToArray();

            if (aggregates is not [InvocationExpressionSyntax aggregate]
                || aggregate.ArgumentList.Arguments is not [ArgumentSyntax { Expression: { } aggregateReference }]
                || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(aggregateReference).Symbol, list)
                || CompletionPreservingExpression(aggregate, member.Model).Parent is not AwaitExpressionSyntax finalJoin
                || !IsPrunedTaskDrainAwait(member, aggregate, finalJoin)
                || finalJoin.Parent is not ExpressionStatementSyntax finalStatement
                || finalStatement.Parent is not BlockSyntax { Statements.Count: 1, Parent: FinallyClauseSyntax finalizer }
                || finalizer.Parent is not TryStatementSyntax protectedLifetime
                || protectedLifetime.Parent != owner
                || listDeclaration.Span.End >= protectedLifetime.SpanStart
                || !protectedLifetime.Block.Span.Contains(source.Span)
                || !protectedLifetime.Block.Span.Contains(add.Span))
                return null;

            ElementAccessExpressionSyntax[] reads = references.Select(reference => reference.Parent)
                .OfType<ElementAccessExpressionSyntax>().ToArray();

            if (reads is not [ElementAccessExpressionSyntax read]
                || read.ArgumentList.Arguments is not [ArgumentSyntax { Expression: { } readIndex }]
                || read.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax completedDeclaration }
                || completedDeclaration.Parent?.Parent is not LocalDeclarationStatementSyntax completedStatement
                || completedStatement.Parent is not BlockSyntax
                {
                    Statements: [LocalDeclarationStatementSyntax first, IfStatementSyntax guard,
                        ExpressionStatementSyntax { Expression: AwaitExpressionSyntax completedJoin },
                        ExpressionStatementSyntax { Expression: InvocationExpressionSyntax remove }],
                    Parent: ForStatementSyntax pruning,
                }
                || first != completedStatement
                || !protectedLifetime.Block.Span.Contains(pruning.Span)
                || pruning.Declaration is not VariableDeclarationSyntax { Variables: [VariableDeclaratorSyntax indexDeclaration] }
                || member.Model.GetDeclaredSymbol(indexDeclaration) is not ILocalSymbol index
                || index.Type.SpecialType != SpecialType.System_Int32
                || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(readIndex).Symbol, index)
                || member.Model.GetDeclaredSymbol(completedDeclaration) is not ILocalSymbol completed
                || !IsExactFrameworkType(completed.Type, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName()))
                return null;

            if (indexDeclaration.Initializer?.Value is not BinaryExpressionSyntax countMinusOne
                || !countMinusOne.IsKind(SyntaxKind.SubtractExpression)
                || countMinusOne.Left is not MemberAccessExpressionSyntax { Expression: { } countReceiver } count
                || member.Model.GetSymbolInfo(count).Symbol is not IPropertySymbol { Name: "Count" } countProperty
                || !SymbolEqualityComparer.Default.Equals(countProperty.ContainingType, listType)
                || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(countReceiver).Symbol, list)
                || member.Model.GetConstantValue(countMinusOne.Right) is not { HasValue: true, Value: int one } || one != 1
                || pruning.Condition is not BinaryExpressionSyntax condition || !condition.IsKind(SyntaxKind.GreaterThanOrEqualExpression)
                || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(condition.Left).Symbol, index)
                || member.Model.GetConstantValue(condition.Right) is not { HasValue: true, Value: int zero } || zero != 0
                || pruning.Incrementors is not [PostfixUnaryExpressionSyntax decrement] || !decrement.IsKind(SyntaxKind.PostDecrementExpression)
                || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(decrement.Operand).Symbol, index)
                || guard.Else is not null || guard.Statement is not ContinueStatementSyntax
                || guard.Condition is not PrefixUnaryExpressionSyntax notCompleted || !notCompleted.IsKind(SyntaxKind.LogicalNotExpression)
                || notCompleted.Operand is not MemberAccessExpressionSyntax { Expression: { } guardedTask } isCompleted
                || member.Model.GetSymbolInfo(isCompleted).Symbol is not IPropertySymbol { Name: "IsCompleted" } completedProperty
                || !IsExactFrameworkType(completedProperty.ContainingType, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName())
                || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(guardedTask).Symbol, completed)
                || remove.Expression is not MemberAccessExpressionSyntax { Expression: { } removeReceiver }
                || member.Model.GetSymbolInfo(remove).Symbol is not IMethodSymbol { Name: "RemoveAt" } removeMethod
                || !SymbolEqualityComparer.Default.Equals(removeMethod.ContainingType, listType)
                || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(removeReceiver).Symbol, list)
                || remove.ArgumentList.Arguments is not [ArgumentSyntax { RefKindKeyword.RawKind: 0, Expression: { } removeIndex }]
                || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(removeIndex).Symbol, index))
                return null;

            ExpressionSyntax[] completedReferences = SymbolReferences(member, completed);

            ExpressionSyntax[] joinedReferences = completedReferences.Where(reference =>
                IsPrunedTaskDrainAwait(member, reference, completedJoin)).ToArray();

            if (completedReferences.Length != 2 || joinedReferences is not [ExpressionSyntax joinedTask]
                || completedReferences.Any(reference => reference != guardedTask && reference != joinedTask))
                return null;

            ExpressionSyntax[] indexReferences = SymbolReferences(member, index);

            if (indexReferences.Length != 4 || indexReferences.Any(reference => reference != condition.Left
                    && reference != decrement.Operand && reference != readIndex && reference != removeIndex))
                return null;

            HashSet<ExpressionSyntax> retainedReferences = [addReceiver, aggregateReference, read.Expression, countReceiver, removeReceiver];

            return references.Length == retainedReferences.Count && references.All(retainedReferences.Contains) ? finalJoin : null;
        }

        private static bool IsPrunedTaskDrainAwait(AuthoredMember member, ExpressionSyntax task, AwaitExpressionSyntax awaited)
        {
            SyntaxNode expression = task;

            while (true)
            {
                if (expression.Parent is ParenthesizedExpressionSyntax parentheses)
                {
                    expression = parentheses;

                    continue;
                }

                if (expression.Parent is MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "ConfigureAwait",
                        Parent: InvocationExpressionSyntax configured,
                    }
                    && member.Model.GetSymbolInfo(configured).Symbol is IMethodSymbol { Name: "ConfigureAwait", IsStatic: false } method
                    && IsExactFrameworkType(method.ContainingType, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName())
                    && configured.ArgumentList.Arguments is [ArgumentSyntax argument]
                    && member.Model.GetConstantValue(argument.Expression) is { HasValue: true, Value: bool })
                {
                    expression = configured;

                    continue;
                }

                return expression.Parent == awaited && awaited.Expression == expression;
            }
        }
    }
}
