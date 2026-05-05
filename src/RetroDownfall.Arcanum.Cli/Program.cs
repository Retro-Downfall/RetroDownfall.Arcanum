using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Commands.Daemon;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;
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
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ArcanumApiClient))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CliTypeRegistrar))]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Spectre.Console.Cli is reflection-based; TrimmerRootAssembly + DynamicDependency attributes preserve the required types.")]

    public static async Task<int> Main(string[] args)
    {
        ServiceCollection services = new();

        services.AddDataProtection().SetApplicationName("ArcanumCore");

        services.AddSingleton<ISecretStore, DataProtectionSecretStore>();

        services.AddHttpClient("ArcanumApi", client => client.BaseAddress = new Uri("http://localhost:5001/"));

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

        CliTypeRegistrar registrar = new(services);

        CommandApp app = new(registrar);

        app.Configure(config =>
        {
            config.AddCommand<ServeCommand>("serve")
                .WithDescription("Hosts the Arcanum Minimal API on http://localhost:5001.");

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

            });

        });

        var argv = args.Length == 0 ? new[] { "--help" } : args;

        return await app.RunAsync(argv);
    }
}
