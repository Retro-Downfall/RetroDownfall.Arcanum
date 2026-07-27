using System.Text;
using System.Text.Json;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{
    private async Task<McpToolsCallResultWire> ExecuteApplyPatchAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {

        if (ApplyPatchInvocationAmbient.Current
            is not ApplyPatchInvocationContext invocation)
        {
            return BuildSmallStructuredPatchResult(
                new WorkspacePatchToolResultEnvelope
                {
                    Status = "invalid_request",
                    Code = "session_required",
                    Message =
                        "apply_patch requires a bound persisted session and assistant-turn invocation.",
                });
        }

        McpToolsCallResultWire? workspaceGate = TryRequireWorkspaceRoot();

        if (workspaceGate is not null)
        {
            return workspaceGate;
        }

        ApplyPatchParams? request;

        try
        {
            request = JsonSerializer.Deserialize(
                arguments,
                _json.ApplyPatchParams);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || request.Patch is null)
        {
            return BuildSmallStructuredPatchResult(
                new WorkspacePatchToolResultEnvelope
                {
                    Status = "invalid_request",
                    Code = "invalid_request",
                    Message =
                        "apply_patch requires a canonical unified diff in 'patch'.",
                });
        }

        long budget =
            ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
                _settings.ToolOutputCapBytes,
                _maxJsonRpcLineBytes);

        ApplyPatchToolExecutionService executor = new(
            _workspaceRoot!,
            _workspacePatchSettings,
            budget,
            _json);

        ApplyPatchToolExecutionResponse response =
            await executor.ExecuteAsync(
                request,
                invocation,
                cancellationToken).ConfigureAwait(false);

        return StructuredTextResult(
            response.SerializedResult,
            isError: false);

    }

    private McpToolsCallResultWire BuildSmallStructuredPatchResult(
        WorkspacePatchToolResultEnvelope result)
    {

        long budget =
            ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
                _settings.ToolOutputCapBytes,
                _maxJsonRpcLineBytes);

        string serialized = JsonSerializer.Serialize(
            result,
            _json.WorkspacePatchToolResultEnvelope);

        if (Encoding.UTF8.GetByteCount(serialized) > budget)
        {
            serialized = JsonSerializer.Serialize(
                new MinimalStructuredToolResultEnvelope(),
                _json.MinimalStructuredToolResultEnvelope);
        }

        return StructuredTextResult(serialized, isError: false);

    }
}
