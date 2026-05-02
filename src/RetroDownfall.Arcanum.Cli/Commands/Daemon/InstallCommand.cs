using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Daemon;

public sealed class InstallCommand(IDaemonManager daemonManager) : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[#C0C0C0]Installing launchd agent…[/]");

        Result result = await daemonManager.InstallAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.Error.Message)}");

            return 1;
        }

        AnsiConsole.MarkupLine("[#87CEEB]Daemon installed and bootstrapped.[/]");

        return 0;
    }
}
