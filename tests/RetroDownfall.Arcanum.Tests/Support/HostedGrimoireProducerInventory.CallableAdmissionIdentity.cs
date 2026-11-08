using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private AuthoredMember NormalizeEmptyCapturedAdmissionIdentity(AuthoredMember target)
        {
            if (target.Symbol.MethodKind is not (MethodKind.LocalFunction or MethodKind.AnonymousFunction)
                || target.AdmissionBindings is not { Count: > 0 } admissions
                || target.CallableBindings is { Count: > 0 }
                || !HasExactAuthoredOwner(target, target.Symbol))
            {
                return target;
            }

            ExpressionSyntax[] expressions = target.Syntax.DescendantNodesAndSelf().OfType<ExpressionSyntax>().ToArray();

            // The dependency scanner follows ordinary local initializers and assignments
            // in this body. Captured local writes, separately declared local functions
            // and bound delegates need a larger closure proof, so retain their maps.
            foreach (ExpressionSyntax expression in expressions)
            {
                ISymbol? symbol = target.Model.GetSymbolInfo(expression).Symbol;

                if (symbol is ILocalSymbol local
                    && (local.ContainingSymbol is not IMethodSymbol localOwner
                        || !SymbolEqualityComparer.Default.Equals(localOwner.OriginalDefinition, target.Symbol.OriginalDefinition)))
                {
                    return target;
                }

                if (expression is InvocationExpressionSyntax
                    && (symbol is not IMethodSymbol method
                        || method.MethodKind is MethodKind.LocalFunction or MethodKind.DelegateInvoke))
                {
                    return target;
                }
            }

            HashSet<ISymbol> removable = new(SymbolEqualityComparer.Default);

            foreach ((ISymbol symbol, IReadOnlySet<string> origins) in admissions)
            {
                if (origins.Count != 0
                    || target.RecoveryBindings?.ContainsKey(symbol) == true
                    || symbol is not IParameterSymbol { RefKind: RefKind.None, ContainingSymbol: IMethodSymbol owner }
                    || owner.MethodKind is not (MethodKind.Ordinary or MethodKind.LocalFunction or MethodKind.AnonymousFunction)
                    || SymbolEqualityComparer.Default.Equals(owner.OriginalDefinition, target.Symbol.OriginalDefinition)
                    || !HasExactAuthoredOwner(target, owner))
                {
                    continue;
                }

                bool enclosing = false;

                ISymbol? scope = target.Symbol.ContainingSymbol;

                for (int depth = 0; depth < DefaultEvaluationEnvironmentMaximumDepth && scope is IMethodSymbol ancestor; depth++)
                {
                    if (!HasExactAuthoredOwner(target, ancestor)) break;

                    if (SymbolEqualityComparer.Default.Equals(owner.OriginalDefinition, ancestor.OriginalDefinition))
                    {
                        enclosing = true;

                        break;
                    }

                    scope = ancestor.ContainingSymbol;
                }

                if (!enclosing) continue;

                Dictionary<ExpressionSyntax, bool> completed = new(ReferenceEqualityComparer.Instance);

                bool unused = true;

                foreach (ExpressionSyntax expression in expressions)
                {
                    ExpressionDependencyResult dependency = EvaluateExpressionDependency(target, expression, symbol,
                        new HashSet<ISymbol>(SymbolEqualityComparer.Default), completed, analysis: null, symbols: dependencySymbols);

                    if (dependency.Depends || !dependency.Complete)
                    {
                        unused = false;

                        break;
                    }
                }

                if (unused) removable.Add(symbol);
            }

            return removable.Count == 0 ? target : target with
            {
                AdmissionBindings = admissions.Where(pair => !removable.Contains(pair.Key))
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, SymbolEqualityComparer.Default),
            };
        }
    }
}
