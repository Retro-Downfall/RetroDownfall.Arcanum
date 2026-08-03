using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence;
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

        return BuildBoundedStructuredWorkspaceCheckResult(result);
    }

    private McpToolsCallResultWire BuildBoundedStructuredWorkspaceCheckResult(
        WorkspaceCheckToolResultEnvelope result)
    {
        return BuildBoundedStructuredResult(
            result,
            _json.WorkspaceCheckToolResultEnvelope,
            isError: false,
            static source => source.RetainLeadingItems(0) with
            {
                StandardOutput = null,
                StandardError = null,
                Truncated = true,
            });
    }
}
