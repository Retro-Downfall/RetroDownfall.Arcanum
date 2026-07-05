using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Commands.Configuration;
using RetroDownfall.Arcanum.Cli.Commands.Daemon;
using RetroDownfall.Arcanum.Cli.Commands.Llama;
using RetroDownfall.Arcanum.Cli.Commands.Lore;
using RetroDownfall.Arcanum.Cli.Commands.ProvingGrounds;
using RetroDownfall.Arcanum.Cli.Commands.TheForge;
using RetroDownfall.Arcanum.Cli.Commands.Wards;
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
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DoctorCommand.Settings))]
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
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CampaignListCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignListCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CampaignGetCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignGetCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CampaignCreateCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignCreateCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CampaignUpdateCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignUpdateCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CampaignDeleteCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignDeleteCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CampaignExportCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignExportCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CampaignImportCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignImportCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CampaignCodexGetCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignCodexGetCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CampaignCodexPutCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignCodexPutCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CampaignCodexDeleteCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignCodexDeleteCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CampaignSpellsCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignSpellsCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CampaignPromptsCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignPromptsCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CampaignSessionsCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CampaignSessionsCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SessionDivinationCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SessionDivinationCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SagaListCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SagaListCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SagaDivineCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SagaDivineCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SagaDeleteCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SagaDeleteCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SagaStatsCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellListCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellListCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellGetCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellGetCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellCreateCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellCreateCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellUpdateCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellUpdateCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellDeleteCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellDeleteCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellSearchCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellSearchCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellValidateCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellValidateCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellExecuteCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellExecuteCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellVersionsCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellVersionsCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellExportCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellExportCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellImportCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellImportCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellCastCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellCastCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellCloneCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellCloneCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellVersionCreateCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellVersionCreateCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellVersionUpdateCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellVersionUpdateCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SpellVersionActivateCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SpellVersionActivateCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PromptListCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PromptListCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PromptGetCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PromptGetCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PromptVersionsCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PromptVersionsCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PromptCreateCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PromptCreateCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PromptUpdateCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PromptUpdateCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PromptDeleteCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PromptDeleteCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PromptRenderCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PromptRenderCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PromptTestCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PromptTestCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PromptExecuteCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PromptExecuteCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PromptExportCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PromptExportCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PromptImportCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PromptImportCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PromptCloneCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PromptCloneCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(WardListCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WardGetCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(WardGetCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WardResolveCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(WardResolveCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(TrialRunCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(TrialRunCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ApprenticeListCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeListCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ApprenticeGetCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeGetCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ApprenticeCreateCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeCreateCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ApprenticeDeleteCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeDeleteCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ApprenticeLifecycleCommandBase.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeStartCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticePauseCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeResumeCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeCancelCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ApprenticeReweaveCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeReweaveCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ApprenticeInterveneCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeInterveneCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ApprenticeCastCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeCastCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ApprenticeChronicleCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ApprenticeChronicleCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ModelListCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ModelListCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ProviderListCommand.Settings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ProviderListCommand))]
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
