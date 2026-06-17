using RetroDownfall.Arcanum.Cli.UX;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

public sealed class PromptRenderCommand(IThemePalette themePalette) : AsyncCommand
{

    protected override Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        TheForgeRouteTable.Print(
            "Prompt render — /api/prompts/{id}/render",
            [
                ("POST", "/api/prompts/{id}/render", "Render template with { \"parameters\": { ... } } body."),
                ("POST", "/api/prompts/{id}/test", "Assemble system prompt via SystemPromptBuilder (no LLM)."),
                ("GET", "/api/prompts", "List/search prompts (?campaignId= ?q= ?tag=)."),
                ("GET", "/api/prompts/{id}", "Prompt detail."),
                ("GET", "/api/prompts/by-name/{name}/versions", "List versions (?campaignId= for scoped)."),
                ("POST", "/api/prompts", "Create prompt version (201)."),
                ("PUT", "/api/prompts/{id}", "Update prompt."),
                ("DELETE", "/api/prompts/{id}", "Delete prompt (204)."),
                ("POST", "/api/prompts/{id}/export", "Portable JSON export."),
                ("POST", "/api/prompts/import", "Import into campaign or global scope."),
            ],
            themePalette);

        return Task.FromResult(0);

    }

}
