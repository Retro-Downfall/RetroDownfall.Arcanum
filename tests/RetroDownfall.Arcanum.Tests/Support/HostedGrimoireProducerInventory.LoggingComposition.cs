using Microsoft.CodeAnalysis;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Only the no-callback overload adds the framework's default logging descriptors.
        // Action<ILoggingBuilder> exposes Services and can replace recovery registrations.
        private static bool IsExactDefaultLoggingRegistration(IMethodSymbol method)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            return definition is { IsStatic: true, MethodKind: MethodKind.Ordinary, Arity: 0, Parameters: [{ RefKind: RefKind.None } services] }
                && definition.Name == "AddLogging"
                && IsExactFrameworkType(definition.ContainingType,
                    "Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions",
                    typeof(Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions).Assembly.GetName())
                && IsExactFrameworkType(services.Type,
                    "Microsoft.Extensions.DependencyInjection.IServiceCollection",
                    typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.GetName())
                && SymbolEqualityComparer.Default.Equals(definition.ReturnType, services.Type);
        }
    }
}
