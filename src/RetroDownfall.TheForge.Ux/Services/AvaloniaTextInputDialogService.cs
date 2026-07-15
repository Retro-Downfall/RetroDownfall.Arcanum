using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Whispers-style single-line text prompt for clone name/version. Returns <see langword="null"/> on
/// cancel or empty input after trim — callers treat cancel as a silent no-op. UI-only — tests fake
/// <see cref="ITextInputDialogService"/>.
/// </summary>
public sealed class AvaloniaTextInputDialogService : ITextInputDialogService
{

    public Task<string?> PromptAsync(string title, string label, string? defaultValue, CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<string?>(null);

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            TextBox input = new()
            {
                Text = defaultValue ?? string.Empty,

                MinWidth = 360,
            };

            Button ok = new() { Content = "OK", IsDefault = true };

            Button cancel = new() { Content = "Cancel", IsCancel = true };

            TaskCompletionSource<string?> tcs = new();

            Window dialog = new()
            {
                Title = title,

                Width = 440,

                Height = 200,

                WindowStartupLocation = WindowStartupLocation.CenterOwner,

                CanResize = false,

                Content = new StackPanel
                {
                    Margin = new Thickness(16),

                    Spacing = 12,

                    Children =
                    {
                        new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },

                        input,

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

            ok.Click += (_, _) =>
            {

                string value = input.Text?.Trim() ?? string.Empty;

                tcs.TrySetResult(string.IsNullOrEmpty(value) ? null : value);

                dialog.Close();

            };

            cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            dialog.Closed += (_, _) => tcs.TrySetResult(null);

            await dialog.ShowDialog(desktop.MainWindow).ConfigureAwait(true);

            return await tcs.Task.ConfigureAwait(true);

        });

    }

}
