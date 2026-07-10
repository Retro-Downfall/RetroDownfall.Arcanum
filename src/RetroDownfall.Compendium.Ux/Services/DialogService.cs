using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace RetroDownfall.Compendium.Ux.Services;

public sealed class DialogService(IMainWindowProvider mainWindowProvider) : IDialogService
{

    public async Task ShowAlertAsync(string title, string message, string cancel = "OK")
    {

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {

            Window? owner = mainWindowProvider.GetMainWindow();

            if (owner is null)
            {

                return;

            }

            Window dialog = CreateDialog(title, message, cancel, null, null);

            await dialog.ShowDialog(owner);

        });

    }

    public async Task<bool> ShowConfirmAsync(string title, string message, string accept = "Yes", string cancel = "No")
    {

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {

            Window? owner = mainWindowProvider.GetMainWindow();

            if (owner is null)
            {

                return false;

            }

            TaskCompletionSource<bool> tcs = new();

            Window dialog = CreateDialog(title, message, cancel, accept, tcs);

            await dialog.ShowDialog(owner);

            return await tcs.Task;

        });

    }

    private static Window CreateDialog(
        string title,
        string message,
        string cancel,
        string? accept,
        TaskCompletionSource<bool>? tcs)
    {

        Button cancelButton = new()
        {
            Content = cancel,
            MinWidth = 72,
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton },
        };

        if (accept is not null)
        {

            Button acceptButton = new()
            {
                Content = accept,
                MinWidth = 72,
            };

            acceptButton.Click += (_, _) =>
            {

                tcs?.TrySetResult(true);

                // Close via tag
            };

            buttons.Children.Insert(0, acceptButton);

            // Wire after dialog created below
        }

        StackPanel content = new()
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420,
                },
                buttons,
            },
        };

        Window dialog = new()
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content,
        };

        cancelButton.Click += (_, _) =>
        {

            tcs?.TrySetResult(false);

            dialog.Close();

        };

        if (accept is not null && buttons.Children[0] is Button acceptBtn)
        {

            acceptBtn.Click += (_, _) =>
            {

                tcs?.TrySetResult(true);

                dialog.Close();

            };

        }

        dialog.Closed += (_, _) => tcs?.TrySetResult(false);

        return dialog;

    }

}
