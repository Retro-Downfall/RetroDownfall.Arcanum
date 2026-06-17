using RetroDownfall.Arcanum.Cli.UX;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

public sealed class SpellSearchCommand(IThemePalette themePalette) : AsyncCommand
{

    protected override Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        TheForgeRouteTable.Print(
            "Spell search — /api/spells/search",
            [
                ("GET", "/api/spells/search", "Multi-source spell search across builtin, workspace, and campaigns."),
                ("", "", "Query: ?q= ?tag= ?tool= ?source=builtin|workspace|campaign ?campaignId= ?workspace="),
                ("POST", "/api/spells/{name}/validate", "Validate spell metadata and declared tools (warnings only)."),
                ("POST", "/api/spells/{name}/export", "Export SKILL.json + SPELL.md + scripts (base64)."),
                ("POST", "/api/spells/import", "Import portable spell bundle into a workspace."),
            ],
            themePalette);

        return Task.FromResult(0);

    }

}
