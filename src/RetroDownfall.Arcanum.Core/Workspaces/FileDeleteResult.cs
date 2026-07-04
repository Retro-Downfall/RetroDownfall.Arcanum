namespace RetroDownfall.Arcanum.Core.Workspaces;

public sealed record FileDeleteResult(
    string RelativePath,
    bool WasDirectory,
    DateTimeOffset DeletedAt);
