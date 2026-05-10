namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record PerceptionSettings
{

    public int MaxEnumerationSteps { get; init; } = 50_000;

    public int MaxTableOfContentsLines { get; init; } = 20;

    /// <summary>
    /// Optional list of absolute directory roots that <c>GET /api/perception/look</c> is
    /// allowed to scan. When empty (default), any directory the process can read is permitted
    /// (caller still needs the API key). When non-empty, requested paths must resolve under
    /// one of these roots.
    /// </summary>
    public string[] AllowedWorkspaceRoots { get; init; } = [];

}
