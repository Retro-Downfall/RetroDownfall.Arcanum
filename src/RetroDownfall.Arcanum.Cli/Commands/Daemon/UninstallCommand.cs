using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Daemon;

public sealed class UninstallCommand(IDaemonManager daemonManager, IThemePalette themePalette) : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Removing launchd agent…")));

        Result result = await daemonManager.UninstallAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorLabelMarkup(Markup.Escape("Error:"), result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.HighlightMarkup(Markup.Escape("Daemon uninstall finished.")));

        return 0;
    }
}
