using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    public static RootCommand Build(
        IServiceProvider serviceProvider,
        out CliGlobalOptions globalOptions)
    {
        RootCommand root = new("Arcanum CLI");
        Option<bool> json = new("--json")
        {
            Description = "Force structured JSON output for machine consumption.",
            Recursive = true,
        };
        Option<bool> plain = new("--plain")
        {
            Description = "Disable ANSI colors and terminal animations.",
            Recursive = true,
        };
        Option<bool> yes = new("--yes")
        {
            Description = "Automatically approve destructive confirmation prompts.",
            Recursive = true,
        };
        Option<bool> noContext = new("--no-context")
        {
            Description = "Bypass saved CLI context for this invocation.",
            Recursive = true,
        };
        root.Add(json);
        root.Add(plain);
        root.Add(yes);
        root.Add(noContext);
        globalOptions = new CliGlobalOptions(json, plain, yes, noContext);

        Command center = BuildCenter(serviceProvider);

        Command open = BuildOpen(serviceProvider);

        Command serve = BuildServe(serviceProvider);
        Command ask = BuildAsk(serviceProvider);
        Command run = BuildRun(serviceProvider);
        Command chat = BuildChat(serviceProvider);
        Command look = BuildLook(serviceProvider);
        Command doctor = BuildDoctor(serviceProvider);
        Command key = BuildKey(serviceProvider);
        Command lore = BuildLore(serviceProvider);
        Command daemon = BuildDaemon(serviceProvider);
        Command campaign = BuildCampaign(serviceProvider);
        Command session = BuildSession(serviceProvider);
        Command saga = BuildSaga(serviceProvider);
        Command memory = BuildMemory(serviceProvider);
        Command spell = BuildSpell(serviceProvider);
        spell.Add(BuildSpellVersion(serviceProvider));
        Command prompt = BuildPrompt(serviceProvider);
        Command ward = BuildWard(serviceProvider);
        Command trial = BuildTrial(serviceProvider);
        Command apprentice = BuildApprentice(serviceProvider);
        Command modelCmd = BuildModel(serviceProvider);
        Command provider = BuildProvider(serviceProvider);
        Command workspace = BuildWorkspace(serviceProvider);
        Command mcp = BuildMcp(serviceProvider);
        Command tool = BuildTool(serviceProvider);
        Command search = BuildSearch(serviceProvider);
        Command browse = BuildBrowse(serviceProvider);
        Command research = BuildResearch(serviceProvider);
        Command file = BuildFileCommands(serviceProvider);
        Command batch = BuildBatchCommands(serviceProvider);

        Command attachment = BuildAttachment(serviceProvider);

        Command operation = BuildOperation(serviceProvider);

        Command backup = BuildBackup(serviceProvider);

        Command data = BuildData(serviceProvider);
        Command use = BuildUse(serviceProvider);
        Command context = BuildContext(serviceProvider);
        Command mana = BuildMana(serviceProvider);
        Command config = BuildConfig(serviceProvider);

        Command preset = BuildPreset(serviceProvider);

        Command watch = BuildWatch(serviceProvider);

        root.Add(center);

        root.Add(open);

        root.Add(serve);
        root.Add(ask);
        root.Add(run);
        root.Add(chat);
        root.Add(look);
        root.Add(doctor);
        root.Add(key);
        root.Add(lore);
        root.Add(daemon);
        root.Add(campaign);
        root.Add(session);
        root.Add(saga);
        root.Add(memory);
        root.Add(spell);
        root.Add(prompt);
        root.Add(ward);
        root.Add(trial);
        root.Add(apprentice);
        root.Add(modelCmd);
        root.Add(provider);
        root.Add(workspace);
        root.Add(mcp);
        root.Add(tool);
        root.Add(search);
        root.Add(browse);
        root.Add(research);
        root.Add(file);
        root.Add(batch);

        root.Add(attachment);

        root.Add(operation);

        root.Add(backup);

        root.Add(data);
        root.Add(use);
        root.Add(context);
        root.Add(mana);
        root.Add(config);

        root.Add(preset);

        root.Add(watch);

        return root;
    }

    private static RootCommand CreateRoot() => new("Arcanum CLI");

    private static string? ActiveCampaign(
        IServiceProvider serviceProvider,
        string? explicitValue) =>
        ActiveValue(
            serviceProvider,
            explicitValue,
            static document => document.CampaignId?.ToString("D"));

    private static string? ActiveWorkspace(
        IServiceProvider serviceProvider,
        string? explicitValue)
    {

        string? value = ActiveValue(
            serviceProvider,
            explicitValue,
            static document => document.WorkspacePath);

        if (explicitValue is null
            && value is not null
            && !CliContextService.IsWithin(Environment.CurrentDirectory, value))
        {

            serviceProvider
                .GetRequiredService<IConsoleDispatcher>()
                .WriteDiagnostic(
                    $"Warning: Current directory is outside the selected workspace {value}.");

        }

        return value;

    }

    private static string? ActiveModel(
        IServiceProvider serviceProvider,
        string? explicitValue) =>
        ActiveValue(
            serviceProvider,
            explicitValue,
            static document => document.Model);

    private static string? ActiveSession(
        IServiceProvider serviceProvider,
        string? explicitValue) =>
        ActiveValue(
            serviceProvider,
            explicitValue,
            static document => document.SessionId?.ToString("D"));

    private static string? ActiveValue(
        IServiceProvider serviceProvider,
        string? explicitValue,
        Func<CliContextDocument, string?> selector)
    {

        if (!string.IsNullOrWhiteSpace(explicitValue)
            || CliInvocationContext.Current.NoContext)
        {

            return explicitValue;

        }

        CliContextDocument document = serviceProvider
            .GetRequiredService<ICliContextStore>()
            .Load();

        return selector(document);

    }
}

internal readonly record struct CliGlobalOptions(
    Option<bool> Json,
    Option<bool> Plain,
    Option<bool> Yes,
    Option<bool> NoContext);
