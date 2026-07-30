using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildServe(IServiceProvider sp)
    {
        ServeCommand handler = sp.GetRequiredService<ServeCommand>();
        Command serve = new("serve", "Hosts the Arcanum Minimal API.");
        Command quit = new("quit", "Requests the running host to shut down.");
        quit.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Quit(ct).ConfigureAwait(false));
        serve.Add(quit);
        serve.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Run(ct).ConfigureAwait(false));
        return serve;
    }
}
