namespace RetroDownfall.Arcanum.Core.Workspaces;

public sealed record TextBlockReplaceRequest(
    string OldString,
    string NewString,
    int? ExpectedReplacements = null);
