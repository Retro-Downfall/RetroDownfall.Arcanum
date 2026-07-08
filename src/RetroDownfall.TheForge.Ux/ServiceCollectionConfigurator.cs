using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.Anvil;
using RetroDownfall.TheForge.Ux.ViewModels.Arsenal;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Gatehouse;
using RetroDownfall.TheForge.Ux.ViewModels.Hearth;
using RetroDownfall.TheForge.Ux.ViewModels.Treasury;
using RetroDownfall.TheForge.Ux.ViewModels.WarTable;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;

namespace RetroDownfall.TheForge.Ux;

/// <summary>
/// Builds the app-wide <see cref="ServiceProvider"/>: <c>forge.json</c> configuration (with
/// <c>reloadOnChange: true</c> so <see cref="IOptionsMonitor{TOptions}"/> subscribers see live edits),
/// the Arcanum HTTP client stack, every per-route service, navigation, and root ViewModels.
/// </summary>
internal static class ServiceCollectionConfigurator
{

    public static ServiceProvider Build()
    {

        ServiceCollection services = new();

        string forgeJsonPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "forge.json");

        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(forgeJsonPath, optional: true, reloadOnChange: true)
            .Build();

        services.AddSingleton(configuration);

        services.AddOptions<ForgeSettings>().Bind(configuration);

        services.AddLogging(builder => builder.AddDebug());

        services.AddHttpClient(ArcanumApiClient.HttpClientName);

        services.AddSingleton<ApiKeyResolver>();

        services.AddSingleton<ArcanumApiClient>();

        services.AddSingleton<ArcanumSseClient>();

        services.AddSingleton<ArcanumConnectionService>();

        services.AddSingleton<IArcanumConnection>(sp => sp.GetRequiredService<ArcanumConnectionService>());

        services.AddSingleton<INavigationService, NavigationService>();

        services.AddSingleton<IAtelierDataSource, AtelierDataSource>();

        services.AddSingleton<ISpellEditorDataSource, SpellEditorDataSource>();

        services.AddSingleton<ITomeDataSource, TomeDataSource>();

        services.AddSingleton<IWorkbenchDocumentFactory, WorkbenchDocumentFactory>();

        RegisterRouteServices(services);

        RegisterViewModels(services);

        services.AddTransient<MainViewModel>();

        return services.BuildServiceProvider();

    }

    private static void RegisterViewModels(ServiceCollection services)
    {

        services.AddTransient<AtelierViewModel>();

        services.AddTransient<WarTableViewModel>();

        services.AddTransient<GatehouseViewModel>();

        services.AddTransient<TreasuryViewModel>();

        services.AddTransient<ArsenalViewModel>();

        // Singleton so Workbench documents (The Tome) and the shell share one log surface.
        services.AddSingleton<FoundryFloorViewModel>();

        services.AddTransient<HearthViewModel>();

        services.AddTransient<AnvilViewModel>();

    }

    private static void RegisterRouteServices(ServiceCollection services)
    {

        services.AddSingleton<HealthService>();

        services.AddSingleton<BudgetService>();

        services.AddSingleton<CampaignService>();

        services.AddSingleton<SpellService>();

        services.AddSingleton<PromptService>();

        services.AddSingleton<SessionService>();

        services.AddSingleton<ApprenticeService>();

        services.AddSingleton<WardService>();

        services.AddSingleton<TrialService>();

        services.AddSingleton<McpService>();

        services.AddSingleton<LlamaService>();

        services.AddSingleton<LoreService>();

        services.AddSingleton<SagaService>();

        services.AddSingleton<ConfigService>();

        services.AddSingleton<ModelService>();

        services.AddSingleton<WorkspaceService>();

        services.AddSingleton<DivinationService>();

        services.AddSingleton<CommLinkService>();

        services.AddSingleton<SanctumService>();

        services.AddSingleton<ExportImportService>();

        services.AddSingleton<LogService>();

        services.AddSingleton<AuditService>();

        services.AddSingleton<DaemonService>();

    }

}
