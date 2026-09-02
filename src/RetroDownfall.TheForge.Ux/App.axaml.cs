using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.Arcanum.Core.Desktop;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.Views;

namespace RetroDownfall.TheForge.Ux;

public partial class App : Application
{

    private static IServiceProvider? _services;

    private static ApplicationDeepLink? _startupDeepLink;

    private static bool _deepLinkParseFailed;

    /// <summary>
    /// <paramref name="deepLinkParseFailed"/> is true when the process was launched with a
    /// <c>--arcanum-deep-link</c> argument that <see cref="TheForgeDeepLinkStartup.Parse"/> could not
    /// turn into a link (malformed JSON, unsupported schema version, or a target mismatch) — distinct
    /// from <paramref name="startupDeepLink"/> being null because no such argument was present at all.
    /// </summary>
    public static void ConfigureServices(
        IServiceProvider services,
        ApplicationDeepLink? startupDeepLink = null,
        bool deepLinkParseFailed = false)
    {

        _services = services;

        _startupDeepLink = startupDeepLink;

        _deepLinkParseFailed = deepLinkParseFailed;

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

            // Apply theme after Avalonia Application.Current exists.
            _ = services.GetRequiredService<ThemeApplicationService>();

            MainViewModel mainViewModel = services.GetRequiredService<MainViewModel>();

            // Initialize Whispers before assigning MainViewModel so WhispersHostView
            // does not briefly inherit MainViewModel (compiled binding expects IWhispersService).
            MainWindow mainWindow = new();
            mainWindow.Initialize(services.GetRequiredService<IWhispersService>());
            mainWindow.DataContext = mainViewModel;

            desktop.MainWindow = mainWindow;

            // Start AutoConnect only after MainWindow exists so the API-key paste prompt can show.
            services.GetRequiredService<ArcanumConnectionService>().StartAutoConnectIfConfigured();

            CancellationTokenSource? deepLinkCancellation = StartDeepLinkRouting(services);

            desktop.MainWindow.Closed += (_, _) =>
            {

                deepLinkCancellation?.Cancel();

                deepLinkCancellation?.Dispose();

                if (desktop.MainWindow.DataContext is IDisposable disposable)
                {

                    disposable.Dispose();

                }

                // ServiceProvider is disposed in Program.Main finally — do not dispose here
                // to avoid double-dispose of singletons while Avalonia is still shutting down.

            };

        }

        base.OnFrameworkInitializationCompleted();

    }

    internal static CancellationTokenSource? StartDeepLinkRouting(IServiceProvider services)
    {

        if (_startupDeepLink is null)
        {

            if (_deepLinkParseFailed)
            {

                services.GetRequiredService<IWhispersService>().Show(
                    WhisperSeverity.Error,
                    "The requested resource could not be opened.",
                    "Application link");

            }

            return null;

        }

        CancellationTokenSource cancellation = new();

        TaskUtilities.FireAndForget(
            RouteStartupDeepLinkAsync(
                services.GetRequiredService<TheForgeDeepLinkCoordinator>(),
                services.GetRequiredService<IWhispersService>(),
                _startupDeepLink,
                cancellation.Token));

        return cancellation;

    }

    private static async Task RouteStartupDeepLinkAsync(
        TheForgeDeepLinkCoordinator coordinator,
        IWhispersService whispers,
        ApplicationDeepLink deepLink,
        CancellationToken cancellationToken)
    {

        TheForgeDeepLinkRouteResult result = await coordinator
            .RouteAsync(deepLink, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Accepted)
        {

            whispers.Show(
                WhisperSeverity.Error,
                "The requested resource could not be opened.",
                "Application link");

        }

    }

    private async void OnAboutClick(object? sender, EventArgs e)
    {

        try
        {

            Window? owner = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

            if (owner is null)
            {

                return;

            }

            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0-alpha";

            Window dialog = new()
            {
                Title = "About The Forge",
                Width = 420,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(24),
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "The Forge — Inference IDE",
                            FontWeight = FontWeight.SemiBold,
                            FontSize = 16,
                        },
                        new TextBlock
                        {
                            Text = $"Version {version}",
                            Opacity = 0.8,
                        },
                        new TextBlock
                        {
                            Text = "Arcanum HTTP API client for campaigns, spells, sessions, and apprentices.",
                            TextWrapping = TextWrapping.Wrap,
                            Opacity = 0.72,
                            Margin = new Thickness(0, 4, 0, 0),
                        },
                    },
                },
            };

            await dialog.ShowDialog(owner);

        }
        catch (Exception)
        {

            // async void reposts an unhandled throw to the UI synchronization context instead of
            // returning it to a caller, which crashes the process — a stale owner (window closing or
            // already torn down) must not take the app down just because the About dialog could not
            // be shown, and neither must a fault while building the dialog itself. Mirrors
            // Compendium's App.axaml.cs OnAboutClick.
        }

    }

    private void OnSettingsClick(object? sender, EventArgs e)
    {

        if (_services is null)
        {

            return;

        }

        CompendiumLaunchResult result = OpenSettings(
            _services.GetRequiredService<ICompendiumLauncher>());

        _services.GetRequiredService<IWhispersService>().Show(
            result.Launched ? WhisperSeverity.Success : WhisperSeverity.Error,
            result.Message,
            "Settings");

    }

    internal static CompendiumLaunchResult OpenSettings(
        ICompendiumLauncher launcher) =>
        launcher.TryLaunch();

}
