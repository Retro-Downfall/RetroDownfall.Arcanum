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

        Option<string?> agentUrl = new("--agent-url")
        {
            Description = "Remote agent base URL or Agent Card URL.",
        };

        Option<string?> goal = new("--goal") { Description = "Goal text delegated to the remote agent." };

        Option<string?> name = new("--name") { Description = "Optional display name for the Sending." };

        Option<bool> continuable = new("--continuable")
        {
            Description =
                "Return a continuation task id when the remote agent asks for more input or authentication, "
                + "instead of ending the Sending. Answer it with 'arcanum conclave continue'.",
        };

        Option<string?> skill = new("--skill")
        {
            Description =
                "Agent Card skill id to target. The Sending fails before the remote task is created if "
                + "the peer advertises no such skill.",
        };

        Option<string[]?> accept = new("--accept")
        {
            Description =
                "Media type to accept back (repeatable, e.g. --accept text/plain). Omit to accept "
                + "whatever this instance can consume.",
            AllowMultipleArgumentsPerToken = true,
        };

        Option<bool> callback = new("--callback")
        {
            Description =
                "Ask the remote agent to report back when it finishes instead of holding one of this "
                + "instance's concurrent-Sending slots for the whole remote run. Requires "
                + "Arcanum:Integrations:A2A:PushNotifications and a reachable PushCallbackBaseUrl; "
                + "falls back to the ordinary wait when the peer cannot accept a callback.",
        };

        dispatch.Add(agentUrl);
        dispatch.Add(goal);
        dispatch.Add(name);
        dispatch.Add(continuable);
        dispatch.Add(skill);
        dispatch.Add(accept);
        dispatch.Add(callback);

        dispatch.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await handler.Dispatch(
                result.GetValue(agentUrl),
                result.GetValue(goal),
                result.GetValue(name),
                result.GetValue(continuable),
                result.GetValue(skill),
                result.GetValue(accept),
                result.GetValue(callback),
                cancellationToken).ConfigureAwait(false));

        conclave.Add(dispatch);

        Command continueSending = new(
            "continue",
            "Answer a Sending the remote agent parked at input-required or auth-required.");

        Argument<string?> taskId = new("task-id")
        {
            Description = "Remote A2A task id reported by 'arcanum conclave dispatch --continuable'.",
        };

        Option<string?> continueAgentUrl = new("--agent-url")
        {
            Description = "Remote agent base URL or Agent Card URL — the same one the Sending was dispatched to.",
        };

        Option<string?> message = new("--message")
        {
            Description = "The input or credential the remote agent asked for.",
        };

        Option<bool> stayContinuable = new("--continuable")
        {
            Description = "Keep returning a continuation if the remote asks for something again.",
        };

        Option<string?> continueSkill = new("--skill")
        {
            Description = "Agent Card skill id to target, validated against the peer's card before sending.",
        };

        Option<string[]?> continueAccept = new("--accept")
        {
            Description =
                "Media type to accept back (repeatable). Omit to accept whatever this instance can consume.",
            AllowMultipleArgumentsPerToken = true,
        };

        continueSending.Add(taskId);
        continueSending.Add(continueAgentUrl);
        continueSending.Add(message);
        continueSending.Add(stayContinuable);
        continueSending.Add(continueSkill);
        continueSending.Add(continueAccept);

        continueSending.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await handler.Continue(
                result.GetValue(taskId),
                result.GetValue(continueAgentUrl),
                result.GetValue(message),
                result.GetValue(stayContinuable),
                result.GetValue(continueSkill),
                result.GetValue(continueAccept),
                cancellationToken).ConfigureAwait(false));

        conclave.Add(continueSending);

        return conclave;

    }

}
