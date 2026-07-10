using Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace RetroDownfall.Compendium.Ux;

internal static class Program
{

    [STAThread]
    public static void Main(string[] args)
    {

        ServiceProvider services = ServiceCollectionConfigurator.Build();

        App.ConfigureServices(services);

        try
        {

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

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
