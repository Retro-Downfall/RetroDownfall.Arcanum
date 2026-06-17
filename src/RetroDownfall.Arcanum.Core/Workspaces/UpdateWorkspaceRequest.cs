namespace RetroDownfall.Arcanum.Core.Workspaces;

public sealed record UpdateWorkspaceRequest(
    string? Name,
    WorkspaceType? Type);
