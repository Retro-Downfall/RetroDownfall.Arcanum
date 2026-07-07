namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Result shape returned by the <c>browse_web</c> built-in tool.
/// </summary>
public sealed record BrowseWebResult
{

    public string Title { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public IReadOnlyList<string> Links { get; init; } = [];

}
