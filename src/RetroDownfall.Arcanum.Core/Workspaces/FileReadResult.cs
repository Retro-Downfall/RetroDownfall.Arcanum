namespace RetroDownfall.Arcanum.Core.Workspaces;

public sealed record FileReadResult(
    string RelativePath,
    string Content,
    string Encoding,
    long Size,
    DateTimeOffset LastModified);
