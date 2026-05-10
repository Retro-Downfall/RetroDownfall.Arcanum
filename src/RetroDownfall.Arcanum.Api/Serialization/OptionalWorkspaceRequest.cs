namespace RetroDownfall.Arcanum.Api.Serialization;

/// <summary>
/// Optional JSON body for routes that only need a workspace directory (e.g. MCP reload, arsenal).
/// </summary>
public sealed record OptionalWorkspaceRequest(string? WorkingDirectory = null);
