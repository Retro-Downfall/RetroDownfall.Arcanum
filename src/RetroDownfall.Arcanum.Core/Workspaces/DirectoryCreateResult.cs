namespace RetroDownfall.Arcanum.Core.Workspaces;

public sealed record DirectoryCreateResult(
    string RelativePath,
    DateTimeOffset CreatedAt);
