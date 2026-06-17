using RetroDownfall.Arcanum.Cli.UX;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

public sealed class ApprenticeChronicleCommand(IThemePalette themePalette) : AsyncCommand
{

    protected override Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        TheForgeRouteTable.Print(
            "Apprentice Chronicle — GET /api/apprentices/{id}/chronicle",
            [
                ("GET", "/api/apprentices/{id}/chronicle", "SSE stream of Apprentice lifecycle and tool events."),
            ],
            themePalette);

        return Task.FromResult(0);

    }

}
