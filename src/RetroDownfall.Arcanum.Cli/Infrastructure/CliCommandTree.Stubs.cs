using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildChat(IServiceProvider sp) => new Command("chat", "Interactive chat session.");
    private static Command BuildLook(IServiceProvider sp) => new Command("look", "Eye of the World directory snapshot.");
    private static Command BuildDoctor(IServiceProvider sp) => new Command("doctor", "Diagnostics.");
    private static Command BuildKey(IServiceProvider sp) => new Command("key", "Key management.");
    private static Command BuildLore(IServiceProvider sp) => new Command("lore", "Lore management.");
    private static Command BuildDaemon(IServiceProvider sp) => new Command("daemon", "Daemon management.");
    private static Command BuildCampaign(IServiceProvider sp)
    {
        Command cmd = new("campaign", "Campaign commands.");
        cmd.Add(new Command("codex", "Campaign codex commands.") { });
        return cmd;
    }
    private static Command BuildSession(IServiceProvider sp) => new Command("session", "Session commands.");
    private static Command BuildSaga(IServiceProvider sp) => new Command("saga", "Saga commands.");
    private static Command BuildSpell(IServiceProvider sp) => new Command("spell", "Spell commands.");
    private static Command BuildSpellVersion(IServiceProvider sp) => new Command("version", "Spell version commands.");
    private static Command BuildPrompt(IServiceProvider sp) => new Command("prompt", "Prompt commands.");
    private static Command BuildWard(IServiceProvider sp) => new Command("ward", "Ward commands.");
    private static Command BuildTrial(IServiceProvider sp) => new Command("trial", "Trial commands.");
    private static Command BuildApprentice(IServiceProvider sp) => new Command("apprentice", "Apprentice commands.");
    private static Command BuildModel(IServiceProvider sp) => new Command("model", "Model commands.");
    private static Command BuildProvider(IServiceProvider sp) => new Command("provider", "Provider commands.");
}
