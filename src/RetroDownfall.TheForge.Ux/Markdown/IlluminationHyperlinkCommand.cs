using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>
/// Hyperlink command for The Illumination that opens only schemes allowed by
/// <see cref="MarkdownLinkPolicy"/> via the OS launcher on explicit click.
/// </summary>
public sealed class IlluminationHyperlinkCommand : ICommand
{

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        MarkdownLinkPolicy.ShouldOpen(parameter?.ToString());

    public async void Execute(object? parameter)
    {

        string? uri = parameter?.ToString();

        if (!MarkdownLinkPolicy.ShouldOpen(uri))
        {

            return;

        }

        ILauncher? launcher = ResolveLauncher();

        if (launcher is null)
        {

            return;

        }

        try
        {

            await launcher.LaunchUriAsync(new Uri(uri!)).ConfigureAwait(true);

        }
        catch
        {

            // Ignore launch failures; preview must never crash on a bad link click.
        }

    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static ILauncher? ResolveLauncher()
    {

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } main)
        {

            return TopLevel.GetTopLevel(main)?.Launcher;

        }

        return null;

    }

}
