using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.TheForge.Ux.ViewModels.Setup;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>Avalonia modal host for <see cref="SetupWizardViewModel"/>.</summary>
public sealed class AvaloniaSetupWizardDialogService : ISetupWizardDialogService
{

    private readonly IServiceProvider _services;

    public AvaloniaSetupWizardDialogService(IServiceProvider services)
    {

        _services = services;

    }

    public Task ShowAsync(CancellationToken cancellationToken = default)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.CompletedTask;

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            SetupWizardViewModel viewModel = _services.GetRequiredService<SetupWizardViewModel>();

            TextBlock stepTitle = new() { FontWeight = FontWeight.SemiBold, FontSize = 16, Text = viewModel.StepTitle };

            TextBlock stepDescription = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Text = viewModel.StepDescription };

            TextBox baseUrl = new() { PlaceholderText = "http://localhost:5001", Text = viewModel.BaseUrl };

            TextBox apiKey = new() { PlaceholderText = "Paste API key", PasswordChar = '•' };

            TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };

            TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };

            TextBlock detail = new() { TextWrapping = TextWrapping.Wrap };

            TextBlock configHint = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Text = viewModel.ConfigPathHint };

            TextBlock compendiumMessage = new() { TextWrapping = TextWrapping.Wrap };

            Button close = new() { Content = "Close", IsCancel = true };

            Button back = new() { Content = "Back" };

            Button next = new() { Content = "Next", IsDefault = true };

            Button test = new() { Content = "Test connection" };

            Button openCompendium = new() { Content = "Open Compendium" };

            Button skipEmbeddings = new() { Content = "Skip embeddings" };

            void Sync()
            {

                stepTitle.Text = viewModel.StepTitle;

                stepDescription.Text = viewModel.StepDescription;

                status.Text = viewModel.StatusText ?? string.Empty;

                error.Text = viewModel.ErrorText ?? string.Empty;

                configHint.Text = viewModel.ConfigPathHint ?? string.Empty;

                compendiumMessage.Text = viewModel.CompendiumMessage ?? string.Empty;

                detail.Text = string.Join(
                    Environment.NewLine,
                    new[]
                    {
                        viewModel.ProvidersSummary,
                        viewModel.ModelsSummary,
                        viewModel.DefaultModelSummary,
                        viewModel.FastModelSummary,
                        viewModel.EmbeddingsSummary,
                    }.Where(static s => !string.IsNullOrWhiteSpace(s)));

                baseUrl.IsVisible = viewModel.Step == SetupWizardStep.BaseUrl;

                apiKey.IsVisible = viewModel.Step == SetupWizardStep.ApiKey;

                test.IsVisible = viewModel.Step == SetupWizardStep.TestConnection;

                skipEmbeddings.IsVisible = viewModel.Step == SetupWizardStep.Embeddings;

                back.IsVisible = viewModel.CanGoBack;

                next.IsVisible = viewModel.CanGoNext;

                next.IsEnabled = !viewModel.IsBusy;

                test.IsEnabled = !viewModel.IsBusy;

            }

            baseUrl.TextChanged += (_, _) => viewModel.BaseUrl = baseUrl.Text ?? string.Empty;

            apiKey.TextChanged += (_, _) => viewModel.ApiKeyInput = apiKey.Text ?? string.Empty;

            viewModel.PropertyChanged += (_, _) => Sync();

            Sync();

            back.Click += async (_, _) =>
            {

                if (viewModel.BackCommand.CanExecute(null))
                {

                    await viewModel.BackCommand.ExecuteAsync(null).ConfigureAwait(true);

                }

            };

            next.Click += async (_, _) =>
            {

                if (viewModel.NextCommand.CanExecute(null))
                {

                    await viewModel.NextCommand.ExecuteAsync(null).ConfigureAwait(true);

                }

            };

            test.Click += async (_, _) =>
            {

                if (viewModel.TestConnectionCommand.CanExecute(null))
                {

                    await viewModel.TestConnectionCommand.ExecuteAsync(null).ConfigureAwait(true);

                }

            };

            skipEmbeddings.Click += (_, _) => viewModel.SkipEmbeddingsCommand.Execute(null);

            openCompendium.Click += (_, _) => viewModel.OpenCompendiumCommand.Execute(null);

            Window dialog = new()
            {
                Title = "The Forge — Setup",
                Width = 560,
                Height = 640,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer
                {
                    Content = new StackPanel
                    {
                        Margin = new Thickness(16),
                        Spacing = 10,
                        Children =
                        {
                            stepTitle,
                            stepDescription,
                            baseUrl,
                            apiKey,
                            test,
                            detail,
                            skipEmbeddings,
                            status,
                            error,
                            new TextBlock { Text = "Config path:", Opacity = 0.6 },
                            configHint,
                            openCompendium,
                            compendiumMessage,
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                HorizontalAlignment = HorizontalAlignment.Right,
                                Spacing = 8,
                                Children = { close, back, next },
                            },
                        },
                    },
                },
            };

            TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            close.Click += (_, _) => dialog.Close();

            dialog.Closed += (_, _) => tcs.TrySetResult();

            await using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {

                tcs.TrySetCanceled(cancellationToken);

                try
                {

                    dialog.Close();

                }
                catch
                {
                }

            });

            await dialog.ShowDialog(desktop.MainWindow).ConfigureAwait(true);

            await tcs.Task.ConfigureAwait(true);

        });

    }

}
