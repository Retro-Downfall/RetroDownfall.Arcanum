using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.ViewModels;

namespace RetroDownfall.Compendium.Ux.Views;

public partial class AppShell : ContentPage
{

    private readonly ConfigurationViewModel _viewModel;

    private readonly IServiceProvider _services;

    public AppShell(ConfigurationViewModel viewModel, IServiceProvider services)
    {

        InitializeComponent();

        _viewModel = viewModel;

        _services = services;

        BindingContext = viewModel;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        SetContentPage(viewModel.SelectedSection);

    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {

        if (e.PropertyName == nameof(ConfigurationViewModel.SelectedSection))

        {

            SetContentPage(_viewModel.SelectedSection);

        }

    }

    private void SetContentPage(ConfigSection section)
    {

        Page page = section switch
        {

            ConfigSection.Host => _services.GetRequiredService<HostPage>(),

            ConfigSection.Server => _services.GetRequiredService<ServerPage>(),

            ConfigSection.Providers => _services.GetRequiredService<ProvidersPage>(),

            ConfigSection.Intelligence => _services.GetRequiredService<IntelligencePage>(),

            ConfigSection.Mcp => _services.GetRequiredService<McpPage>(),

            ConfigSection.LlamaCpp => _services.GetRequiredService<LlamaCppPage>(),

            ConfigSection.Orchestration => _services.GetRequiredService<OrchestrationPage>(),

            ConfigSection.Security => _services.GetRequiredService<SecurityPage>(),

            ConfigSection.CommLink => _services.GetRequiredService<CommLinkPage>(),

            ConfigSection.Storage => _services.GetRequiredService<StoragePage>(),

            ConfigSection.Forge => _services.GetRequiredService<ForgePage>(),

            ConfigSection.ProvingGrounds => _services.GetRequiredService<ProvingGroundsPage>(),

            ConfigSection.Cli => _services.GetRequiredService<CliPage>(),

            ConfigSection.Scrying => _services.GetRequiredService<ScryingPage>(),

            _ => _services.GetRequiredService<HostPage>(),

        };

        ContentHost.Content = page;

    }

}
