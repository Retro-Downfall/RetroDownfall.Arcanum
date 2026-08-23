using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Whispers-style modals for campaign New / Edit / Import-strategy / Open path. UI-only — tests fake
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

    public Task<NewCampaignInputs?> PromptNewCampaignAsync(
        NewCampaignDialogOptions? options = null,
        CancellationToken cancellationToken = default)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<NewCampaignInputs?>(null);

        }

        NewCampaignDialogOptions opts = options ?? new NewCampaignDialogOptions();

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            TextBox nameBox = new()
            {
                PlaceholderText = "Name (required)",
                Text = opts.PrefillName ?? string.Empty,
            };

            TextBox pathBox = new()
            {
                PlaceholderText = opts.PathFieldLabel ?? "Absolute path (required)",
                Text = opts.PrefillPath ?? string.Empty,
            };

            TextBox descBox = new()
            {
                PlaceholderText = "Description (optional)",
                Text = opts.PrefillDescription ?? string.Empty,
            };

            ComboBox typeBox = new()
            {
                ItemsSource = TypeOptions,
                MinWidth = 280,
            };

            int typeIndex = opts.PrefillType is { } prefillType
                ? Array.IndexOf(TypeOptions, prefillType)
                : 0;

            typeBox.SelectedIndex = typeIndex >= 0 ? typeIndex : 0;

            TextBlock error = new() { TextWrapping = TextWrapping.Wrap };

            Button browse = new()
            {
                Content = "Browse…",
                IsVisible = opts.AllowLocalFolderBrowse,
            };

            Button ok = new() { Content = "Create", IsDefault = true };

            Button cancel = new() { Content = "Cancel", IsCancel = true };

            TaskCompletionSource<NewCampaignInputs?> tcs = new();

            string intro = opts.IntroText
                ?? (opts.AllowLocalFolderBrowse
                    ? "Create a campaign. Choose a local folder or type an absolute path."
                    : "Create a campaign. Enter the absolute path on the Arcanum host.");

            string pathLabel = opts.PathFieldLabel
                ?? (opts.AllowLocalFolderBrowse ? "Path" : "Path on Arcanum host");

            DockPanel pathRow = new()
            {
                LastChildFill = true,
            };

            pathRow.Children.Add(browse);

            DockPanel.SetDock(browse, Dock.Right);

            pathRow.Children.Add(pathBox);

            Window dialog = new()
            {
                Title = "New Campaign",
                Width = 480,
                Height = 440,
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
                            Text = intro,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock { Text = "Name" },
                        nameBox,
                        new TextBlock { Text = pathLabel },
                        pathRow,
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

            browse.Click += async (_, _) =>
            {

                IReadOnlyList<IStorageFolder> folders = await dialog.StorageProvider
                    .OpenFolderPickerAsync(new FolderPickerOpenOptions
                    {
                        Title = "Select campaign folder",
                        AllowMultiple = false,
                    })
                    .ConfigureAwait(true);

                if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } localPath)
                {

                    pathBox.Text = localPath;

                    if (string.IsNullOrWhiteSpace(nameBox.Text))
                    {

                        nameBox.Text = CampaignPathComparer.ProposeNameFromPath(localPath, loopback: true) ?? string.Empty;

                    }

                }

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

                tcs.TrySetResult(new NewCampaignInputs(name, path, type, string.IsNullOrWhiteSpace(descBox.Text) ? null : descBox.Text.Trim()));

                dialog.Close();

            };

            cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            dialog.Closed += (_, _) => tcs.TrySetResult(null);

            await dialog.ShowDialog(desktop.MainWindow).ConfigureAwait(true);

            return await tcs.Task.ConfigureAwait(true);

        });

    }

    public Task<string?> PromptOpenCampaignPathAsync(
        bool allowLocalFolderBrowse,
        CancellationToken cancellationToken = default)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<string?>(null);

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (allowLocalFolderBrowse)
            {

                IReadOnlyList<IStorageFolder> folders = await desktop.MainWindow.StorageProvider
                    .OpenFolderPickerAsync(new FolderPickerOpenOptions
                    {
                        Title = "Open Campaign folder",
                        AllowMultiple = false,
                    })
                    .ConfigureAwait(true);

                if (folders.Count == 0)
                {

                    return null;

                }

                return folders[0].TryGetLocalPath();

            }

            TextBox pathBox = new()
            {
                PlaceholderText = "Absolute path on the Arcanum host",
                MinWidth = 360,
            };

            TextBlock error = new() { TextWrapping = TextWrapping.Wrap };

            Button ok = new() { Content = "Open", IsDefault = true };

            Button cancel = new() { Content = "Cancel", IsCancel = true };

            TaskCompletionSource<string?> tcs = new();

            Window dialog = new()
            {
                Title = "Open Campaign",
                Width = 480,
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
                            Text = "Enter the absolute path of the campaign folder on the Arcanum host.",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock { Text = "Path on Arcanum host" },
                        pathBox,
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

                string path = pathBox.Text?.Trim() ?? string.Empty;

                if (string.IsNullOrEmpty(path))
                {

                    error.Text = "Path is required.";

                    return;

                }

                tcs.TrySetResult(path);

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

            TextBox nameBox = new() { Text = existing.Name, PlaceholderText = "Name (required)" };

            TextBox descBox = new() { Text = existing.Description ?? string.Empty, PlaceholderText = "Description (optional)" };

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
