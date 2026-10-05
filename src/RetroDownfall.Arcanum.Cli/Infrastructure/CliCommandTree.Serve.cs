using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildServe(IServiceProvider sp)
    {
        DeferredHandler<ServeCommand> handler = new(sp);
        Command serve = new("serve", "Hosts the Arcanum Minimal API.");
        Command quit = new("quit", "Requests the running host to shut down.");
        quit.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Quit(ct).ConfigureAwait(false));
        serve.Add(quit);
        serve.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Run(ct).ConfigureAwait(false));
        return serve;
    }
}
