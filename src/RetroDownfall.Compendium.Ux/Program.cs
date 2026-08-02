using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Compendium.Ux.Services;

namespace RetroDownfall.Compendium.Ux;

internal static class Program
{

    [STAThread]
    public static void Main(string[] args)
    {

        CompendiumStartupArguments startup = CompendiumDeepLinkStartup.Parse(args);

        ServiceProvider services = ServiceCollectionConfigurator.Build();

        App.ConfigureServices(services, startup.DeepLink);

        try
        {

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(startup.AvaloniaArguments);

        }
        finally
        {

            services.Dispose();

        }

    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

}
