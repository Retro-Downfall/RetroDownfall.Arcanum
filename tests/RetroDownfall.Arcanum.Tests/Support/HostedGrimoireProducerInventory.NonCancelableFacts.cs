using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private readonly Dictionary<(GraphMemberIdentity Member, int Parameter), bool> nonCancelableFactDemands = [];

        private static ExpressionSyntax CancellationFactOperand(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parentheses) expression = parentheses.Expression;

            return expression;
        }

        private readonly Dictionary<(GraphMemberIdentity Member, int Parameter), bool> stableOwnCancellationParameters = [];

        private bool IsStableOwnCancellationParameter(AuthoredMember member, IParameterSymbol parameter)
        {
            if (parameter.RefKind != RefKind.None || !IsExactCancellationToken(parameter.Type)
                || member.Symbol.MethodKind != MethodKind.Ordinary
                || parameter.ContainingSymbol is not IMethodSymbol owner
                || !SymbolEqualityComparer.Default.Equals(owner.OriginalDefinition, member.Symbol.OriginalDefinition)) return false;

            var key = (MemberIdentity(member), parameter.Ordinal);

            if (stableOwnCancellationParameters.TryGetValue(key, out bool known)) return known;

            bool stable = ProveStableOwnCancellationParameter(member, parameter);

            stableOwnCancellationParameters.Add(key, stable);

            return stable;
        }

        private static bool ProveStableOwnCancellationParameter(AuthoredMember member, IParameterSymbol parameter) =>
            !HasCollectionValueReplacement(member, parameter)
            && !member.Model.SyntaxTree.GetRoot().DescendantNodes().Any(node => node switch
            {
                ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.InKeyword) =>
                    MentionsCancellationParameter(member, argument.Expression, parameter),
                MakeRefExpressionSyntax reference => MentionsCancellationParameter(member, reference.Expression, parameter),
                _ => false,
            });

        private static bool MentionsCancellationParameter(AuthoredMember member, SyntaxNode node, IParameterSymbol parameter) =>
            node.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(expression =>
                SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(expression).Symbol, parameter));

        private bool IsExactNonCancelableToken(AuthoredMember member, ExpressionSyntax expression)
        {
            expression = CancellationFactOperand(expression);

            return member.Model.GetSymbolInfo(expression).Symbol switch
            {
                IPropertySymbol { Name: "None", IsStatic: true, IsIndexer: false, GetMethod.IsStatic: true } none =>
                    IsExactCancellationToken(none.ContainingType) && IsExactCancellationToken(none.Type),
                IParameterSymbol parameter => member.KnownNonCancelableParameters?.Contains(parameter) == true
                    && IsStableOwnCancellationParameter(member, parameter),
                _ => false,
            };
        }

        // This is a syntax-only demand, never supplier provenance. Only the root query is
        // cached: cutting a recursion edge is not a reusable negative for that subproblem.
        private bool RequiresNonCancelableParameterFact(AuthoredMember target, IParameterSymbol parameter)
        {
            if (!IsStableOwnCancellationParameter(target, parameter)) return false;

            var key = (MemberIdentity(target), parameter.Ordinal);

            if (nonCancelableFactDemands.TryGetValue(key, out bool known)) return known;

            bool result = RequiresNonCancelableParameterFact(target, parameter, [], 0);

            nonCancelableFactDemands.Add(key, result);

            return result;
        }

        private bool RequiresNonCancelableParameterFact(AuthoredMember target, IParameterSymbol parameter,
            HashSet<(GraphMemberIdentity Member, int Parameter)> visited, int depth)
        {
            if (depth >= ReceiverCleanupForwardingMaximumDepth
                || !IsStableOwnCancellationParameter(target, parameter)
                || !visited.Add((MemberIdentity(target), parameter.Ordinal))) return false;

            foreach (InvocationExpressionSyntax call in target.Syntax.DescendantNodesAndSelf(node =>
                node == target.Syntax || node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
                .OfType<InvocationExpressionSyntax>())
            {
                if (target.Model.GetOperation(call) is not IInvocationOperation { IsVirtual: false } invocation) continue;

                foreach (IArgumentOperation argument in invocation.Arguments)
                {
                    if (argument is not { ArgumentKind: ArgumentKind.Explicit, Parameter: { RefKind: RefKind.None } formal,
                            Value.Syntax: ExpressionSyntax value }
                        || !argument.InConversion.IsIdentity || !argument.OutConversion.IsIdentity
                        || value.Parent is ArgumentSyntax { RefKindKeyword.RawKind: not 0 }
                        || !SymbolEqualityComparer.Default.Equals(
                            target.Model.GetSymbolInfo(CancellationFactOperand(value)).Symbol, parameter)) continue;

                    if (IsExactAggregateCancellationWait(invocation, argument)) return true;

                    if (invocation.TargetMethod is { MethodKind: MethodKind.Ordinary, ReducedFrom: null } method
                        && Resolve(method, target.Model.Compilation) is { } forwarded
                        && formal.Ordinal < forwarded.Symbol.Parameters.Length
                        && RequiresNonCancelableParameterFact(forwarded, forwarded.Symbol.Parameters[formal.Ordinal], visited, depth + 1)) return true;
                }
            }

            return false;
        }

        // Demand may overapproximate the selector, but never grants completion: the
        // existing aggregate recognizer still proves the exact selector/array/actual join.
        private static bool IsExactAggregateCancellationWait(IInvocationOperation invocation, IArgumentOperation argument) =>
            invocation.TargetMethod is { Name: "WaitAsync", IsStatic: false, Arity: 0,
                Parameters: [IParameterSymbol { RefKind: RefKind.None } cancellation] } wait
            && IsExactFrameworkType(wait.ContainingType, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName())
            && IsExactCancellationToken(cancellation.Type)
            && invocation.Arguments is [IArgumentOperation only] && ReferenceEquals(only, argument)
            && invocation.Instance is IInvocationOperation
            {
                TargetMethod: { Name: "WhenAll", IsStatic: true, Arity: 0,
                    Parameters: [IParameterSymbol { RefKind: RefKind.None, Type: INamedTypeSymbol sequence }] } all,
            }
            && IsExactFrameworkType(all.ContainingType, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName())
            && IsExactFrameworkType(sequence, "System.Collections.Generic.IEnumerable`1", typeof(IEnumerable<>).Assembly.GetName())
            && sequence.TypeArguments is [ITypeSymbol task]
            && IsExactFrameworkType(task, "System.Threading.Tasks.Task", typeof(Task).Assembly.GetName());

        private void AppendKnownNonCancelableParameterIdentity(StringBuilder value, AuthoredMember member)
        {
            AppendIdentitySection(value, "known-noncancelable-parameters", member.KnownNonCancelableParameters?.Count ?? 0);

            foreach (ISymbol parameter in member.KnownNonCancelableParameters?
                .OrderBy(parameter => BindingSymbolIdentity(parameter, member), StringComparer.Ordinal) ?? Enumerable.Empty<ISymbol>())
            {
                AppendIdentitySegment(value, BindingSymbolIdentity(parameter, member));
            }
        }
    }
}
