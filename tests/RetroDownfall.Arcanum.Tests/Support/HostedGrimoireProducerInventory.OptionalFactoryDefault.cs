using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Only the absent-optional certificate calls this predicate. An explicit C#
        // factory may omit a null-default slot without resolving that slot from DI.
        private bool HasExactOmittedOptionalFactoryDefault(
            ResolvedRegistration registration,
            IMethodSymbol constructor,
            IParameterSymbol parameter,
            Compilation consumingCompilation)
        {
            if (!IsExactRootFactoryRegistrationMethod(registration.Method)
                || parameter is not { RefKind: RefKind.None, IsOptional: true, HasExplicitDefaultValue: true, ExplicitDefaultValue: null }
                || !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol.OriginalDefinition, constructor.OriginalDefinition)
                || registration.Call.ArgumentList.Arguments is not
                    [ArgumentSyntax { Expression: AnonymousFunctionExpressionSyntax factory }]) return false;

            IParameterSymbol? provider = factory switch
            {
                SimpleLambdaExpressionSyntax simple => registration.Model.GetDeclaredSymbol(simple.Parameter),
                ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters: [ParameterSyntax syntax] } =>
                    registration.Model.GetDeclaredSymbol(syntax),
                _ => null,
            };

            if (provider is null
                || !IsExactFrameworkType(provider.Type, "System.IServiceProvider", typeof(IServiceProvider).Assembly.GetName())
                || !TryResolveExactFactoryConstruction(factory, registration.Model, constructor.ContainingType, consumingCompilation,
                    out AuthoredMember factoryMember, out BaseObjectCreationExpressionSyntax creation, out AuthoredMember context)
                || HasConstructorDependencyMutation(factoryMember, provider)
                || !SymbolEqualityComparer.Default.Equals(context.Symbol.OriginalDefinition, constructor.OriginalDefinition)
                || registration.Model.GetOperation(creation) is not IObjectCreationOperation { Constructor: { } selected } operation
                || !SymbolEqualityComparer.Default.Equals(selected.OriginalDefinition, constructor.OriginalDefinition)
                || parameter.Ordinal >= selected.Parameters.Length) return false;

            IParameterSymbol selectedParameter = selected.Parameters[parameter.Ordinal];

            IArgumentOperation[] arguments = operation.Arguments
                .Where(argument => argument.Parameter?.Ordinal == parameter.Ordinal).ToArray();

            return SymbolEqualityComparer.Default.Equals(selectedParameter.OriginalDefinition, parameter.OriginalDefinition)
                && selectedParameter is { RefKind: RefKind.None, IsOptional: true, HasExplicitDefaultValue: true, ExplicitDefaultValue: null }
                && arguments is [IArgumentOperation
                {
                    ArgumentKind: ArgumentKind.DefaultValue,
                    IsImplicit: true,
                    Parameter: { } omitted,
                    Value.ConstantValue: { HasValue: true, Value: null },
                } omittedArgument]
                && SymbolEqualityComparer.Default.Equals(omitted.OriginalDefinition, selectedParameter.OriginalDefinition)
                && omittedArgument.InConversion.MethodSymbol is null
                && omittedArgument.OutConversion.MethodSymbol is null;
        }
    }
}
