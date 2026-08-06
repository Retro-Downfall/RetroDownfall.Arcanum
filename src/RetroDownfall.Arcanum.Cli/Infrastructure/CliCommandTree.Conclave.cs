using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildConclave(IServiceProvider serviceProvider)
    {

        ConclaveCommands handler = serviceProvider.GetRequiredService<ConclaveCommands>();

        Command conclave = new("conclave", "The Conclave and its A2A surface (requires arcanum serve).");

        Command status = new("status", "Show whether A2A is disabled, configured, degraded, or healthy.");

        status.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await handler.Status(cancellationToken).ConfigureAwait(false));

        conclave.Add(status);

        Command dispatch = new("dispatch", "Dispatch a Sending to a remote A2A agent and wait for its result.");

        Option<string?> agentUrl = new("--agent-url", "--agentUrl")
        {
            Description = "Remote agent base URL or Agent Card URL.",
        };

        Option<string?> goal = new("--goal") { Description = "Goal text delegated to the remote agent." };

        Option<string?> name = new("--name") { Description = "Optional display name for the Sending." };

        dispatch.Add(agentUrl);
        dispatch.Add(goal);
        dispatch.Add(name);

        dispatch.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await handler.Dispatch(
                result.GetValue(agentUrl),
                result.GetValue(goal),
                result.GetValue(name),
                cancellationToken).ConfigureAwait(false));

        conclave.Add(dispatch);

        return conclave;

    }

}
