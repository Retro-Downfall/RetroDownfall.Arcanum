using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Desktop;
using RetroDownfall.Compendium.Ux.Services;

namespace RetroDownfall.Compendium.Ux;

public partial class App : Application
{

    private static IServiceProvider? _services;

    private static ApplicationDeepLink? _startupDeepLink;

    public static void ConfigureServices(
        IServiceProvider services,
        ApplicationDeepLink? startupDeepLink = null)
    {

        _services = services;

        _startupDeepLink = startupDeepLink;

    }

    public override void Initialize()
    {

        AvaloniaXamlLoader.Load(this);

    }

    public override void OnFrameworkInitializationCompleted()
    {

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {

            IServiceProvider services = _services
                ?? throw new InvalidOperationException("DI services were not configured before startup; call App.ConfigureServices first.");

            // RequestedThemeVariant=Default (App.axaml) follows the OS; ThemeDictionaries
            // supply Light/Dark Forge brushes automatically via ActualThemeVariant.

            ViewModels.ConfigurationViewModel viewModel = services.GetRequiredService<ViewModels.ConfigurationViewModel>();

            CompendiumDeepLinkStartup.Apply(_startupDeepLink, viewModel);

            Views.MainWindow mainWindow = services.GetRequiredService<Views.MainWindow>();

            mainWindow.DataContext = viewModel;

            if (services.GetRequiredService<IMainWindowProvider>() is MainWindowProvider provider)
            {

                provider.SetMainWindow(mainWindow);

            }

            desktop.MainWindow = mainWindow;

        }

        base.OnFrameworkInitializationCompleted();

    }

    private async void OnAboutClick(object? sender, EventArgs e)
    {

        if (_services is null)
        {

            return;

        }

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0-beta";

        IDialogService dialogs = _services.GetRequiredService<IDialogService>();

        await dialogs.ShowAlertAsync(
            "About Compendium",
            $"Compendium — Arcanum configuration editor\nVersion {version}");

    }

}
