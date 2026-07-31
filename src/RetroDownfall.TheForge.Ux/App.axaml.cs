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

            desktop.MainWindow.Closed += (_, _) =>
            {

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

    private async void OnAboutClick(object? sender, EventArgs e)
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
