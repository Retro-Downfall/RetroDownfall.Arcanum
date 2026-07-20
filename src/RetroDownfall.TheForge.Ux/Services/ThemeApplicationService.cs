using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Options;
using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Applies <see cref="TheForgeSettings.Theme"/> to Avalonia: RequestedThemeVariant plus exactly one
/// Dark/Light resource dictionary (VS 2026 Fluent-inspired tokens). Typography and Icons stay static
/// in <c>App.axaml</c>; this service owns the sole active theme dictionary.
/// </summary>
public sealed class ThemeApplicationService : IDisposable
{

    private readonly IDisposable? _subscription;

    private ResourceInclude? _currentThemeInclude;

    private bool _disposed;

    public ThemeApplicationService(IOptionsMonitor<TheForgeSettings> settings)
    {

        Apply(settings.CurrentValue.Theme);

        // OptionsMonitor may raise OnChange on a background thread (config file reload).
        _subscription = settings.OnChange(s => Apply(s.Theme));

    }

    public void Apply(string? theme)
    {

        if (_disposed)
        {

            return;

        }

        if (!Dispatcher.UIThread.CheckAccess())
        {

            Dispatcher.UIThread.Post(() => Apply(theme));

            return;

        }

        Application? app = Application.Current;

        if (app is null)
        {

            return;

        }

        bool light = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);

        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;

        string uri = light
            ? "avares://RetroDownfall.TheForge.Ux/Themes/LightTheme.axaml"
            : "avares://RetroDownfall.TheForge.Ux/Themes/DarkTheme.axaml";

        ResourceDictionary resources = (ResourceDictionary)app.Resources;

        if (_currentThemeInclude is not null)
        {

            resources.MergedDictionaries.Remove(_currentThemeInclude);

        }

        ResourceInclude include = new(new Uri("avares://RetroDownfall.TheForge.Ux/"))
        {
            Source = new Uri(uri),
        };

        resources.MergedDictionaries.Add(include);

        _currentThemeInclude = include;

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        _subscription?.Dispose();

    }

}
