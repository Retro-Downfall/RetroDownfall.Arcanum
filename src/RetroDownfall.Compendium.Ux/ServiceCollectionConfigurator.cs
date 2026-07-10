using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        services.AddSingleton<IArcanumSecretProtector, ArcanumDataProtectionSecretProtector>();

        services.AddSingleton<IArcanumConfigurationStore, ArcanumConfigurationStore>();

        services.AddSingleton<IMainWindowProvider, MainWindowProvider>();

        services.AddSingleton<IDialogService, DialogService>();

        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();

        services.AddSingleton<ConfigurationViewModel>();

        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();

    }

}
