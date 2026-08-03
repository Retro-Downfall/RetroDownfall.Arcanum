namespace RetroDownfall.Arcanum.Core.Workspaces;

public sealed record FileListResult(
    FileEntry[] Entries,
    string? ParentPath,
    string? NextCursor = null,
    string? ContinuationAction = null);
