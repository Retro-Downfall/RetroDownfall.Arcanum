using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Workspace;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Chronosync;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Pattern;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Theme;
using RetroDownfall.Arcanum.Infrastructure.Workspace;

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
    /// Registers the Unseen Servant background scheduler (minute-based headless inference jobs).
    /// </summary>
    public static IServiceCollection AddArcanumDaemonServices(this IServiceCollection services)
    {
        services.AddHostedService<UnseenServantService>();

        return services;
    }

    /// <summary>
    /// Registers Serilog file logging, Data Protection, the secret store, encrypted Grimoire database, and workspace scanning.
    /// </summary>
    public static IServiceCollection AddArcanumInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));

        services.AddArcanumEyeOfTheWorld();

        services.AddArcanumSerilog();
        services.AddDataProtection().SetApplicationName("ArcanumCore");
        services.AddSingleton<ISecretStore, DataProtectionSecretStore>();
        services.AddSingleton<IGrimoireDbPassphraseSource, GrimoireDbPassphraseSource>();
        services.AddHostedService<GrimoireDatabaseHostedService>();
        services.AddDbContext<ArcanumDbContext>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IGrimoireRepository, GrimoireRepository>();
        services.AddScoped<IChronosyncEngine, ChronosyncEngine>();
        services.AddSingleton<CampaignLoggerQueue>();
        services.AddSingleton<ICampaignLoggerQueue>(sp => sp.GetRequiredService<CampaignLoggerQueue>());
        services.AddHostedService<CampaignLoggerBackgroundService>();
        services.AddSingleton<IWorkspaceScanner, PhysicalWorkspaceScanner>();
        services.AddSingleton<IUnseenServantPacer, UnseenServantPacer>();
        services.AddSingleton<McpConnectionManager>();
        return services;
    }
}
