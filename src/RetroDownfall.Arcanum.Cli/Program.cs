using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Commands.Daemon;
using RetroDownfall.Arcanum.Cli.Commands.Llama;
using RetroDownfall.Arcanum.Cli.Commands.Lore;
using RetroDownfall.Arcanum.Cli.Commands.TheForge;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli;

[ExcludeFromCodeCoverage] // Reason: Spectre.Console.Cli entrypoint; command wiring is covered via CliApplicationFactory and command unit tests.
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
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(DoctorCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(KeyShowCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DaemonInitiativeCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(DaemonInitiativeCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DaemonAlertCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(DaemonAlertCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LoreListCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LoreGetCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(LoreGetCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LoreSetCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(LoreSetCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LoreDeleteCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(LoreDeleteCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LlamaPullCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(LlamaPullCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LlamaStartCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(LlamaStartCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LlamaStopCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(LlamaStopCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(LlamaStatusCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellSearchCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PromptRenderCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeCreateCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeStartCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeChronicleCommand))]
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

        AppContext.SetSwitch("Microsoft.AspNetCore.Mvc.ApiExplorer.IsEnhancedModelMetadataSupportEnabled", false);

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        configuration.AddArcanumConfiguration();

        CliApplicationFactory.ConfigureAnsiConsoleForEnvironment(configuration);

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        CommandApp app = CliApplicationFactory.BuildCommandApp(services);

        string[] argv = args.Length == 0 ? ["--help"] : args;

        return await app.RunAsync(argv);
    }

}
