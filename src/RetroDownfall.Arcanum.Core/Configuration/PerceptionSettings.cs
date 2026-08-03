namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Perception runtime projection. Path authority comes from
/// <c>Arcanum:Security:PerceptionWorkspaceRoots</c>; the table-of-contents projection is code-owned.
/// </summary>
public sealed record PerceptionSettings
{

    public int MaxTableOfContentsLines { get; set; } = 20;

    /// <summary>
    /// Optional list of absolute directory roots that <c>GET /api/perception/look</c> is
    /// allowed to scan. When empty (default), all look requests are denied with
    /// <c>403 Perception.PathNotAllowed</c> (secure-by-default; configure at least one
    /// <c>Arcanum:Security:PerceptionWorkspaceRoots</c> entry).
    /// When non-empty, requested paths must resolve under one of these roots.
    /// </summary>
    public string[] AllowedWorkspaceRoots { get; set; } = [];

}
