using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Daemon;

public sealed class UninstallCommand(IDaemonManager daemonManager) : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[#C0C0C0]Removing launchd agent…[/]");

        Result result = await daemonManager.UninstallAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.Error.Message)}");

            return 1;
        }

        AnsiConsole.MarkupLine("[#87CEEB]Daemon uninstall finished.[/]");

        return 0;
    }
}
