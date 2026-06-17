namespace RetroDownfall.Arcanum.Core.Workspaces;

public sealed record WorkspaceInfo(
    string Id,
    string Name,
    string Path,
    WorkspaceType Type,
    DateTimeOffset RegisteredAt,
    bool Persisted = false);
