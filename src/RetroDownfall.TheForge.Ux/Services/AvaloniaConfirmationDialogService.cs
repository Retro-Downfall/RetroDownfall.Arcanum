using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Whispers-style OK/Cancel confirmation modal. Mirrors <see cref="AvaloniaArtifactCreationDialogService"/>:
/// resolves the main window via <c>IClassicDesktopStyleApplicationLifetime</c>, returns <c>false</c> when no
/// window is available or the operator cancels. UI-only — tests fake <see cref="IConfirmationDialogService"/>.
/// </summary>
public sealed class AvaloniaConfirmationDialogService : IConfirmationDialogService
{

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult(false);

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            Button ok = new() { Content = "Confirm", IsDefault = true };

            Button cancel = new() { Content = "Cancel", IsCancel = true };

            TaskCompletionSource<bool> tcs = new();

            Window dialog = new()
            {
                Title = title,

                Width = 420,

                Height = 200,

                WindowStartupLocation = WindowStartupLocation.CenterOwner,

                CanResize = false,

                Content = new StackPanel
                {
                    Margin = new Thickness(16),

                    Spacing = 12,

                    Children =
                    {
                        new TextBlock { Text = title, FontSize = 16 },

                        new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },

                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,

                            HorizontalAlignment = HorizontalAlignment.Right,

                            Spacing = 8,

                            Children = { cancel, ok },
                        },
                    },
                },
            };

            ok.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };

            cancel.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };

            dialog.Closed += (_, _) => tcs.TrySetResult(false);

            await dialog.ShowDialog(desktop.MainWindow).ConfigureAwait(true);

            return await tcs.Task.ConfigureAwait(true);

        });

    }

}
