using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Commands.Configuration;
using RetroDownfall.Arcanum.Cli.Commands.Daemon;
using RetroDownfall.Arcanum.Cli.Commands.Llama;
using RetroDownfall.Arcanum.Cli.Commands.Lore;
using RetroDownfall.Arcanum.Cli.Commands.ProvingGrounds;
using RetroDownfall.Arcanum.Cli.Commands.TheForge;
using RetroDownfall.Arcanum.Cli.Commands.Wards;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Theme;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

[ExcludeFromCodeCoverage] // Reason: Spectre.Console.Cli wiring factory; covered via CliApplicationFactoryTests and command smoke tests.
internal static class CliApplicationFactory
{

    public static void ConfigureCliServices(IServiceCollection services, IConfiguration configuration)
    {

        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));

        services.AddArcanumThemeDetection();

        services.AddSingleton<ICliEnvironment>(sp =>
        {
            bool showManaBar = sp.GetRequiredService<IOptions<ArcanumSettings>>().Value.Cli.ShowManaBar;

            return new CliEnvironment(showManaBar);
        });

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

        // W6.4: shared secret/grimoire stack (Data Protection + digest cache + secret store +
        // CLI Grimoire), owned by Infrastructure so it cannot drift from the host wiring.
        services.AddArcanumCliClientStack();

        services.AddHttpClient(
            ArcanumApiClient.StreamingHttpClientName,
            (serviceProvider, client) =>
            {
                int port = ArcanumSettingClamps.HostPort(
                    serviceProvider.GetRequiredService<IOptions<ArcanumSettings>>().Value.Host.Port);

                client.BaseAddress = new Uri($"http://localhost:{port}/");

                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        services.AddHttpClient(
            ArcanumApiClient.RequestHttpClientName,
            (serviceProvider, client) =>
            {
                ArcanumSettings settings = serviceProvider.GetRequiredService<IOptions<ArcanumSettings>>().Value;

                int port = ArcanumSettingClamps.HostPort(settings.Host.Port);

                int timeoutSeconds = ArcanumSettingClamps.ApiRequestTimeoutSeconds(
                    settings.Cli.ApiRequestTimeoutSeconds);

                client.BaseAddress = new Uri($"http://localhost:{port}/");

                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
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

        services.AddTransient<DoctorCommand>();

        services.AddTransient<KeyShowCommand>();

        services.AddTransient<LlamaPullCommand>();

        services.AddTransient<LlamaStartCommand>();

        services.AddTransient<LlamaStopCommand>();

        services.AddTransient<LlamaStatusCommand>();

        services.AddTransient<CampaignListCommand>();

        services.AddTransient<CampaignGetCommand>();

        services.AddTransient<CampaignCreateCommand>();

        services.AddTransient<CampaignUpdateCommand>();

        services.AddTransient<CampaignDeleteCommand>();

        services.AddTransient<CampaignExportCommand>();

        services.AddTransient<CampaignImportCommand>();

        services.AddTransient<CampaignCodexGetCommand>();

        services.AddTransient<CampaignCodexPutCommand>();

        services.AddTransient<CampaignCodexDeleteCommand>();

        services.AddTransient<CampaignSpellsCommand>();

        services.AddTransient<CampaignPromptsCommand>();

        services.AddTransient<CampaignSessionsCommand>();

        services.AddTransient<SessionDivinationCommand>();

        services.AddTransient<SagaListCommand>();

        services.AddTransient<SagaDivineCommand>();

        services.AddTransient<SagaDeleteCommand>();

        services.AddTransient<SagaStatsCommand>();

        services.AddTransient<SpellListCommand>();

        services.AddTransient<SpellGetCommand>();

        services.AddTransient<SpellCreateCommand>();

        services.AddTransient<SpellUpdateCommand>();

        services.AddTransient<SpellDeleteCommand>();

        services.AddTransient<SpellSearchCommand>();

        services.AddTransient<SpellValidateCommand>();

        services.AddTransient<SpellExecuteCommand>();

        services.AddTransient<SpellVersionsCommand>();

        services.AddTransient<SpellExportCommand>();

        services.AddTransient<SpellImportCommand>();

        services.AddTransient<SpellCastCommand>();

        services.AddTransient<SpellCloneCommand>();

        services.AddTransient<SpellVersionCreateCommand>();

        services.AddTransient<SpellVersionUpdateCommand>();

        services.AddTransient<SpellVersionActivateCommand>();

        services.AddTransient<PromptListCommand>();

        services.AddTransient<PromptGetCommand>();

        services.AddTransient<PromptVersionsCommand>();

        services.AddTransient<PromptCreateCommand>();

        services.AddTransient<PromptUpdateCommand>();

        services.AddTransient<PromptDeleteCommand>();

        services.AddTransient<PromptRenderCommand>();

        services.AddTransient<PromptTestCommand>();

        services.AddTransient<PromptExecuteCommand>();

        services.AddTransient<PromptExportCommand>();

        services.AddTransient<PromptImportCommand>();

        services.AddTransient<PromptCloneCommand>();

        services.AddTransient<WardListCommand>();

        services.AddTransient<WardGetCommand>();

        services.AddTransient<WardResolveCommand>();

        services.AddTransient<TrialRunCommand>();

        services.AddTransient<ApprenticeListCommand>();

        services.AddTransient<ApprenticeGetCommand>();

        services.AddTransient<ApprenticeCreateCommand>();

        services.AddTransient<ApprenticeDeleteCommand>();

        services.AddTransient<ApprenticeStartCommand>();

        services.AddTransient<ApprenticePauseCommand>();

        services.AddTransient<ApprenticeResumeCommand>();

        services.AddTransient<ApprenticeCancelCommand>();

        services.AddTransient<ApprenticeReweaveCommand>();

        services.AddTransient<ApprenticeInterveneCommand>();

        services.AddTransient<ApprenticeCastCommand>();

        services.AddTransient<ApprenticeChronicleCommand>();

        services.AddTransient<ModelListCommand>();

        services.AddTransient<ProviderListCommand>();

    }

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Spectre.Console.Cli is reflection-based; TrimmerRootAssembly + DynamicDependency attributes on Program.Main preserve the required command types.")]
    public static CommandApp BuildCommandApp(IServiceCollection services)
    {

        CliTypeRegistrar registrar = new(services);

        CommandApp app = new(registrar);

        app.Configure(ConfigureCommands);

        return app;

    }

    public static void ConfigureCommands(IConfigurator config)
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

        config.AddCommand<DoctorCommand>("doctor")
            .WithDescription("Run environment diagnostics (version, paths, API health).");

        config.AddBranch("key", key =>
        {
            key.SetDescription("Master API key utilities (local secret store only; no HTTP).");

            key.AddCommand<KeyShowCommand>("show")
                .WithDescription("Print the stored master API key to stderr (so stdout piping does not capture the secret).");
        });

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

        config.AddBranch("llama", llama =>
        {
            llama.SetDescription("Manage local llama-server instances and GGUF model cache (requires arcanum serve).");

            llama.AddCommand<LlamaPullCommand>("pull")
                .WithDescription("Download a GGUF model into the local cache.");

            llama.AddCommand<LlamaStartCommand>("start")
                .WithDescription("Start llama-server for a cached model.");

            llama.AddCommand<LlamaStopCommand>("stop")
                .WithDescription("Stop one or all llama-server instances.");

            llama.AddCommand<LlamaStatusCommand>("status")
                .WithDescription("List running servers and cached models.");
        });

        config.AddBranch("campaign", campaign =>
        {
            campaign.SetDescription("The Forge campaign registry (requires arcanum serve).");

            campaign.AddCommand<CampaignListCommand>("list")
                .WithDescription("List registered campaigns (GET /api/campaigns).");

            campaign.AddCommand<CampaignGetCommand>("get")
                .WithDescription("Show campaign detail (GET /api/campaigns/{id}).");

            campaign.AddCommand<CampaignCreateCommand>("create")
                .WithDescription("Register a new campaign (POST /api/campaigns).");

            campaign.AddCommand<CampaignUpdateCommand>("update")
                .WithDescription("Update a campaign (PUT /api/campaigns/{id}).");

            campaign.AddCommand<CampaignDeleteCommand>("delete")
                .WithDescription("Remove a campaign (DELETE /api/campaigns/{id}).");

            campaign.AddCommand<CampaignExportCommand>("export")
                .WithDescription("Export a campaign's spells and prompts as JSON (POST /api/campaigns/{id}/export).");

            campaign.AddCommand<CampaignImportCommand>("import")
                .WithDescription("Import spells and prompts into a campaign (POST /api/campaigns/{id}/import).");

            campaign.AddBranch("codex", codex =>
            {
                codex.SetDescription("Manage the campaign's CODEX.md scratchpad.");

                codex.AddCommand<CampaignCodexGetCommand>("get")
                    .WithDescription("Print CODEX.md (GET /api/campaigns/{id}/codex).");

                codex.AddCommand<CampaignCodexPutCommand>("put")
                    .WithDescription("Write CODEX.md from a file (PUT /api/campaigns/{id}/codex).");

                codex.AddCommand<CampaignCodexDeleteCommand>("delete")
                    .WithDescription("Delete CODEX.md (DELETE /api/campaigns/{id}/codex).");
            });

            campaign.AddCommand<CampaignSpellsCommand>("spells")
                .WithDescription("List spells scoped to a campaign, shadowing built-ins (GET /api/campaigns/{id}/spells).");

            campaign.AddCommand<CampaignPromptsCommand>("prompts")
                .WithDescription("List prompts scoped to a campaign (GET /api/campaigns/{id}/prompts).");

            campaign.AddCommand<CampaignSessionsCommand>("sessions")
                .WithDescription("List sessions scoped to a campaign (GET /api/campaigns/{id}/sessions).");
        });

        config.AddBranch("session", session =>
        {
            session.SetDescription("Session semantic search (requires arcanum serve).");

            session.AddCommand<SessionDivinationCommand>("divine")
                .WithDescription(
                    "Semantic search over Grimoire entries (POST /api/sessions/divine; requires Arcanum:Embeddings:Enabled and SessionSearchEnabled).");
        });

        config.AddBranch("saga", saga =>
        {
            saga.SetDescription("Saga long-term associative memory (requires arcanum serve).");

            saga.AddCommand<SagaListCommand>("list")
                .WithDescription("Paginated listing of Saga memories (GET /api/saga).");

            saga.AddCommand<SagaDivineCommand>("divine")
                .WithDescription(
                    "Semantic search over Saga memories (POST /api/saga/divine; requires Arcanum:Embeddings:Enabled and SagaEnabled).");

            saga.AddCommand<SagaDeleteCommand>("delete")
                .WithDescription("Delete a single Saga memory (DELETE /api/saga/{id}).");

            saga.AddCommand<SagaStatsCommand>("stats")
                .WithDescription("Aggregate summary of Saga memory storage (GET /api/saga/stats).");
        });

        config.AddBranch("spell", spell =>
        {
            spell.SetDescription("The Forge spell utilities (requires arcanum serve).");

            spell.AddCommand<SpellListCommand>("list")
                .WithDescription("List spells (GET /api/spells).");

            spell.AddCommand<SpellGetCommand>("get")
                .WithDescription("Show spell detail (GET /api/spells/{name}).");

            spell.AddCommand<SpellCreateCommand>("create")
                .WithDescription("Create a spell (POST /api/spells).");

            spell.AddCommand<SpellUpdateCommand>("update")
                .WithDescription("Update a spell (PUT /api/spells/{name}).");

            spell.AddCommand<SpellDeleteCommand>("delete")
                .WithDescription("Delete a spell (DELETE /api/spells/{name}).");

            spell.AddCommand<SpellSearchCommand>("search")
                .WithDescription("Search spells by query/tag/tool/source (GET /api/spells/search).");

            spell.AddCommand<SpellValidateCommand>("validate")
                .WithDescription("Validate a spell's frontmatter and dependencies (POST /api/spells/{name}/validate).");

            spell.AddCommand<SpellExecuteCommand>("execute")
                .WithDescription("Execute a spell and print the assistant response (POST /api/spells/{name}/execute).");

            spell.AddCommand<SpellVersionsCommand>("versions")
                .WithDescription("List spell versions (GET /api/spells/{name}/versions).");

            spell.AddCommand<SpellExportCommand>("export")
                .WithDescription("Export a spell as portable JSON (POST /api/spells/{name}/export).");

            spell.AddCommand<SpellImportCommand>("import")
                .WithDescription("Import a spell from portable JSON (POST /api/spells/import).");

            spell.AddCommand<SpellCastCommand>("cast")
                .WithDescription("Dry-run preview of a spell's assembled system prompt \u2014 no inference tokens consumed (POST /api/spells/{name}/cast).");

            spell.AddCommand<SpellCloneCommand>("clone")
                .WithDescription("Clone a spell to a new name (POST /api/spells/{name}/clone).");

            spell.AddBranch("version", version =>
            {
                version.SetDescription("Manage named spell file versions (SPELL.v{label}.md sidecar files).");

                version.AddCommand<SpellVersionCreateCommand>("create")
                    .WithDescription("Create a new spell version (POST /api/spells/{name}/versions).");

                version.AddCommand<SpellVersionUpdateCommand>("update")
                    .WithDescription("Update an existing spell version's body (PUT /api/spells/{name}/versions/{version}).");

                version.AddCommand<SpellVersionActivateCommand>("activate")
                    .WithDescription("Activate a spell version, swapping it into SPELL.md (POST /api/spells/{name}/versions/{version}/activate).");
            });
        });

        config.AddBranch("prompt", prompt =>
        {
            prompt.SetDescription("The Forge prompt utilities (requires arcanum serve).");

            prompt.AddCommand<PromptListCommand>("list")
                .WithDescription("List prompts (GET /api/prompts).");

            prompt.AddCommand<PromptGetCommand>("get")
                .WithDescription("Show prompt detail (GET /api/prompts/{id}).");

            prompt.AddCommand<PromptVersionsCommand>("versions")
                .WithDescription("List versions of a prompt by name (GET /api/prompts/by-name/{name}/versions).");

            prompt.AddCommand<PromptCreateCommand>("create")
                .WithDescription("Create a prompt (POST /api/prompts).");

            prompt.AddCommand<PromptUpdateCommand>("update")
                .WithDescription("Update a prompt (PUT /api/prompts/{id}).");

            prompt.AddCommand<PromptDeleteCommand>("delete")
                .WithDescription("Delete a prompt (DELETE /api/prompts/{id}).");

            prompt.AddCommand<PromptRenderCommand>("render")
                .WithDescription("Render a prompt template with parameters (POST /api/prompts/{id}/render).");

            prompt.AddCommand<PromptTestCommand>("test")
                .WithDescription("Assemble the system prompt without LLM cost (POST /api/prompts/{id}/test).");

            prompt.AddCommand<PromptExecuteCommand>("execute")
                .WithDescription("Render and run session-backed inference (POST /api/prompts/{id}/execute).");

            prompt.AddCommand<PromptExportCommand>("export")
                .WithDescription("Export a prompt as portable JSON (POST /api/prompts/{id}/export).");

            prompt.AddCommand<PromptImportCommand>("import")
                .WithDescription("Import a prompt from portable JSON (POST /api/prompts/import).");

            prompt.AddCommand<PromptCloneCommand>("clone")
                .WithDescription("Clone a prompt to a new name/version (POST /api/prompts/{id}/clone).");
        });

        config.AddBranch("ward", ward =>
        {
            ward.SetDescription("Ward approval gates for Forbidden Arts (requires arcanum serve).");

            ward.AddCommand<WardListCommand>("list")
                .WithDescription("List active wards (GET /api/wards).");

            ward.AddCommand<WardGetCommand>("get")
                .WithDescription("Show ward detail (GET /api/wards/{id}).");

            ward.AddCommand<WardResolveCommand>("resolve")
                .WithDescription("Allow or deny a ward (POST /api/wards/{id}).");
        });

        config.AddBranch("trial", trial =>
        {
            trial.SetDescription("The Proving Grounds: run Trials against spells, prompts, or Apprentice goals (requires arcanum serve).");

            trial.AddCommand<TrialRunCommand>("run")
                .WithDescription("Run a Trial with Inquisitors (POST /api/proving-grounds/trials/run).");
        });

        config.AddBranch("apprentice", apprentice =>
        {
            apprentice.SetDescription("The Forge Apprentice orchestration (requires arcanum serve).");

            apprentice.AddCommand<ApprenticeListCommand>("list")
                .WithDescription("List Apprentices (GET /api/apprentices).");

            apprentice.AddCommand<ApprenticeGetCommand>("get")
                .WithDescription("Show Apprentice detail (GET /api/apprentices/{id}).");

            apprentice.AddCommand<ApprenticeCreateCommand>("create")
                .WithDescription("Create an Apprentice (POST /api/apprentices).");

            apprentice.AddCommand<ApprenticeDeleteCommand>("delete")
                .WithDescription("Delete a terminal Apprentice (DELETE /api/apprentices/{id}).");

            apprentice.AddCommand<ApprenticeStartCommand>("start")
                .WithDescription("Start plan generation and execution (POST /api/apprentices/{id}/start).");

            apprentice.AddCommand<ApprenticePauseCommand>("pause")
                .WithDescription("Pause at the next step boundary (POST /api/apprentices/{id}/pause).");

            apprentice.AddCommand<ApprenticeResumeCommand>("resume")
                .WithDescription("Resume from checkpoint (POST /api/apprentices/{id}/resume).");

            apprentice.AddCommand<ApprenticeCancelCommand>("cancel")
                .WithDescription("Cancel execution (POST /api/apprentices/{id}/cancel).");

            apprentice.AddCommand<ApprenticeReweaveCommand>("reweave")
                .WithDescription("Replace the remaining plan steps (POST /api/apprentices/{id}/reweave).");

            apprentice.AddCommand<ApprenticeInterveneCommand>("intervene")
                .WithDescription("Provide Divine Intervention guidance to an escalated Apprentice (POST /api/apprentices/{id}/intervene).");

            apprentice.AddCommand<ApprenticeCastCommand>("cast")
                .WithDescription("Delegate a child Apprentice via The Conclave (POST /api/apprentices/{id}/cast).");

            apprentice.AddCommand<ApprenticeChronicleCommand>("chronicle")
                .WithDescription("Stream live Apprentice events (GET /api/apprentices/{id}/chronicle, SSE).");
        });

        config.AddBranch("model", model =>
        {
            model.SetDescription("Native model listing across configured providers (requires arcanum serve).");

            model.AddCommand<ModelListCommand>("list")
                .WithDescription("List configured models across all providers (GET /api/models).");
        });

        config.AddBranch("provider", provider =>
        {
            provider.SetDescription("Native provider listing and configuration summary (requires arcanum serve).");

            provider.AddCommand<ProviderListCommand>("list")
                .WithDescription("List configured providers with redacted secrets (GET /api/providers).");
        });

    }

    public static void ConfigureAnsiConsoleForEnvironment(IConfiguration configuration)
    {

        bool noColor = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARCANUM_NO_COLOR"));

        bool stdoutRedirected = Console.IsOutputRedirected;

        if (!noColor && !stdoutRedirected)
        {
            // Leave Spectre's auto-detected capabilities alone for normal interactive use.
            return;
        }

        AnsiSupport ansi = noColor || stdoutRedirected ? AnsiSupport.No : AnsiSupport.Detect;

        ColorSystemSupport colorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect;

        InteractionSupport interaction = stdoutRedirected ? InteractionSupport.No : InteractionSupport.Detect;

        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = ansi,
            ColorSystem = colorSystem,
            Interactive = interaction,
            Out = new AnsiConsoleOutput(Console.Out),
        });

        _ = configuration;
    }

}
