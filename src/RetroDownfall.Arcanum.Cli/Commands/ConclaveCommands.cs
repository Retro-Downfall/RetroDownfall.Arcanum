using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Operator-facing Conclave / A2A commands (requires <c>arcanum serve</c>).
/// </summary>
/// <remarks>
/// Issue #12 requires the Mage to discover capability, dispatch work, observe progress, cancel, and
/// receive a terminal result without raw HTTP calls. <c>status</c> covers discovery and diagnosis;
/// <c>dispatch</c> covers outbound work and returns the remote's terminal result. Progress and
/// cancellation for <em>inbound</em> Sendings are the existing Apprentice surfaces — an inbound Sending
/// <em>is</em> an Apprentice, so <c>arcanum watch apprentice</c> and <c>arcanum apprentice cancel</c>
/// already apply.
/// </remarks>
public sealed class ConclaveCommands(
    ArcanumApiClient apiClient,
    IConsoleDispatcher console,
    ICliInvocationContext invocationContext)
{

    public async Task<int> Status(CancellationToken cancellationToken)
    {

        Result<ConclaveStatusDto> result = await apiClient
            .GetConclaveStatusAsync(cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            console.WriteDiagnostic(result.Error.Message);

            // Exit 3 means "the host was unreachable". A missing key or a server-side domain error
            // is not a network fault and must not tell automation to retry the connection.
            return result.Error.Code.StartsWith("Connection.", StringComparison.Ordinal)
                ? (int)CliExitCode.NetworkError
                : (int)CliExitCode.GenericError;

        }

        ConclaveStatusDto status = result.Value;

        if (invocationContext.Options.Json)
        {

            console.WriteJson(status, ArcanumJsonContext.Default.ConclaveStatusDto);

            return (int)CliExitCode.Success;

        }

        console.WritePayload($"Conclave: {status.State}");

        console.WritePayload($"  Cross-Apprentice delegation (cast_sending): {Describe(status.ConclaveEnabled)}");

        console.WritePayload($"  Inbound A2A server:  {Describe(status.A2AServerEnabled)}");

        console.WritePayload($"  Outbound A2A client: {Describe(status.A2AClientEnabled)}");

        console.WritePayload($"  Server path:     {status.ServerPath ?? "(not mapped)"}");

        console.WritePayload($"  Agent Card path: {status.AgentCardPath ?? "(not mapped)"}");

        console.WritePayload($"  Allowed remote agents: {status.AllowedRemoteAgentCount}");

        console.WritePayload($"  {status.Detail}");

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Dispatches a Sending to a remote A2A agent and blocks until it reaches a terminal state.
    /// Cancelling locally also cancels the remote task.
    /// </summary>
    /// <param name="continuable">
    /// When set, a remote that asks for more input or authentication returns a continuation task id
    /// instead of ending the Sending, so <c>arcanum conclave continue</c> can answer it (issue #64).
    /// </param>
    /// <param name="skillId">
    /// Optional Agent Card skill id to target; the dispatch fails before the remote task is created if
    /// the peer advertises no such skill (issue #65).
    /// </param>
    /// <param name="acceptedOutputModes">
    /// Optional media types to accept back. Empty means "whatever this instance can consume".
    /// </param>
    public async Task<int> Dispatch(
        string? agentUrl,
        string? goal,
        string? name,
        bool continuable,
        string? skillId,
        string[]? acceptedOutputModes,
        bool callback,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(agentUrl) || string.IsNullOrWhiteSpace(goal))
        {

            console.WriteDiagnostic("Both --agent-url and --goal are required.");

            return (int)CliExitCode.ConfigurationError;

        }

        Result<SendingDispatchDto> result = await apiClient
            .DispatchSendingAsync(
                agentUrl.Trim(),
                goal.Trim(),
                name,
                continuable,
                skillId,
                acceptedOutputModes,
                callback,
                cancellationToken)
            .ConfigureAwait(false);

        return Render(result, agentUrl.Trim());

    }

    /// <summary>
    /// Answers a Sending the remote parked at <c>input-required</c>/<c>auth-required</c>, resuming the
    /// same remote task instead of re-running the whole goal.
    /// </summary>
    public async Task<int> Continue(
        string? taskId,
        string? agentUrl,
        string? message,
        bool continuable,
        string? skillId,
        string[]? acceptedOutputModes,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(agentUrl) || string.IsNullOrWhiteSpace(message))
        {

            console.WriteDiagnostic("A task id plus --agent-url and --message are all required.");

            return (int)CliExitCode.ConfigurationError;

        }

        Result<SendingDispatchDto> result = await apiClient
            .ContinueSendingAsync(
                agentUrl.Trim(),
                taskId.Trim(),
                message.Trim(),
                continuable,
                skillId,
                acceptedOutputModes,
                cancellationToken)
            .ConfigureAwait(false);

        return Render(result, agentUrl.Trim());

    }

    private int Render(Result<SendingDispatchDto> result, string agentUrl)
    {

        if (result.IsFailure)
        {

            // Error messages from the Archmage Client already name the owner (feature gate, allowlist,
            // outbound URL policy, remote failure) and the next action.
            console.WriteDiagnostic($"{result.Error.Code}: {result.Error.Message}");

            return (int)CliExitCode.GenericError;

        }

        SendingDispatchDto dispatch = result.Value;

        if (invocationContext.Options.Json)
        {

            console.WriteJson(dispatch, ArcanumJsonContext.Default.SendingDispatchDto);

            return (int)CliExitCode.Success;

        }

        console.WritePayload($"Sending dispatched to {dispatch.AgentUrl}");

        console.WritePayload($"  Remote task: {dispatch.TaskId ?? "(stateless reply — no task created)"}");

        console.WritePayload($"  Remote cost: {DescribeCost(dispatch)}");

        if (DescribeDuration(dispatch) is { } duration)
        {

            console.WritePayload($"  Remote time: {duration}");

        }

        if (dispatch.ContinuationNeed is { Length: > 0 } need)
        {

            console.WritePayload(string.Empty);

            console.WritePayload(
                $"The remote agent is waiting for {(need == "auth" ? "authentication" : "more input")}. Answer it with:");

            console.WritePayload(
                $"  arcanum conclave continue {dispatch.TaskId} --agent-url {agentUrl} --message \"…\"");

        }

        console.WritePayload(string.Empty);

        console.WritePayload(dispatch.ResponseText);

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Renders remote cost, keeping "the peer said nothing" visibly distinct from "the peer said zero"
    /// (issue #60).
    /// </summary>
    private static string DescribeCost(SendingDispatchDto dispatch)
    {

        if (!dispatch.CostKnown)
        {

            return "unknown (the remote agent reported no usage)";

        }

        string tokens = dispatch.RemoteTotalTokens is { } t ? $"{t:N0} tokens" : "tokens unknown";

        string cost = dispatch.RemoteCostUsd is { } c ? $"${c:0.######} USD" : "cost unknown";

        return $"{tokens}, {cost} (external)";

    }

    private static string? DescribeDuration(SendingDispatchDto dispatch) =>
        dispatch.DispatchedAt is { } from && dispatch.SettledAt is { } to && to >= from
            ? $"{(to - from).TotalSeconds:0.###} s"
            : null;

    private static string Describe(bool enabled) => enabled ? "enabled" : "disabled";

}
