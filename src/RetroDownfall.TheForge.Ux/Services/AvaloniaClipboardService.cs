using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Resolves the clipboard from the main window's <see cref="Avalonia.Controls.TopLevel"/> and copies
/// text on the UI thread. No-ops when no desktop lifetime or clipboard is available.
/// </summary>
public sealed class AvaloniaClipboardService : IClipboardService
{

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.CompletedTask;

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            Avalonia.Controls.TopLevel? topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);

            if (topLevel?.Clipboard is { } clipboard)
            {

                await clipboard.SetTextAsync(text).ConfigureAwait(true);

            }

        });

    }

}
