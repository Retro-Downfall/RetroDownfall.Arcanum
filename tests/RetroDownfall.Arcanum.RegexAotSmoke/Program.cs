using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

RuntimeWorkspaceRegexCreationResult linear = RuntimeWorkspaceRegexFactory.Create(
    @"magic\d+",
    caseSensitive: false,
    TimeSpan.FromMilliseconds(100));

if (!linear.Success
    || linear.Engine != RuntimeWorkspaceRegexEngine.NonBacktracking
    || linear.FallbackAttempted
    || linear.Regex is null
    || !linear.Regex.IsMatch("MAGIC42"))
{
    Console.Error.WriteLine("Native-AOT regex smoke failed on the NonBacktracking path.");
    return 1;
}

RuntimeWorkspaceRegexCreationResult fallback = RuntimeWorkspaceRegexFactory.Create(
    @"(\w+)\s+\1",
    caseSensitive: true,
    TimeSpan.FromMilliseconds(100));

if (!fallback.Success
    || fallback.Engine != RuntimeWorkspaceRegexEngine.Interpreted
    || !fallback.FallbackAttempted
    || fallback.Regex is null
    || !fallback.Regex.IsMatch("echo echo"))
{
    Console.Error.WriteLine("Native-AOT regex smoke failed on the interpreted fallback path.");
    return 2;
}

Console.WriteLine("REGEX_AOT_SMOKE_OK non_backtracking interpreted");

return 0;
