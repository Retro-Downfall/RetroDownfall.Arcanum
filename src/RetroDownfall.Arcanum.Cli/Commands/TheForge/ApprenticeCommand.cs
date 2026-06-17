using RetroDownfall.Arcanum.Cli.UX;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

public sealed class ApprenticeCommand(IThemePalette themePalette) : AsyncCommand
{

    internal static IReadOnlyList<(string Verb, string Path, string Purpose)> AllRoutes =>
    [
        ("GET", "/api/apprentices", "List apprentices (optional ?campaignId=, ?status=)."),
        ("GET", "/api/apprentices/{id}", "Apprentice detail."),
        ("POST", "/api/apprentices", "Create apprentice (201 + Location)."),
        ("DELETE", "/api/apprentices/{id}", "Delete terminal apprentice (204)."),
        ("POST", "/api/apprentices/{id}/start", "Start plan generation and execution (202)."),
        ("POST", "/api/apprentices/{id}/pause", "Pause at step boundary (202)."),
        ("POST", "/api/apprentices/{id}/resume", "Resume from checkpoint (202)."),
        ("POST", "/api/apprentices/{id}/cancel", "Cancel execution (202)."),
        ("GET", "/api/apprentices/{id}/chronicle", "Chronicle SSE stream of Apprentice progress."),
    ];

    protected override Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        TheForgeRouteTable.Print(
            "Apprentice orchestration — /api/apprentices",
            AllRoutes,
            themePalette);

        return Task.FromResult(0);

    }

}
