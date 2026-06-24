using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Daemon;

public sealed class InstallCommand(IDaemonManager daemonManager, IThemePalette themePalette) : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Installing launchd agent…")));

        Result result = await daemonManager.InstallAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorLabelMarkup(Markup.Escape("Error:"), result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.HighlightMarkup(Markup.Escape("Daemon installed and bootstrapped.")));

        return 0;
    }
}
