namespace RetroDownfall.Arcanum.Core.Workspaces;

public sealed record FileWriteResult(
    string RelativePath,
    long BytesWritten,
    DateTimeOffset ModifiedAt);
