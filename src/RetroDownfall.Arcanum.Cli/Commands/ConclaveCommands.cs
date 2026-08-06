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

            return (int)CliExitCode.NetworkError;

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
    public async Task<int> Dispatch(string? agentUrl, string? goal, string? name, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(agentUrl) || string.IsNullOrWhiteSpace(goal))
        {

            console.WriteDiagnostic("Both --agent-url and --goal are required.");

            return (int)CliExitCode.ConfigurationError;

        }

        Result<SendingDispatchDto> result = await apiClient
            .DispatchSendingAsync(agentUrl.Trim(), goal.Trim(), name, cancellationToken)
            .ConfigureAwait(false);

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

        console.WritePayload(string.Empty);

        console.WritePayload(dispatch.ResponseText);

        return (int)CliExitCode.Success;

    }

    private static string Describe(bool enabled) => enabled ? "enabled" : "disabled";

}
