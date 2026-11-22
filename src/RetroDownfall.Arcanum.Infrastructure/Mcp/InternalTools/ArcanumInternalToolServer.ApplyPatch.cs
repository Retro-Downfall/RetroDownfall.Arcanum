using System.Text;
using System.Text.Json;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{
    internal Func<string, MultiFileCommitCoordinator>? PatchCoordinatorForTests { get; set; }

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
                },
                new(TurnFailurePhase.ToolPrecondition, TurnEffectState.NotDispatched, TurnRecoveryAction.InspectSession));
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
                },
                new(TurnFailurePhase.ToolArguments, TurnEffectState.NotDispatched, TurnRecoveryAction.CorrectArguments));
        }

        long budget =
            ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
                _settings.ToolOutputCapBytes,
                _maxJsonRpcLineBytes);

        ApplyPatchToolExecutionService executor = new(
            _workspaceRoot!,
            _workspacePatchSettings,
            budget,
            _json,
            coordinatorFactory: PatchCoordinatorForTests,
            allowProtectedPathWrites: _allowProtectedPathWrites);

        ApplyPatchToolExecutionResponse response =
            await executor.ExecuteAsync(
                request,
                invocation,
                cancellationToken).ConfigureAwait(false);

        TurnFailureEvidence? evidence = response.FailureEvidence;

        if (evidence is not null && response.FailureDiagnostics is { } diagnostics)
        {
            TurnFailureOutput? output = await RetainDiagnosticsAsync(
                diagnostics,
                _json.WorkspacePatchToolResultEnvelope,
                ToolRiskClassifier.ApplyPatchToolName,
                invocation.SessionId,
                cancellationToken).ConfigureAwait(false);

            if (output is not null)
            {
                evidence = evidence with { Output = output };

                if (!invocation.ReceiptHandled)
                {
                    string serialized = executor.SerializeBounded(diagnostics with { Output = output });

                    return WithOwnerEvidence(StructuredTextResult(serialized, isError: false), evidence);
                }
            }
        }

        McpToolsCallResultWire result = WithOwnerEvidence(StructuredTextResult(response.SerializedResult, isError: false), evidence);

        return response.CommittedWorkspaceMutations is { } facts
            ? result with { Meta = (result.Meta ?? new McpToolResultMetadata()) with { CommittedWorkspaceMutations = facts } }
            : result;
    }

    private McpToolsCallResultWire BuildSmallStructuredPatchResult(
        WorkspacePatchToolResultEnvelope result,
        TurnFailureEvidence evidence)
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

        return WithOwnerEvidence(StructuredTextResult(serialized, isError: false), evidence);
    }
}
