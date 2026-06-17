using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Workspace;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.CommLink;
using RetroDownfall.Arcanum.Infrastructure.Chronosync;
using RetroDownfall.Arcanum.Infrastructure.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Daemons;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Pattern;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Theme;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.Workspace;
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
    /// Registers daemon job registry, execution history, runner, config-backed <see cref="IDaemonJob"/> instances, and the Unseen Servant scheduler.
    /// </summary>
    public static IServiceCollection AddArcanumDaemonServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IDaemonExecutionRepository, InMemoryDaemonExecutionRepository>();

        services.AddSingleton<DaemonJobRegistry>();

        services.AddSingleton<IDaemonRegistry>(static sp => sp.GetRequiredService<DaemonJobRegistry>());

        services.AddSingleton<IDaemonRunner, DaemonRunner>();

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

        services.AddHostedService<PidFileService>();

        services.AddSingleton<ConfigurationWriter>();

        services.AddSingleton<ConfigurationValidator>();

        services.AddArcanumEyeOfTheWorld();

        services.AddArcanumSerilog();

        services.AddSingleton<InMemoryLogRingBuffer>();

        services.AddSingleton<ILogRingBuffer>(static sp => sp.GetRequiredService<InMemoryLogRingBuffer>());

        services.AddSingleton<ILogQueryService, LogQueryService>();

        services.AddSingleton<IDaemonLogAttacher, DaemonLogAttacher>();

        services.AddSingleton<SerilogLogRingBufferSink>();

        services.AddDataProtection()
            .SetApplicationName("ArcanumCore")
            .PersistKeysToFileSystem(DataProtectionKeyPaths.EnsureDirectory());

        services.AddSingleton<ConfigurationSecretProtector>();

        services.AddSingleton<ISecretStore, DataProtectionSecretStore>();
        services.AddSingleton<IWard, WardGate>();
        services.AddSingleton<SanctumBreachStore>();
        services.AddScoped<ISanctumGuard, SanctumGuard>();
        services.AddSingleton<IGrimoireDbPassphraseSource, GrimoireDbPassphraseSource>();
        services.AddSingleton<IGrimoireDbReadiness, GrimoireDbReadiness>();
        services.AddHostedService<GrimoireDatabaseHostedService>();

        services.AddDbContextPool<ArcanumDbContext>(_ => { }, poolSize: 32);

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IGrimoireRepository, GrimoireRepository>();
        services.AddScoped<ICampaignRepository, CampaignRepository>();
        services.AddScoped<IPromptRepository, PromptRepository>();
        services.AddScoped<IApprenticeRepository, ApprenticeRepository>();
        services.AddScoped<IChronosyncEngine, ChronosyncEngine>();
        services.AddSingleton<CampaignLoggerQueue>();
        services.AddSingleton<ICampaignLoggerQueue>(sp => sp.GetRequiredService<CampaignLoggerQueue>());
        services.AddHostedService<CampaignLoggerBackgroundService>();
        services.AddSingleton<ChronicleHub>();
        services.AddSingleton<ApprenticeService>();
        services.AddSingleton<IApprenticeRuntime>(static sp => sp.GetRequiredService<ApprenticeService>());
        services.AddHostedService(static sp => sp.GetRequiredService<ApprenticeService>());
        services.AddSingleton<IWorkspaceScanner, PhysicalWorkspaceScanner>();
        services.AddSingleton<IUnseenServantPacer, UnseenServantPacer>();
        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventBus>(static sp => sp.GetRequiredService<InMemoryEventBus>());

        services.AddHttpClient(
            WebhookCommLinkDispatcher.HttpClientName,
            (sp, client) =>
            {
                IOptionsMonitor<ArcanumSettings> opts = sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

                int timeoutSeconds = ArcanumSettingClamps.WebhookTimeoutSeconds(
                    opts.CurrentValue.CommLink?.WebhookTimeoutSeconds ?? new CommLinkSettings().WebhookTimeoutSeconds);

                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
            {
                AllowAutoRedirect = false,
            });

        services.AddSingleton<WebhookCommLinkDispatcher>();

        services.AddSingleton<ICommLinkDispatcher>(static sp =>
        {
            WebhookCommLinkDispatcher webhook = sp.GetRequiredService<WebhookCommLinkDispatcher>();

            IReadOnlyList<ICommLinkDispatcher> sinks = [webhook];

            return new CommLinkMultiplexer(sinks);
        });

        services.AddSingleton<ITrustedMcpWorkspaceStore, TrustedMcpWorkspaceStore>();

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

        services.AddSingleton<ISpellRepository, SpellRepository>();

        services.AddHttpClient(
            GgufModelCache.HttpClientName,
            (sp, client) =>
            {
                IOptionsMonitor<ArcanumSettings> opts = sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

                int timeoutSeconds = ArcanumSettingClamps.LlamaModelDownloadTimeoutSeconds(
                    opts.CurrentValue.LlamaCpp?.ModelDownloadTimeoutSeconds ?? new LlamaCppSettings().ModelDownloadTimeoutSeconds);

                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
            {
                AllowAutoRedirect = false,
            });

        services.AddSingleton<IGgufModelCache, GgufModelCache>();

        services.AddSingleton<LlamaServerManager>();

        services.AddSingleton<ILlamaServerManager>(static sp => sp.GetRequiredService<LlamaServerManager>());

        services.AddHostedService<LlamaServerLifecycleHostedService>();

        return services;
    }
}
