using CommunityToolkit.Maui;
using RetroDownfall.Compendium.Ux.Services;
using RetroDownfall.Compendium.Ux.ViewModels;
using RetroDownfall.Compendium.Ux.Views;

namespace RetroDownfall.Compendium.Ux;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit();

        builder.Services.AddSingleton<IArcanumSecretProtector, ArcanumDataProtectionSecretProtector>();

        builder.Services.AddSingleton<IArcanumConfigurationStore, ArcanumConfigurationStore>();

        builder.Services.AddSingleton<IDialogService, DialogService>();

        builder.Services.AddSingleton<ConfigurationViewModel>();

        builder.Services.AddTransient<AppShell>();

        RegisterSection<HostPage, HostSectionViewModel>(builder.Services);

        RegisterSection<ServerPage, ServerSectionViewModel>(builder.Services);

        RegisterSection<ProvidersPage, ProvidersSectionViewModel>(builder.Services);

        RegisterSection<IntelligencePage, IntelligenceSectionViewModel>(builder.Services);

        RegisterSection<McpPage, McpSectionViewModel>(builder.Services);

        RegisterSection<LlamaCppPage, LlamaCppSectionViewModel>(builder.Services);

        RegisterSection<OrchestrationPage, OrchestrationSectionViewModel>(builder.Services);

        RegisterSection<SecurityPage, SecuritySectionViewModel>(builder.Services);

        RegisterSection<CommLinkPage, CommLinkSectionViewModel>(builder.Services);

        RegisterSection<StoragePage, StorageSectionViewModel>(builder.Services);

        RegisterSection<ForgePage, ForgeSectionViewModel>(builder.Services);

        RegisterSection<ProvingGroundsPage, ProvingGroundsSectionViewModel>(builder.Services);

        RegisterSection<CliPage, CliSectionViewModel>(builder.Services);

        RegisterSection<ScryingPage, ScryingSectionViewModel>(builder.Services);

        return builder.Build();
    }

    private static void RegisterSection<TPage, TViewModel>(IServiceCollection services)
        where TPage : class
        where TViewModel : class
    {
        services.AddTransient<TPage>();

        services.AddTransient<TViewModel>();
    }
}
