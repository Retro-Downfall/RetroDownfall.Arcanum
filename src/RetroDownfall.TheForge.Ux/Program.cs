using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Desktop;
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

        // ApplicationDeepLinkCodec.ParseArguments only ever returns null when the argument is
        // altogether absent; when it is present it either returns a link or throws (caught inside
        // Parse). So "the argument was present, yet DeepLink is null" is exact for "present but
        // unparseable" — never a false positive on a link that parsed fine but targets another app.
        bool deepLinkParseFailed = startup.DeepLink is null
            && args.Contains(ApplicationDeepLinkCodec.ArgumentName, StringComparer.Ordinal);

        ServiceProvider services = ServiceCollectionConfigurator.Build();

        App.ConfigureServices(services, startup.DeepLink, deepLinkParseFailed);

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
