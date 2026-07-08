using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.Views;

namespace RetroDownfall.TheForge.Ux;

public partial class App : Application
{

    private static IServiceProvider? _services;

    public static void ConfigureServices(IServiceProvider services)
    {

        _services = services;

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

            desktop.MainWindow = new MainWindow
            {
                DataContext = services.GetRequiredService<MainViewModel>(),
            };

        }

        base.OnFrameworkInitializationCompleted();

    }

}
