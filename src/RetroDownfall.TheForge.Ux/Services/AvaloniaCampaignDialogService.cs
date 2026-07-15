using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Whispers-style modals for campaign New / Edit / Import-strategy. UI-only — tests fake
/// <see cref="ICampaignDialogService"/>.
/// </summary>
public sealed class AvaloniaCampaignDialogService : ICampaignDialogService
{

    private static readonly WorkspaceType[] TypeOptions =
    [
        WorkspaceType.Campaign,
        WorkspaceType.Spell,
        WorkspaceType.Data,
        WorkspaceType.Custom,
    ];

    public Task<NewCampaignInputs?> PromptNewCampaignAsync(CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<NewCampaignInputs?>(null);

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            TextBox nameBox = new() { Watermark = "Name (required)" };

            TextBox pathBox = new() { Watermark = "Absolute path (required)" };

            TextBox descBox = new() { Watermark = "Description (optional)" };

            ComboBox typeBox = new()
            {
                ItemsSource = TypeOptions,
                SelectedIndex = 0,
                MinWidth = 280,
            };

            TextBlock error = new() { TextWrapping = TextWrapping.Wrap };

            Button ok = new() { Content = "Create", IsDefault = true };

            Button cancel = new() { Content = "Cancel", IsCancel = true };

            TaskCompletionSource<NewCampaignInputs?> tcs = new();

            Window dialog = new()
            {
                Title = "New Campaign",
                Width = 460,
                Height = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Register a campaign root. Path must exist and be allowed by Arcanum.",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock { Text = "Name" },
                        nameBox,
                        new TextBlock { Text = "Path" },
                        pathBox,
                        new TextBlock { Text = "Type" },
                        typeBox,
                        new TextBlock { Text = "Description" },
                        descBox,
                        error,
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

                string name = nameBox.Text?.Trim() ?? string.Empty;

                string path = pathBox.Text?.Trim() ?? string.Empty;

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
                {

                    error.Text = "Name and path are required.";

                    return;

                }

                WorkspaceType type = typeBox.SelectedItem is WorkspaceType selected
                    ? selected
                    : WorkspaceType.Campaign;

                tcs.TrySetResult(new NewCampaignInputs(name, path, type, descBox.Text?.Trim()));

                dialog.Close();

            };

            cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            dialog.Closed += (_, _) => tcs.TrySetResult(null);

            await dialog.ShowDialog(desktop.MainWindow).ConfigureAwait(true);

            return await tcs.Task.ConfigureAwait(true);

        });

    }

    public Task<EditCampaignInputs?> PromptEditCampaignAsync(CampaignDto existing, CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<EditCampaignInputs?>(null);

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            TextBox nameBox = new() { Text = existing.Name, Watermark = "Name (required)" };

            TextBox descBox = new() { Text = existing.Description ?? string.Empty, Watermark = "Description (optional)" };

            ComboBox typeBox = new()
            {
                ItemsSource = TypeOptions,
                MinWidth = 280,
            };

            int typeIndex = Array.IndexOf(TypeOptions, existing.Type);

            typeBox.SelectedIndex = typeIndex >= 0 ? typeIndex : 0;

            TextBlock pathHint = new()
            {
                Text = $"Path: {existing.Path} (immutable)",
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap,
            };

            TextBlock error = new() { TextWrapping = TextWrapping.Wrap };

            Button ok = new() { Content = "Save", IsDefault = true };

            Button cancel = new() { Content = "Cancel", IsCancel = true };

            TaskCompletionSource<EditCampaignInputs?> tcs = new();

            Window dialog = new()
            {
                Title = $"Edit Campaign — {existing.Name}",
                Width = 460,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 10,
                    Children =
                    {
                        pathHint,
                        new TextBlock { Text = "Name" },
                        nameBox,
                        new TextBlock { Text = "Type" },
                        typeBox,
                        new TextBlock { Text = "Description" },
                        descBox,
                        error,
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

                string name = nameBox.Text?.Trim() ?? string.Empty;

                if (string.IsNullOrEmpty(name))
                {

                    error.Text = "Name is required.";

                    return;

                }

                WorkspaceType type = typeBox.SelectedItem is WorkspaceType selected
                    ? selected
                    : existing.Type;

                string? description = string.IsNullOrWhiteSpace(descBox.Text) ? null : descBox.Text.Trim();

                tcs.TrySetResult(new EditCampaignInputs(name, type, description));

                dialog.Close();

            };

            cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            dialog.Closed += (_, _) => tcs.TrySetResult(null);

            await dialog.ShowDialog(desktop.MainWindow).ConfigureAwait(true);

            return await tcs.Task.ConfigureAwait(true);

        });

    }

    public Task<string?> PromptImportStrategyAsync(CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<string?>(null);

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            ComboBox strategyBox = new()
            {
                ItemsSource = new[] { "merge", "replace" },
                SelectedIndex = 0,
                MinWidth = 280,
            };

            Button ok = new() { Content = "Continue", IsDefault = true };

            Button cancel = new() { Content = "Cancel", IsCancel = true };

            TaskCompletionSource<string?> tcs = new();

            Window dialog = new()
            {
                Title = "Import into Campaign",
                Width = 440,
                Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Import a campaign export bundle into this existing campaign. Choose merge or replace.",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock { Text = "Strategy" },
                        strategyBox,
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

                string strategy = strategyBox.SelectedItem as string ?? "merge";

                tcs.TrySetResult(strategy);

                dialog.Close();

            };

            cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            dialog.Closed += (_, _) => tcs.TrySetResult(null);

            await dialog.ShowDialog(desktop.MainWindow).ConfigureAwait(true);

            return await tcs.Task.ConfigureAwait(true);

        });

    }

}
