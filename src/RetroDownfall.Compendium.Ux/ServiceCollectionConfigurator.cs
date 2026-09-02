using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Compendium.Ux.Services;
using RetroDownfall.Compendium.Ux.ViewModels;
using RetroDownfall.Compendium.Ux.Views;

namespace RetroDownfall.Compendium.Ux;

internal static class ServiceCollectionConfigurator
{

    public static ServiceProvider Build()
    {

        ServiceCollection services = new();

        services.AddLogging(builder => builder.AddDebug());

        services.AddArcanumClientMutationCoordination();

        services.AddArcanumConfigurationPresets(
            static (sp, inner) => new CompendiumConfigurationPresetService(
                inner,
                sp.GetRequiredService<IArcanumClientMutationBoundary>()));

        services.AddSingleton<IArcanumConfigurationStore, ArcanumConfigurationStore>();

        services.AddSingleton<LocalCertificateGenerator>();

        services.AddSingleton<IMainWindowProvider, MainWindowProvider>();

        services.AddSingleton<IDialogService, DialogService>();

        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();

        // The probe authenticates to the host with the master API key, so the secret store travels
        // with it. Nothing here opens the Grimoire — this is the key read and nothing more.
        services.TryAddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddArcanumSecretStore();

        // The only thing Compendium asks the host for. Configuration persistence stays exactly as it
        // is — direct, atomic, transactional writes through ArcanumConfigurationStore.
        // Named so the socket handler this creates is pooled and reused across probes instead of a
        // fresh SocketsHttpHandler (and TCP+TLS handshake) per click.
        services.AddHttpClient(nameof(FamiliarProbeClient));

        services.AddSingleton<IFamiliarProbeClient, FamiliarProbeClient>();

        services.AddSingleton<ConfigurationViewModel>();

        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();

    }

}
