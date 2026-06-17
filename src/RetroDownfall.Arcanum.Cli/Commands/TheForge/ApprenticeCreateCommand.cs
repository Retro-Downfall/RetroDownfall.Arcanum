using RetroDownfall.Arcanum.Cli.UX;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

public sealed class ApprenticeCreateCommand(IThemePalette themePalette) : AsyncCommand
{

    protected override Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        TheForgeRouteTable.Print(
            "Create Apprentice — POST /api/apprentices",
            [
                ("POST", "/api/apprentices", "Body: name, goal, optional campaignId, optional workspacePath."),
            ],
            themePalette);

        return Task.FromResult(0);

    }

}
