using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal enum TrustedStructuredToolResultKind
{
    WorkspaceSearch,
    WorkspacePatch,
    WorkspaceCheck,
    WorkspaceFileMutation,
}

/// <summary>
/// In-process provenance marker applied only by Arcanum's internal MCP bridge.
/// It is intentionally not part of the MCP wire contract.
/// </summary>
internal sealed record TrustedStructuredToolResult(
    TrustedStructuredToolResultKind Kind,
    string Text,
    bool ReceiptHandled = false)
{
    internal TurnFailureEvidence? FailureEvidence { get; init; }

    internal WorkspaceMutationFact[]? CommittedWorkspaceMutations { get; init; }

    public override string ToString() => Text;
}
