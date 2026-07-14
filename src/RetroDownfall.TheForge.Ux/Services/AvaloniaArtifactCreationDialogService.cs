using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Whispers-style modal dialogs for Atelier New Spell / New Prompt / New Session commands. Each
/// method builds a <c>Window</c> in code and calls <c>ShowDialog(MainWindow)</c>, mirroring
/// <see cref="AvaloniaApiKeyPrompt"/>. Required fields are validated inline; <c>null</c> is returned
/// on cancel. UI-only — tests fake <see cref="IArtifactCreationDialogService"/> instead.
/// </summary>
public sealed class AvaloniaArtifactCreationDialogService : IArtifactCreationDialogService
{

    public Task<NewSpellInputs?> PromptNewSpellAsync(
        IReadOnlyList<WorkspaceOption> workspaces,
        WorkspaceOption? preselected,
        CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<NewSpellInputs?>(null);

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            TextBox nameBox = new() { Watermark = "Spell name (required)" };

            TextBox descBox = new() { Watermark = "Description (optional)" };

            TextBox bodyBox = new()
            {
                Watermark = "Body / SPELL.md (optional)",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 80,
            };

            List<WorkspaceOption> workspaceList = workspaces.ToList();

            ComboBox workspaceBox = new() { ItemsSource = workspaceList, MinWidth = 320 };

            workspaceBox.ItemTemplate = new FuncDataTemplate<WorkspaceOption>(
                (option, _) => new TextBlock { Text = option?.Display });

            if (preselected is not null)
            {

                int idx = workspaceList.FindIndex(w => string.Equals(w.Path, preselected.Path, StringComparison.Ordinal));

                if (idx >= 0)
                {

                    workspaceBox.SelectedIndex = idx;

                }

            }

            TextBlock error = new() { TextWrapping = TextWrapping.Wrap };

            Button ok = new() { Content = "Create", IsDefault = true };

            Button cancel = new() { Content = "Cancel", IsCancel = true };

            TaskCompletionSource<NewSpellInputs?> tcs = new();

            Window dialog = new()
            {
                Title = "Create Spell in Workspace",
                Width = 460,
                Height = 460,
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
                            Text = "Create a new workspace spell. Built-in spells remain read-only.",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock { Text = "Workspace" },
                        workspaceBox,
                        new TextBlock { Text = "Name" },
                        nameBox,
                        new TextBlock { Text = "Description" },
                        descBox,
                        new TextBlock { Text = "Body" },
                        bodyBox,
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

                WorkspaceOption? selected = workspaceBox.SelectedItem as WorkspaceOption;

                if (string.IsNullOrEmpty(name))
                {

                    error.Text = "Name is required.";

                    return;

                }

                if (selected is null || string.IsNullOrWhiteSpace(selected.Path))
                {

                    error.Text = "Select a workspace.";

                    return;

                }

                tcs.TrySetResult(new NewSpellInputs(name, descBox.Text?.Trim(), bodyBox.Text, selected.Path));

                dialog.Close();

            };

            cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            dialog.Closed += (_, _) => tcs.TrySetResult(null);

            await dialog.ShowDialog(desktop.MainWindow).ConfigureAwait(true);

            return await tcs.Task.ConfigureAwait(true);

        });

    }

    public Task<NewPromptInputs?> PromptNewPromptAsync(Guid? campaignId, string? campaignName, CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<NewPromptInputs?>(null);

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            TextBox nameBox = new() { Watermark = "Prompt name (required)" };

            TextBox versionBox = new() { Watermark = "Version (required)" };

            TextBox descBox = new() { Watermark = "Description (optional)" };

            TextBox templateBox = new()
            {
                Watermark = "Template (defaults to a stub if blank)",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 80,
            };

            TextBlock error = new() { TextWrapping = TextWrapping.Wrap };

            Button ok = new() { Content = "Create", IsDefault = true };

            Button cancel = new() { Content = "Cancel", IsCancel = true };

            TaskCompletionSource<NewPromptInputs?> tcs = new();

            Window dialog = new()
            {
                Title = campaignName is null ? "Create Global Prompt" : $"Create Prompt in {campaignName}",
                Width = 460,
                Height = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = "Name" },
                        nameBox,
                        new TextBlock { Text = "Version" },
                        versionBox,
                        new TextBlock { Text = "Description" },
                        descBox,
                        new TextBlock { Text = "Template" },
                        templateBox,
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

                string version = versionBox.Text?.Trim() ?? string.Empty;

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version))
                {

                    error.Text = "Name and version are required.";

                    return;

                }

                string template = string.IsNullOrWhiteSpace(templateBox.Text)
                    ? $"# {name}{Environment.NewLine}{Environment.NewLine}"
                    : templateBox.Text;

                tcs.TrySetResult(new NewPromptInputs(name, version, descBox.Text?.Trim(), template));

                dialog.Close();

            };

            cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            dialog.Closed += (_, _) => tcs.TrySetResult(null);

            await dialog.ShowDialog(desktop.MainWindow).ConfigureAwait(true);

            return await tcs.Task.ConfigureAwait(true);

        });

    }

    public Task<NewSessionInputs?> PromptNewSessionAsync(Guid? campaignId, string? campaignName, CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<NewSessionInputs?>(null);

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            TextBox titleBox = new() { Watermark = "Title (optional)" };

            Button ok = new() { Content = "Create", IsDefault = true };

            Button cancel = new() { Content = "Cancel", IsCancel = true };

            TaskCompletionSource<NewSessionInputs?> tcs = new();

            Window dialog = new()
            {
                Title = campaignName is null ? "Create Session" : $"Create Session in {campaignName}",
                Width = 440,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = "Title" },
                        titleBox,
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

                tcs.TrySetResult(new NewSessionInputs(titleBox.Text?.Trim()));

                dialog.Close();

            };

            cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            dialog.Closed += (_, _) => tcs.TrySetResult(null);

            await dialog.ShowDialog(desktop.MainWindow).ConfigureAwait(true);

            return await tcs.Task.ConfigureAwait(true);

        });

    }

}
