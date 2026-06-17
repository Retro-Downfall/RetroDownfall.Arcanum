using RetroDownfall.Arcanum.Cli.UX;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

public sealed class ApprenticeStartCommand(IThemePalette themePalette) : AsyncCommand
{

    protected override Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        TheForgeRouteTable.Print(
            "Start Apprentice — POST /api/apprentices/{id}/start",
            [
                ("POST", "/api/apprentices/{id}/start", "Start plan generation and step execution (202)."),
            ],
            themePalette);

        return Task.FromResult(0);

    }

}
