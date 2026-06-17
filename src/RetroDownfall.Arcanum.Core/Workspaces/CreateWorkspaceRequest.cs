namespace RetroDownfall.Arcanum.Core.Workspaces;

public sealed record CreateWorkspaceRequest(
    string Name,
    string Path,
    WorkspaceType Type);
