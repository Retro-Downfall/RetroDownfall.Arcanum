namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal enum TrustedStructuredToolResultKind
{
    WorkspaceSearch,
    WorkspacePatch,
    WorkspaceCheck,
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
    public override string ToString() => Text;
}
