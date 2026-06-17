namespace RetroDownfall.Arcanum.Core.Workspaces;

public sealed record FileListRequest(
    string? RelativePath = null,
    bool Recursive = false,
    string? SearchPattern = null);
