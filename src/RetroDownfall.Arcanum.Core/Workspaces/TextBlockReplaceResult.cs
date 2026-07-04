namespace RetroDownfall.Arcanum.Core.Workspaces;

public sealed record TextBlockReplaceResult(
    string RelativePath,
    int Replacements,
    long BytesWritten,
    DateTimeOffset ModifiedAt);
