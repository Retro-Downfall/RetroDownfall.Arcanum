using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{

    private async Task<McpToolsCallResultWire> ExecuteWorkspaceCheckAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {

        McpToolsCallResultWire? gate = TryRequireWorkspaceRoot();

        if (gate is not null)
        {

            return gate;
        }

        WorkspaceCheckExecutionStatus capability =
            _workspaceCheckRuntime.GetStatus(_workspaceRoot!);

        if (!capability.IsEligible)
        {

            return BuildBoundedStructuredWorkspaceCheckResult(
                new WorkspaceCheckToolResultEnvelope
                {
                    Status = "unavailable",
                    Code = "capability_unavailable",
                    Message = capability.Reason,
                });
        }

        WorkspaceCheckParams? request;

        try
        {

            request = JsonSerializer.Deserialize(
                arguments,
                _json.WorkspaceCheckParams);
        }
        catch (JsonException)
        {

            request = null;
        }

        if (request is null
            || string.IsNullOrWhiteSpace(request.Profile))
        {

            return BuildBoundedStructuredWorkspaceCheckResult(
                new WorkspaceCheckToolResultEnvelope
                {
                    Status = "invalid_request",
                    Code = "invalid_request",
                    Message = "workspace_check requires a closed 'profile' ID.",
                });
        }

        WorkspaceCheckToolResultEnvelope result =
            await _workspaceCheckRuntime.RunAsync(
                new WorkspaceCheckRuntimeRequest(
                    _workspaceRoot!,
                    request.Profile,
                    request.Options
                        ?? new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)),
                cancellationToken).ConfigureAwait(false);

        TurnFailureEvidence? evidence = ObserveWorkspaceCheckFailure(result);

        if (evidence is not null && result.MaterializationItemCount > 0)
        {
            TurnFailureOutput? output = await RetainDiagnosticsAsync(
                result,
                _json.WorkspaceCheckToolResultEnvelope,
                ToolRiskClassifier.WorkspaceCheckToolName,
                DiagnosticSessionId,
                cancellationToken).ConfigureAwait(false);

            if (output is not null)
            {
                result = result with { Output = output };
                evidence = evidence with { Output = output, NextAction = TurnRecoveryAction.ReadToolOutput };
            }
        }

        return BuildBoundedStructuredWorkspaceCheckResult(result, evidence);
    }

    private static TurnFailureEvidence? ObserveWorkspaceCheckFailure(
        WorkspaceCheckToolResultEnvelope result) =>
        result.ProcessStartFailure ?? (result.Status switch
        {
            "ok" => null,
            "invalid_request" => new(TurnFailurePhase.ToolArguments, TurnEffectState.NotDispatched, TurnRecoveryAction.CorrectArguments),
            "restore_required" => new(TurnFailurePhase.ToolPrecondition, TurnEffectState.NotDispatched, TurnRecoveryAction.InspectWorkspace),
            "unavailable" when result.Code != "resource_limit_unavailable" =>
                new(TurnFailurePhase.ToolAuthorization, TurnEffectState.NotDispatched, TurnRecoveryAction.UseAuthorizedScope),
            "failed" when result.ExitCode is not null =>
                new(TurnFailurePhase.ToolExecution, TurnEffectState.Completed, TurnRecoveryAction.InspectWorkspace),
            _ => new(TurnFailurePhase.ToolExecution, TurnEffectState.Unknown, TurnRecoveryAction.InspectWorkspace),
        });

    private McpToolsCallResultWire BuildBoundedStructuredWorkspaceCheckResult(
        WorkspaceCheckToolResultEnvelope result,
        TurnFailureEvidence? evidence = null)
    {
        McpToolsCallResultWire wire = BuildBoundedStructuredResult(
            result,
            _json.WorkspaceCheckToolResultEnvelope,
            isError: false,
            static source => source.RetainLeadingItems(0) with
            {
                StandardOutput = null,
                StandardError = null,
                Truncated = true,
            });

        return WithOwnerEvidence(wire, evidence ?? ObserveWorkspaceCheckFailure(result));
    }
}
