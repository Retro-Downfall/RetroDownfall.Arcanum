using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux;

internal static class Program
{

    // Avalonia's Visual Studio designer requires this deterministic, exception-free entry point —
    // do not move DI setup here, do not use any Avalonia types before InitializeWithClassicDesktopLifetime.
    [STAThread]
    public static void Main(string[] args)
    {

        TheForgeStartupArguments startup = TheForgeDeepLinkStartup.Parse(args);

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
