using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // The default-options cleanup proof still requires the existing closed host
        // composition and absence of monitor overrides. This supplies only collection
        // identity through a stable internal registration helper, never DI authority.
        private bool HasExactForwardedOptionsCollection(ISymbol configured, ISymbol receiver, SemanticModel model)
        {
            HashSet<ISymbol> path = new(SymbolEqualityComparer.Default);

            bool SameOrigin(ISymbol candidate, SemanticModel ownerModel)
            {
                if (SymbolEqualityComparer.Default.Equals(configured, candidate)) return true;

                if (candidate is not IParameterSymbol { Ordinal: 0, RefKind: RefKind.None } parameter
                    || !IsExactFrameworkType(parameter.Type, "Microsoft.Extensions.DependencyInjection.IServiceCollection",
                        typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.GetName())
                    || parameter.ContainingSymbol is not IMethodSymbol
                        { MethodKind: MethodKind.Ordinary, IsStatic: true, Arity: 0, Parameters.Length: 1 } method
                    || method.DeclaredAccessibility is not (Accessibility.Internal or Accessibility.Private)
                    || method.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is not MethodDeclarationSyntax { Body: { } body }
                    || !path.Add(parameter)) return false;

                try
                {
                    bool IsParameter(ExpressionSyntax expression) => SymbolEqualityComparer.Default.Equals(
                        ownerModel.GetSymbolInfo(expression).Symbol, parameter);

                    if (body.DescendantNodes().Any(node => node switch
                        {
                            AssignmentExpressionSyntax assignment => IsParameter(assignment.Left),
                            ArgumentSyntax argument when argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword => IsParameter(argument.Expression),
                            RefExpressionSyntax alias => IsParameter(alias.Expression),
                            _ => false,
                        })) return false;

                    bool observed = false;

                    foreach (var caller in invocations)
                    {
                        if (caller.Model.GetSymbolInfo(caller.Call).Symbol is not IMethodSymbol invoked
                            || !SymbolEqualityComparer.Default.Equals((invoked.ReducedFrom ?? invoked).OriginalDefinition, method.OriginalDefinition)) continue;

                        observed = true;

                        ExpressionSyntax? argument = invoked.ReducedFrom is not null
                            ? (caller.Call.Expression as MemberAccessExpressionSyntax)?.Expression
                            : (caller.Model.GetOperation(caller.Call) as IInvocationOperation)?.Arguments
                                .SingleOrDefault(static value => value.Parameter?.Ordinal == 0)?.Value.Syntax as ExpressionSyntax;

                        if (argument is null || caller.Model.GetSymbolInfo(argument).Symbol is not { } source
                            || !SameOrigin(source, caller.Model)) return false;
                    }

                    return observed;
                }
                finally
                {
                    path.Remove(parameter);
                }
            }

            return SameOrigin(receiver, model);
        }

        // Scalar synchronization and registration cleanup confer no producer admission.
        // Argument origins and Register/OnChange callback bodies remain traversed normally.
        private static bool IsReviewedObserverFrameworkMechanic(IMethodSymbol method)
        {
            if (method is { MethodKind: MethodKind.Ordinary, IsStatic: true, Arity: 0, Name: "Read", Parameters: [var location] }
                && method.ReturnType.SpecialType == SpecialType.System_Int64
                && location.Type.SpecialType == method.ReturnType.SpecialType
                && location.RefKind is not (RefKind.None or RefKind.Out)
                && IsExactFrameworkType(method.ContainingType, "System.Threading.Interlocked", typeof(Interlocked).Assembly.GetName()))
                return true;

            return method is { MethodKind: MethodKind.Ordinary, IsStatic: false, Arity: 0, Name: "Dispose", Parameters.Length: 0, ReturnsVoid: true }
                && IsExactFrameworkType(method.ContainingType, "System.Threading.CancellationTokenRegistration", typeof(CancellationTokenRegistration).Assembly.GetName())
                && IsReviewedExactMechanicalCleanup(method.ContainingType, method);
        }
    }
}
