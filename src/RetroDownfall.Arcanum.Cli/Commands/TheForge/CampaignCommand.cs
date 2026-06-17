using RetroDownfall.Arcanum.Cli.UX;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

public sealed class CampaignCommand(IThemePalette themePalette) : AsyncCommand
{

    protected override Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        TheForgeRouteTable.Print(
            "Campaign registry — /api/campaigns",
            [
                ("GET", "/api/campaigns", "List campaigns (optional ?type= spell|campaign|data|custom)."),
                ("GET", "/api/campaigns/{id}", "Campaign detail."),
                ("POST", "/api/campaigns", "Register campaign path (201 + Location; creates .arcanum/)."),
                ("PUT", "/api/campaigns/{id}", "Update campaign name, path, type, or settings."),
                ("DELETE", "/api/campaigns/{id}", "Remove campaign from Grimoire (204)."),
                ("POST", "/api/campaigns/{id}/export", "Export spells + prompts + settings as portable JSON."),
                ("POST", "/api/campaigns/{id}/import", "Import from body or on-disk .arcanum/campaign.json."),
            ],
            themePalette);

        return Task.FromResult(0);

    }

}
