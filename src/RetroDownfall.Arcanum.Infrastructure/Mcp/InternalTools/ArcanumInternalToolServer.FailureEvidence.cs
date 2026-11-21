using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{
    private Guid? DiagnosticSessionId =>
        PersistedToolInvocationAmbient.Current?.SessionId ?? SessionAttachmentToolAmbient.CurrentSessionId;

    private bool CanReadDiagnostics(string ownerTool) =>
        ownerTool == ToolRiskClassifier.ApplyPatchToolName
        || (ownerTool == ToolRiskClassifier.WorkspaceCheckToolName
            && _workspaceCheckRuntime.CanReadRetainedOutput(_workspaceRoot!));

    private async ValueTask<TurnFailureOutput?> RetainDiagnosticsAsync<T>(
        T diagnostics,
        JsonTypeInfo<T> json,
        string ownerTool,
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            CommandOutputArtifactRegistration artifact = await _commandOutputArtifacts.RegisterDiagnosticsAsync(
                diagnostics, json, new CommandDiagnosticScope(ownerTool, sessionId), cancellationToken).ConfigureAwait(false);

            return new(ToolRiskClassifier.ReadToolOutputToolName, artifact.Handle, [.. artifact.AvailableStreams]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _logger?.LogWarning("Could not retain diagnostic output: {ExceptionType}", exception.GetType().Name);

            return null;
        }
    }

    private static McpToolsCallResultWire WithOwnerEvidence(
        McpToolsCallResultWire result,
        TurnFailureEvidence? evidence) =>
        evidence is null ? result : result with
        {
            Meta = (result.Meta ?? new McpToolResultMetadata()) with { FailureEvidence = evidence },
        };
}
