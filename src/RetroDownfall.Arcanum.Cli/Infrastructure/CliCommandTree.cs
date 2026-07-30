using System.CommandLine;
using System.CommandLine.Parsing;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    public static RootCommand Build(IServiceProvider serviceProvider)
    {
        RootCommand root = new("Arcanum CLI");
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

        return root;
    }

    private static RootCommand CreateRoot() => new("Arcanum CLI");
}
