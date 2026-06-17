namespace RetroDownfall.Arcanum.Core.Workspaces;

public sealed record FileEntry(
    string Name,
    string RelativePath,
    string FullPath,
    FileEntryType Type,
    long Size,
    DateTimeOffset LastModified);
