using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.LocalRuntimes;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

public static class LocalRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddArcanumLocalRuntimeAdapters(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILocalRuntimeAdapter, OllamaRuntimeAdapter>());

        services.AddHttpClient(OllamaRuntimeAdapter.HttpClientName, static client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(static () => OutboundUrlGuard.CreateProviderEgressHandler());

        return services;
    }
}
