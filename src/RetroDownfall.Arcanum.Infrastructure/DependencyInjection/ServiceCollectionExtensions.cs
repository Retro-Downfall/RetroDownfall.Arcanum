using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Workspace;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Pattern;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
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
            throw new PlatformNotSupportedException(
                "Arcanum daemon management is only supported on Windows, macOS, and Linux.");
        }

        return services;
    }

    /// <summary>

    /// Registers Serilog file logging, Data Protection, the secret store, encrypted Grimoire database, and workspace scanning.

    /// </summary>

    public static IServiceCollection AddArcanumInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddArcanumEyeOfTheWorld();

        services.AddArcanumSerilog();

        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));

        services.AddDataProtection().SetApplicationName("ArcanumCore");

        services.AddSingleton<ISecretStore, DataProtectionSecretStore>();

        services.AddSingleton<IGrimoireDbPassphraseSource, GrimoireDbPassphraseSource>();

        services.AddHostedService<GrimoireDatabaseHostedService>();

        services.AddDbContext<ArcanumDbContext>();

        services.AddScoped<IGrimoireRepository, GrimoireRepository>();

        services.AddSingleton<IWorkspaceScanner, PhysicalWorkspaceScanner>();

        services.AddSingleton<McpConnectionManager>();

        return services;
    }
}
