using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Markdown;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Services;
using RetroDownfall.TheForge.Ux.Services.Terminal;
using RetroDownfall.TheForge.Ux.Services.Whispers;
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
using RetroDownfall.TheForge.Ux.ViewModels.Archive;
using RetroDownfall.TheForge.Ux.ViewModels.Divination;
using RetroDownfall.TheForge.Ux.ViewModels.Lore;
using RetroDownfall.TheForge.Ux.ViewModels.WorkspaceExplorer;

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

        services.AddOptions<TheForgeSettings>().Bind(configuration);

        services.AddSingleton<ITheForgeSettingsStore>(sp => new TheForgeSettingsStore(
            forgeJsonPath,
            sp.GetService<ILogger<TheForgeSettingsStore>>()));

        services.AddLogging(builder => builder.AddDebug());

        services.AddHttpClient(ArcanumApiClient.HttpClientName, static client =>
        {
            // Unary Forge ↔ Arcanum API calls — keep a finite budget (HttpClient default is 100s).
            // Do not use InfiniteTimeSpan here; inference/streaming uses separate clients on the API side.
            client.Timeout = TimeSpan.FromSeconds(100);
        });

        // Must use the parameterless-ctor factory, not AddSingleton<IOsCredentialStore, OsCredentialStore>():
        // the generic overload lets the container pick OsCredentialStore's test-seam constructor
        // (OsCredentialStore(IOsCredentialStore inner)), which requests IOsCredentialStore again and
        // self-cycles at resolution time (instant quit before MainWindow).
        services.AddSingleton<IOsCredentialStore>(static _ => new OsCredentialStore());

        services.AddSingleton<ApiKeyResolver>();

        services.AddSingleton<IApiKeyPrompt, AvaloniaApiKeyPrompt>();

        services.AddSingleton<ITheForgeApiKeyProvider>(static sp => new TheForgeApiKeyProvider(
            sp.GetRequiredService<ApiKeyResolver>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<TheForgeSettings>>(),
            sp.GetRequiredService<ILogger<TheForgeApiKeyProvider>>(),
            ct => sp.GetRequiredService<IApiKeyPrompt>().PromptForApiKeyAsync(ct)));

        services.AddSingleton<ArcanumApiClient>();

        services.AddSingleton<ArcanumSseClient>();

        services.AddSingleton<ArcanumConnectionService>();

        services.AddSingleton<IArcanumConnection>(sp => sp.GetRequiredService<ArcanumConnectionService>());

        services.AddSingleton<INavigationService, NavigationService>();

        services.AddSingleton<IAtelierDataSource, AtelierDataSource>();

        services.AddSingleton<ISpellEditorDataSource, SpellEditorDataSource>();

        services.AddSingleton<IPromptEditorDataSource, PromptEditorDataSource>();

        services.AddSingleton<ITomeDataSource, TomeDataSource>();

        services.AddSingleton<IArtifactCreationDataSource, ArtifactCreationDataSource>();

        services.AddSingleton<ICampaignManagementDataSource, CampaignManagementDataSource>();

        services.AddSingleton<IArtifactCreationDialogService, AvaloniaArtifactCreationDialogService>();

        services.AddSingleton<ICampaignDialogService, AvaloniaCampaignDialogService>();

        services.AddSingleton<IConfirmationDialogService, AvaloniaConfirmationDialogService>();

        services.AddSingleton<IArtifactFileDialogService, AvaloniaArtifactFileDialogService>();

        services.AddSingleton<ITextInputDialogService, AvaloniaTextInputDialogService>();

        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();

        services.AddSingleton<IGatehouseDataSource, GatehouseDataSource>();

        services.AddSingleton<IAnvilDataSource, AnvilDataSource>();

        services.AddSingleton<IArsenalDataSource, ArsenalDataSource>();

        services.AddSingleton<IModelsProvidersDataSource, ModelsProvidersDataSource>();

        services.AddSingleton<ITreasuryDataSource, TreasuryDataSource>();

        services.AddSingleton<ILoreDataSource, LoreDataSource>();

        services.AddSingleton<ISagaArchiveDataSource, SagaArchiveDataSource>();

        services.AddSingleton<IDivinationDataSource, DivinationDataSource>();

        services.AddSingleton<IWorkspaceExplorerDataSource, WorkspaceExplorerDataSource>();

        services.AddSingleton<ICodexDataSource, CodexDataSource>();

        services.AddSingleton<ITrialDataSource, TrialDataSource>();

        services.AddSingleton<IMarkdownDocumentContentStore, MarkdownDocumentContentStore>();

        services.AddSingleton<ThemeApplicationService>();

        services.AddSingleton<IWhispersClock, SystemWhispersClock>();

        services.AddSingleton<IUiThreadDispatcher, AvaloniaUiThreadDispatcher>();

        services.AddSingleton<IWhispersService, WhispersService>();

        services.AddSingleton<IWarTableDataSource, WarTableDataSource>();

        services.AddSingleton<IWorkbenchDocumentFactory, WorkbenchDocumentFactory>();

        services.AddSingleton<ITerminalShellResolver, TerminalShellResolver>();

        services.AddSingleton<ITerminalCommandRunner, TerminalCommandRunner>();

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

        services.AddTransient<McpServersViewModel>();

        services.AddTransient<ScryingPoolViewModel>();

        services.AddTransient<ModelsProvidersViewModel>();

        // Singleton so Workbench documents (The Tome) and the shell share one log surface.
        services.AddSingleton<FoundryFloorViewModel>();

        services.AddTransient<HearthViewModel>();

        services.AddTransient<AnvilViewModel>();

        services.AddTransient<LoreBrowserViewModel>();

        services.AddTransient<SagaArchiveViewModel>();

        services.AddTransient<DivinationViewModel>();

        services.AddTransient<WorkspaceExplorerViewModel>();

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

        services.AddSingleton<ToolInvokeService>();

        services.AddSingleton<LoreService>();

        services.AddSingleton<SagaService>();

        services.AddSingleton<ConfigService>();

        services.AddSingleton<ModelService>();

        services.AddSingleton<WorkspaceService>();

        services.AddSingleton<DivinationService>();

        services.AddSingleton<CommLinkService>();

        services.AddSingleton<SanctumService>();

        services.AddSingleton<ExportImportService>();

        services.AddSingleton<ILogService>(sp => sp.GetRequiredService<LogService>());

        services.AddSingleton<LogService>();

        services.AddSingleton<AuditService>();

        services.AddSingleton<DaemonService>();

    }

}
