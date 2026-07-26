using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Whispers-style modal that asks the user to paste the master API key when the OS store is empty.
/// </summary>
public sealed class AvaloniaApiKeyPrompt : IApiKeyPrompt
{

    public Task<string?> PromptForApiKeyAsync(CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            throw new InvalidOperationException(
                "Main window is not ready; cannot show the API key paste prompt yet.");

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            TextBox input = new()
            {
                PlaceholderText = "Paste X-Arcanum-Key / master API key",
                PasswordChar = '•',
                MinWidth = 360,
            };

            Button ok = new() { Content = "Store in OS keychain", IsDefault = true };

            Button cancel = new() { Content = "Cancel", IsCancel = true };

            TaskCompletionSource<string?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Window dialog = new()
            {
                Title = "Whispers — API key required",
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
                        new TextBlock
                        {
                            Text = "No master API key was found in the OS credential store (arcanum / master-api-key). "
                                + "Paste the key from `arcanum key show`, or run `arcanum serve` once to create one.",
                            TextWrapping = TextWrapping.Wrap,
                        },
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

                string? value = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim();

                tcs.TrySetResult(value);

                dialog.Close();

            };

            cancel.Click += (_, _) =>
            {

                tcs.TrySetResult(null);

                dialog.Close();

            };

            dialog.Closed += (_, _) => tcs.TrySetResult(null);

            await using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {

                tcs.TrySetCanceled(cancellationToken);

                try
                {

                    dialog.Close();

                }
                catch
                {

                    // Dialog may already be closed.

                }

            });

            await dialog.ShowDialog(desktop.MainWindow).ConfigureAwait(true);

            return await tcs.Task.ConfigureAwait(true);

        });

    }

}
