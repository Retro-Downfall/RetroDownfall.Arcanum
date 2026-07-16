using A2A;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.CommLink;
using RetroDownfall.Arcanum.Infrastructure.Chronosync;
using RetroDownfall.Arcanum.Infrastructure.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Daemons;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Pattern;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Resilience;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Infrastructure.Telemetry;
using RetroDownfall.Arcanum.Infrastructure.Theme;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEyeOfTheWorld"/> without pulling the full infrastructure stack (for example the CLI <c>look</c> command).
    /// </summary>
    public static IServiceCollection AddArcanumEyeOfTheWorld(this IServiceCollection services)
    {
        services.AddSingleton<IEyeOfTheWorld, EyeOfTheWorldService>();
        return services;
    }

    /// <summary>
    /// Registers OS-level light/dark preference detection for CLI theming (narrow registration; no Spectre).
    /// </summary>
    public static IServiceCollection AddArcanumThemeDetection(this IServiceCollection services)
    {
        services.AddSingleton<IThemeDetector, ThemeDetector>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="IDaemonManager"/> for Windows Service (<c>sc.exe</c>), macOS launchd, or Linux systemd user units (narrow registration; does not pull EF Core, Serilog file logging, or Grimoire).
    /// </summary>
    public static IServiceCollection AddArcanumDaemonManagement(this IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IDaemonManager, WindowsDaemonManager>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IDaemonManager, MacOsDaemonManager>();
        }
        else if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<IDaemonManager, LinuxDaemonManager>();
        }
        else
        {
            throw new PlatformNotSupportedException("Arcanum daemon management is only supported on Windows, macOS, and Linux.");
        }

        return services;
    }

    /// <summary>
    /// Registers Grimoire EF Core, scoped repository and Chronosync engine, and a one-shot CLI bootstrap gate (no Serilog file logging, MCP, or campaign logger).
    /// </summary>
    public static IServiceCollection AddArcanumGrimoireForCli(this IServiceCollection services)
    {
        services.AddSingleton<IGrimoireDbPassphraseSource, GrimoireDbPassphraseSource>();

        services.AddSingleton(TimeProvider.System);

        services.AddDbContext<ArcanumDbContext>();

        services.AddScoped<IGrimoireRepository, GrimoireRepository>();

        services.AddScoped<IChronosyncEngine, ChronosyncEngine>();

        services.AddSingleton<IGrimoireCliInitialization, GrimoireCliInitialization>();

        return services;
    }

    /// <summary>
    /// Registers the OS keychain–backed master API key store with Data Protection fallback/mirror,
    /// plus the concrete <see cref="DataProtectionSecretStore"/> used for Grimoire encryption secrets.
    /// </summary>
    public static IServiceCollection AddArcanumSecretStore(this IServiceCollection services)
    {

        // Must use the parameterless-ctor factory, not TryAddSingleton<IOsCredentialStore, OsCredentialStore>():
        // the generic overload lets the container pick OsCredentialStore's test-seam constructor
        // (OsCredentialStore(IOsCredentialStore inner)), which requests IOsCredentialStore again and
        // self-cycles at resolution time.
        services.TryAddSingleton<IOsCredentialStore>(static _ => new OsCredentialStore());

        services.AddSingleton<DataProtectionSecretStore>();

        services.AddSingleton<ISecretStore>(static sp => new OsKeychainSecretStore(
            sp.GetRequiredService<IOsCredentialStore>(),
            sp.GetRequiredService<DataProtectionSecretStore>(),
            sp.GetRequiredService<IApiKeyDigestCache>(),
            sp.GetService<ILogger<OsKeychainSecretStore>>()));

        return services;

    }

    /// <summary>
    /// W6.4: the minimal secret/grimoire stack the CLI shares with the API host — Data Protection,
    /// the API-key digest cache, the OS-keychain-backed secret store, and the CLI Grimoire. Owned
    /// here (next to <see cref="AddArcanumInfrastructure"/>) so the CLI wiring cannot silently drift
    /// out of sync with the host (see DX5).
    /// </summary>
    public static IServiceCollection AddArcanumCliClientStack(this IServiceCollection services)
    {
        services.AddDataProtection()
            .SetApplicationName("ArcanumCore")
            .PersistKeysToFileSystem(DataProtectionKeyPaths.EnsureDirectory());

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddArcanumSecretStore();

        services.AddArcanumGrimoireForCli();

        return services;
    }

    /// <summary>
    /// Registers daemon job registry, execution history, runner, config-backed <see cref="IDaemonJob"/> instances, and the Unseen Servant scheduler.
    /// </summary>
    public static IServiceCollection AddArcanumDaemonServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IDaemonExecutionRepository, InMemoryDaemonExecutionRepository>();

        services.AddSingleton<DaemonJobRegistry>();

        services.AddSingleton<IDaemonRegistry>(static sp => sp.GetRequiredService<DaemonJobRegistry>());

        services.AddSingleton<IDaemonRunner, DaemonRunner>();

        services.AddSingleton<UnseenServantJobTracker>();

        services.AddSingleton<IUnseenServantJobTracker>(static sp => sp.GetRequiredService<UnseenServantJobTracker>());

        List<UnseenServantJob> jobs = configuration.GetSection("Arcanum:Daemon:Jobs").Get<List<UnseenServantJob>>() ?? [];

        foreach (UnseenServantJob job in jobs)
        {
            UnseenServantJob captured = job;

            services.AddSingleton<IDaemonJob>(sp => new UnseenServantDaemonJob(captured, sp));
        }

        services.AddHostedService<UnseenServantService>();

        return services;
    }

    /// <summary>
    /// Registers Serilog file logging, Data Protection, the secret store, encrypted Grimoire database, and workspace scanning.
    /// </summary>
    public static IServiceCollection AddArcanumInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));

        services.AddSingleton<IPostConfigureOptions<ArcanumSettings>, LoreToLexiconSettingsPostConfigure>();

        services.AddHostedService<PidFileService>();

        services.AddSingleton<ConfigurationWriter>();

        services.AddSingleton<ConfigurationValidator>();

        services.AddArcanumEyeOfTheWorld();

        services.AddSingleton<InMemoryLogRingBuffer>();

        services.AddSingleton<ILogRingBuffer>(static sp => sp.GetRequiredService<InMemoryLogRingBuffer>());

        services.AddSingleton<ILogQueryService, LogQueryService>();

        services.AddSingleton<IDaemonLogAttacher, DaemonLogAttacher>();

        services.AddSingleton<SerilogLogRingBufferSink>();

        // Register the ring-buffer sink before AddArcanumSerilog so deferred first-emit resolution succeeds.
        services.AddArcanumSerilog();

        services.AddSingleton<IInferenceAuditLogger, InferenceAuditLogger>();

        services.AddSingleton<IGuardrailAuditLogger, GuardrailAuditLogger>();

        services.AddDataProtection()
            .SetApplicationName("ArcanumCore")
            .PersistKeysToFileSystem(DataProtectionKeyPaths.EnsureDirectory());

        services.AddSingleton<ConfigurationSecretProtector>();

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddArcanumSecretStore();

        services.AddSingleton<IWard, WardGate>();
        services.AddScoped<ISanctumGuard, SanctumGuard>();
        services.AddSingleton<IProcessResourceLimiter, ProcessResourceLimiter>();
        services.AddSingleton<IGrimoireDbPassphraseSource, GrimoireDbPassphraseSource>();
        services.AddSingleton<IGrimoireDbReadiness, GrimoireDbReadiness>();
        services.AddSingleton<WeaveIndexAvailability>();
        services.AddScoped<IDivinationService, DivinationService>();
        services.AddScoped<EmbeddingsResetService>();
        services.AddSingleton<SpellWeaveCache>();
        services.AddHostedService<GrimoireDatabaseHostedService>();

        // RAG Phase 2/3 — Entry Weaving and Workspace Indexing both idle (no-op) until their feature
        // flags are enabled (Arcanum:Embeddings:SessionSearchEnabled / CodebaseRetrievalEnabled), so
        // registering them unconditionally is safe on the hot path. Registered after
        // GrimoireDatabaseHostedService so the Grimoire (and The Weave's schema) is guaranteed ready
        // before either service's first tick can run any query.
        services.AddHostedService<EntryWeavingService>();

        services.AddSingleton<WorkspaceIndexingService>();
        services.AddSingleton<IWorkspaceIndexingService>(static sp => sp.GetRequiredService<WorkspaceIndexingService>());
        services.AddHostedService(static sp => sp.GetRequiredService<WorkspaceIndexingService>());

        // RAG Phase 4 — Saga extraction is event-driven (enqueued by WizardIntelligenceProvider after a
        // successful turn), not polling, so registering it unconditionally is safe on the hot path.
        // Registered as a singleton (not just a hosted service) so the hub can resolve it directly to
        // call EnqueueExtraction, mirroring WorkspaceIndexingService's singleton+hosted-factory pattern.
        services.AddSingleton<SagaExtractionService>();
        services.AddHostedService(static sp => sp.GetRequiredService<SagaExtractionService>());

        services.AddHostedService<ArcanumSettingsClampStartupLogger>();

        services.AddHostedService<ArcanumSecurityStartupChecks>();

        services.AddDbContextPool<ArcanumDbContext>(_ => { }, poolSize: 32);

        services.AddScoped<IUnseenServantWatermarkStore, UnseenServantWatermarkStore>();

        services.AddScoped<IIdempotencyStore, IdempotencyStore>();

        services.AddScoped<IUploadedFileRepository, UploadedFileRepository>();

        services.AddScoped<IBatchRepository, BatchRepository>();

        services.AddScoped<ISanctumBreachRepository, SanctumBreachRepository>();

        services.AddScoped<IBudgetAlertRepository, BudgetAlertRepository>();

        services.AddScoped<ISagaMemoryStore, SagaMemoryStore>();

        services.AddScoped<ILexiconService, LexiconService>();

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IGrimoireRepository, GrimoireRepository>();
        services.AddScoped<ICampaignRepository, CampaignRepository>();
        services.AddScoped<IPromptRepository, PromptRepository>();
        services.AddScoped<IApprenticeRepository, ApprenticeRepository>();
        services.AddScoped<IConclaveArchmage, ConclaveArchmage>();
        services.AddHttpClient(A2AClientService.OutboundHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(static () => OutboundUrlGuard.CreateUntrustedEgressHandler());
        services.AddSingleton<IA2AClientService, A2AClientService>();
        services.AddSingleton<ArcanumA2AAgentHandler>();
        services.AddSingleton(static sp => new A2AServer(
            sp.GetRequiredService<ArcanumA2AAgentHandler>(),
            new InMemoryTaskStore(),
            new ChannelEventNotifier(),
            sp.GetRequiredService<ILogger<A2AServer>>(),
            new A2AServerOptions { AutoAppendHistory = true }));
        services.AddScoped<IChronosyncEngine, ChronosyncEngine>();
        services.AddSingleton<CampaignLoggerQueue>();
        services.AddSingleton<ICampaignLoggerQueue>(sp => sp.GetRequiredService<CampaignLoggerQueue>());
        services.AddHostedService<Loremaster>();
        services.AddSingleton<ChronicleHub>();
        services.AddSingleton<ApprenticeService>();
        services.AddSingleton<IApprenticeRuntime>(static sp => sp.GetRequiredService<ApprenticeService>());
        services.AddHostedService(static sp => sp.GetRequiredService<ApprenticeService>());
        services.AddSingleton<IWorkspaceScanner, PhysicalWorkspaceScanner>();
        services.AddSingleton<IUnseenServantPacer, UnseenServantPacer>();
        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventBus>(static sp => sp.GetRequiredService<InMemoryEventBus>());
        services.AddSingleton<SseConnectionCounter>();
        services.AddSingleton<SseConnectionGate>();
        services.AddSingleton<PrometheusMetricsExporter>();

        services.AddHttpClient(
            WebhookCommLinkDispatcher.HttpClientName,
            (sp, client) =>
            {
                IOptionsMonitor<ArcanumSettings> opts = sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

                int timeoutSeconds = ArcanumSettingClamps.WebhookTimeoutSeconds(
                    opts.CurrentValue.CommLink?.WebhookTimeoutSeconds ?? new CommLinkSettings().WebhookTimeoutSeconds);

                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(static () => OutboundUrlGuard.CreateUntrustedEgressHandler());

        services.AddHttpClient(
            ArcanumBrowseWebConstants.HttpClientName,
            (sp, client) =>
            {
                IOptionsMonitor<ArcanumSettings> opts = sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

                int timeoutSeconds = ArcanumSettingClamps.WebBrowsingRequestTimeoutSeconds(
                    opts.CurrentValue.WebBrowsing?.RequestTimeoutSeconds ?? new WebBrowsingSettings().RequestTimeoutSeconds);

                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(static () => OutboundUrlGuard.CreateUntrustedEgressHandler());

        services.AddSingleton<WebhookCommLinkDispatcher>();

        services.AddSingleton<ICommLinkDispatcher>(static sp =>
        {
            WebhookCommLinkDispatcher webhook = sp.GetRequiredService<WebhookCommLinkDispatcher>();

            IReadOnlyList<ICommLinkDispatcher> sinks = [webhook];

            return new CommLinkMultiplexer(sinks);
        });

        services.AddSingleton<ITrustedMcpWorkspaceStore, TrustedMcpWorkspaceStore>();

        services.AddHttpClient(
            McpConnectionManager.McpHttpClientName,
            (sp, client) =>
            {
                IOptionsMonitor<ArcanumSettings> opts = sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

                int timeoutSeconds = ArcanumSettingClamps.McpHttpRequestTimeoutSeconds(
                    opts.CurrentValue.Mcp?.HttpRequestTimeoutSeconds ?? new McpSettings().HttpRequestTimeoutSeconds);

                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(static () => OutboundUrlGuard.CreateUntrustedEgressHandler());

        services.AddSingleton<McpConnectionManager>();

        services.AddSingleton<IMcpConnectionManager>(static sp => sp.GetRequiredService<McpConnectionManager>());

        services.AddHostedService<McpServerBootstrapHostedService>();

        services.AddSingleton<IHostWorkspaceContext, HostWorkspaceContext>();

        services.AddSingleton<IWorkspaceRegistry>(sp => new CampaignBackedWorkspaceRegistry(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IGrimoireDbReadiness>(),
            sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>()));

        services.AddScoped<ISessionRepository, SessionRepository>();

        services.AddSingleton<SessionEventHub>();

        services.AddSingleton<IFileSystemBrowser, PhysicalFileSystemBrowser>();

        services.AddScoped<IFileSystemWriter, PhysicalFileSystemWriter>();

        services.AddSingleton<ISpellRepository, SpellRepository>();

        services.AddSingleton<ISpellCastPreviewService, SpellCastPreviewService>();

        services.AddArcanumResilience();

        return services;
    }

    /// <summary>
    /// Registers the provider resilience layer: the in-memory health tracker, the connectivity probe,
    /// the periodic probe scheduler, and a dedicated <c>"ProviderHealthProbe"</c> named <see cref="HttpClient"/>
    /// (short timeout, no connection pooling — never reuses the long-lived inference clients). The probe
    /// scheduler is always registered but idles when <c>Arcanum:Resilience:Enabled</c> is <c>false</c>
    /// (the default), so this is a no-op on the hot path until an operator opts in.
    /// </summary>
    private static IServiceCollection AddArcanumResilience(this IServiceCollection services)
    {
        services.TryAddSingleton<IProviderHealthTracker, ProviderHealthTracker>();

        services.TryAddSingleton<IProviderHealthProbe, ProviderHealthProbe>();

        services.AddHttpClient(
            ProviderHealthProbe.HttpClientName,
            (sp, client) =>
            {
                IOptionsMonitor<ArcanumSettings> opts = sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

                int timeoutSeconds = ArcanumSettingClamps.HealthProbeTimeoutSeconds(
                    opts.CurrentValue.Resilience?.HealthProbeTimeoutSeconds ?? new ResilienceSettings().HealthProbeTimeoutSeconds);

                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(static () =>
            {
                SocketsHttpHandler handler = OutboundUrlGuard.CreateProviderEgressHandler();

                handler.PooledConnectionLifetime = TimeSpan.Zero;

                return handler;
            });

        services.AddHostedService<ProviderHealthProbeService>();

        return services;
    }
}
