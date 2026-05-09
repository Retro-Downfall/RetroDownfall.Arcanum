using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Daemon;

public sealed class StatusCommand(IDaemonManager daemonManager, IThemePalette themePalette) : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Checking launchd status…")));

        Result<string> result = await daemonManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(
                themePalette.ErrorLabelMarkup(Markup.Escape("Error:"), Markup.Escape(result.Error.Message)));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.HighlightMarkup(Markup.Escape(result.Value)));

        return 0;
    }
}
