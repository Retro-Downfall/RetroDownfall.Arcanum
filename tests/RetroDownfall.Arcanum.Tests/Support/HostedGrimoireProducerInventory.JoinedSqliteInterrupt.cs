using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Register may invoke its static callback before returning. Once it returns, exact
        // synchronous Dispose joins that callback. This proof only borrows the work already
        // live at both boundaries; it does not grant an external-effect frontier.
        private bool IsJoinedSqliteInterruptRegistration(AuthoredMember member, InvocationExpressionSyntax registration,
            IMethodSymbol method, string operationId, GrimoireWorkKind? kind, WorkAdmissionEvidence? inherited,
            WorkAdmissionEvidence? registeredWork)
        {
            if (registeredWork is null
                || method is not { IsStatic: false, Name: "Register", Arity: 0 }
                || !IsExactFrameworkType(method.ContainingType, "System.Threading.CancellationToken", typeof(CancellationToken).Assembly.GetName())
                || !IsExactFrameworkType(method.ReturnType, "System.Threading.CancellationTokenRegistration", typeof(CancellationTokenRegistration).Assembly.GetName())
                || registration.ArgumentList.Arguments is not [ArgumentSyntax callbackArgument, ArgumentSyntax stateArgument]
                || callbackArgument.Expression is not LambdaExpressionSyntax callback
                || !callback.Modifiers.Any(SyntaxKind.StaticKeyword)
                || callback.Modifiers.Any(SyntaxKind.AsyncKeyword)
                || callback is not SimpleLambdaExpressionSyntax { Body: InvocationExpressionSyntax interrupt } simple
                || member.Model.GetSymbolInfo(interrupt).Symbol is not IMethodSymbol interruptMethod
                || !IsExactSqliteInterrupt(interruptMethod)
                || interrupt.ArgumentList.Arguments is not [ArgumentSyntax interruptArgument]
                || interruptArgument.Expression is not CastExpressionSyntax cast
                || member.Model.GetOperation(cast) is not IConversionOperation { OperatorMethod: null }
                || cast.Expression is not PostfixUnaryExpressionSyntax suppression
                || !suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression)
                || suppression.Operand is not IdentifierNameSyntax parameterUse
                || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(parameterUse).Symbol, member.Model.GetDeclaredSymbol(simple.Parameter))
                || ResolveCallableTargets(callback, member.Model, member.CallableBindings, member.AbsentCallables).Length == 0
                || member.Model.GetTypeInfo(stateArgument.Expression).Type is not { } stateType
                || !IsExactFrameworkType(stateType, "SQLitePCL.sqlite3", typeof(SQLitePCL.sqlite3).Assembly.GetName())
                || registration.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable }
                || variable.Parent is not VariableDeclarationSyntax { Variables.Count: 1, Parent: LocalDeclarationStatementSyntax declaration }
                || declaration.UsingKeyword.RawKind != 0
                || declaration.Parent is not BlockSyntax block
                || member.Model.GetDeclaredSymbol(variable) is not ILocalSymbol local)
                return false;

            int index = block.Statements.IndexOf(declaration);

            if (index < 0) return false;

            StatementSyntax[] following = [.. block.Statements.Skip(index + 1)];

            int protectorIndex = Array.FindIndex(following, static statement => statement is TryStatementSyntax);

            if (protectorIndex < 0
                || following.Take(protectorIndex).Any(statement => statement is not LocalDeclarationStatementSyntax localDeclaration
                    || localDeclaration.UsingKeyword.RawKind != 0
                    || localDeclaration.Declaration.Variables.Any(item => item.Initializer is { } initializer
                        && (!(initializer.Value.IsKind(SyntaxKind.NullLiteralExpression) || initializer.Value.IsKind(SyntaxKind.DefaultLiteralExpression))
                            || member.Model.GetOperation(initializer) is not IVariableInitializerOperation operation
                            || operation.DescendantsAndSelf().OfType<IConversionOperation>().Any(static conversion => conversion.OperatorMethod is not null))))
                || following[protectorIndex] is not TryStatementSyntax { Finally: { } finalizer }
                || finalizer.Block.Statements.FirstOrDefault() is not ExpressionStatementSyntax
                {
                    Expression: InvocationExpressionSyntax cleanup,
                }
                || cleanup.Expression is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver }
                || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(receiver).Symbol, local)
                || member.Model.GetSymbolInfo(cleanup).Symbol is not IMethodSymbol cleanupMethod
                || cleanupMethod is not { IsStatic: false, Name: "Dispose", Parameters.Length: 0, ReturnsVoid: true }
                || !IsExactFrameworkType(cleanupMethod.ContainingType, "System.Threading.CancellationTokenRegistration", typeof(CancellationTokenRegistration).Assembly.GetName()))
                return false;

            IdentifierNameSyntax[] uses = [.. member.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(name => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(name).Symbol, local))];

            if (uses is not [IdentifierNameSyntax only] || only != receiver) return false;

            return WorkAt(member, cleanup, operationId, kind, inherited, cleanup.SpanStart) == registeredWork;
        }
    }
}
