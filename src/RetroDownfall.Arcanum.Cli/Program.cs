using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Commands.Daemon;
using RetroDownfall.Arcanum.Cli.Commands.Lore;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Theme;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli;

internal static class Program
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ServeCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AskCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AskCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ChatCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ChatCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(LookCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(InstallCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(UninstallCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(StatusCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(DaemonJobsCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DaemonInitiativeCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DaemonAlertCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LoreListCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LoreGetCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LoreSetCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LoreDeleteCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ArcanumApiClient))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CliTypeRegistrar))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CliSessionManager))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(MarkdigSpectreRenderer))]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Spectre.Console.Cli is reflection-based; TrimmerRootAssembly + DynamicDependency attributes preserve the required types.")]

    public static async Task<int> Main(string[] args)
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        configuration.AddArcanumConfiguration();

        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));

        services.AddArcanumThemeDetection();

        services.AddSingleton<IThemePalette>(sp =>
        {
            ArcanumSettings arc = sp.GetRequiredService<IOptions<ArcanumSettings>>().Value;

            CliSettings cli = arc.Cli;

            bool useDark = cli.Theme switch
            {
                ArcanumTheme.Light => false,
                ArcanumTheme.Dark => true,
                _ => sp.GetRequiredService<IThemeDetector>().SystemPrefersDark,
            };

            ThemeColors tc = cli.ThemeColors;

            ThemeSemanticColors sem = useDark ? tc.Dark : tc.Light;

            ThemeColors builtin = new();

            ThemeSemanticColors fb = useDark ? builtin.Dark : builtin.Light;

            return new ConfiguredThemePalette(sem, fb);
        });

        services.AddSingleton<CliSessionManager>();

        services.AddSingleton<MarkdigSpectreRenderer>();

        services.AddDataProtection().SetApplicationName("ArcanumCore");

        services.AddSingleton<ISecretStore, DataProtectionSecretStore>();

        services.AddArcanumGrimoireForCli();

        services.AddHttpClient(
            "ArcanumApi",
            (serviceProvider, client) =>
            {
                int port = ArcanumSettingClamps.HostPort(
                    serviceProvider.GetRequiredService<IOptions<ArcanumSettings>>().Value.Host.Port);

                client.BaseAddress = new Uri($"http://localhost:{port}/");

                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        services.AddSingleton<ArcanumApiClient>();

        services.AddArcanumEyeOfTheWorld();

        services.AddArcanumDaemonManagement();

        services.AddTransient<ServeCommand>();

        services.AddTransient<AskCommand>();

        services.AddTransient<ChatCommand>();

        services.AddTransient<LookCommand>();

        services.AddTransient<InstallCommand>();

        services.AddTransient<UninstallCommand>();

        services.AddTransient<StatusCommand>();

        services.AddTransient<DaemonJobsCommand>();

        services.AddTransient<DaemonInitiativeCommand>();

        services.AddTransient<DaemonAlertCommand>();

        services.AddTransient<LoreListCommand>();

        services.AddTransient<LoreGetCommand>();

        services.AddTransient<LoreSetCommand>();

        services.AddTransient<LoreDeleteCommand>();

        CliTypeRegistrar registrar = new(services);

        CommandApp app = new(registrar);

        app.Configure(config =>
        {
            config.AddCommand<ServeCommand>("serve")
                .WithDescription(
                    "Hosts the Arcanum Minimal API (default http://localhost:5001/; set Arcanum:Host:Port in arcanum.json).");

            config.AddCommand<AskCommand>("ask")
                .WithDescription("Ask the Mage (multi-word prompt: all words after ask, or after --; multi-turn via cli-session; --new for a fresh thread).");

            config.AddCommand<ChatCommand>("chat")
                .WithDescription("Interactive multi-turn REPL with the Mage (streamed plain text, swapped to rendered Markdown at end of turn).");

            config.AddCommand<LookCommand>("look")
                .WithDescription("Eye of the World: situational snapshot of the current directory (domain + TOC).");

            config.AddBranch("daemon", daemon =>
            {
                daemon.SetDescription("Manage the Arcanum background daemon.");

                daemon.AddCommand<InstallCommand>("install")
                    .WithDescription("Install and start the Arcanum background daemon.");

                daemon.AddCommand<UninstallCommand>("uninstall")
                    .WithDescription("Stop and uninstall the Arcanum background daemon.");

                daemon.AddCommand<StatusCommand>("status")
                    .WithDescription("Show whether the Arcanum daemon is running.");

                daemon.AddCommand<DaemonJobsCommand>("jobs")
                    .WithDescription(
                        "List Unseen Servant jobs (requires API: arcanum serve on Arcanum:Host:Port).");

                daemon.AddCommand<DaemonInitiativeCommand>("initiative")
                    .WithDescription(
                        "Set adaptive polling interval for a job (requires API: arcanum serve on Arcanum:Host:Port).");

                daemon.AddCommand<DaemonAlertCommand>("alert")
                    .WithDescription(
                        "Send a Comm Link test alert via POST /api/commlink/send (requires API: arcanum serve on Arcanum:Host:Port).");
            });

            config.AddBranch("lore", lore =>
            {
                lore.SetDescription("Manage Grimoire explicit memory (lore) directly.");

                lore.AddCommand<LoreListCommand>("list")
                    .WithDescription("List all scribed lore keys.");

                lore.AddCommand<LoreGetCommand>("get")
                    .WithDescription("Read a specific lore entry by key.");

                lore.AddCommand<LoreSetCommand>("set")
                    .WithDescription("Create or update a lore entry.");

                lore.AddCommand<LoreDeleteCommand>("delete")
                    .WithDescription("Delete a lore entry.");
            });

        });

        var argv = args.Length == 0 ? new[] { "--help" } : args;

        return await app.RunAsync(argv);
    }
}
