using System.CommandLine;
using System.CommandLine.Parsing;

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
        root.Add(json);
        root.Add(plain);
        root.Add(yes);
        globalOptions = new CliGlobalOptions(json, plain, yes);
        Command serve = BuildServe(serviceProvider);
        Command ask = BuildAsk(serviceProvider);
        Command chat = BuildChat(serviceProvider);
        Command look = BuildLook(serviceProvider);
        Command doctor = BuildDoctor(serviceProvider);
        Command key = BuildKey(serviceProvider);
        Command lore = BuildLore(serviceProvider);
        Command daemon = BuildDaemon(serviceProvider);
        Command campaign = BuildCampaign(serviceProvider);
        Command session = BuildSession(serviceProvider);
        Command saga = BuildSaga(serviceProvider);
        Command spell = BuildSpell(serviceProvider);
        spell.Add(BuildSpellVersion(serviceProvider));
        Command prompt = BuildPrompt(serviceProvider);
        Command ward = BuildWard(serviceProvider);
        Command trial = BuildTrial(serviceProvider);
        Command apprentice = BuildApprentice(serviceProvider);
        Command modelCmd = BuildModel(serviceProvider);
        Command provider = BuildProvider(serviceProvider);
        Command operation = BuildOperation(serviceProvider);
        Command data = BuildData(serviceProvider);

        root.Add(serve);
        root.Add(ask);
        root.Add(chat);
        root.Add(look);
        root.Add(doctor);
        root.Add(key);
        root.Add(lore);
        root.Add(daemon);
        root.Add(campaign);
        root.Add(session);
        root.Add(saga);
        root.Add(spell);
        root.Add(prompt);
        root.Add(ward);
        root.Add(trial);
        root.Add(apprentice);
        root.Add(modelCmd);
        root.Add(provider);
        root.Add(operation);
        root.Add(data);

        return root;
    }

    private static RootCommand CreateRoot() => new("Arcanum CLI");
}

internal readonly record struct CliGlobalOptions(
    Option<bool> Json,
    Option<bool> Plain,
    Option<bool> Yes);
