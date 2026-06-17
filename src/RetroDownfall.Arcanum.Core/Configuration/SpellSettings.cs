namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Spell management API path containment.
/// </summary>
public sealed record SpellSettings
{

    /// <summary>
    /// Optional list of absolute directory roots that spell CRUD routes may use when
    /// <c>?workspace=</c> is supplied or when a default workspace is resolved.
    /// When empty (default), any existing directory the process can access is permitted
    /// (caller still needs the API key). When non-empty, resolved workspace paths must
    /// fall under one of these roots.
    /// </summary>
    public string[] AllowedWorkspaceRoots { get; init; } = [];

    /// <summary>
    /// Maximum <c>SPELL.md</c> (and related frontmatter) read size in bytes. Default 256 KiB; clamp 1 KiB–1 MiB,
    /// further capped by <see cref="WorkspaceSettings.MaxFileReadSizeBytes"/>.
    /// </summary>
    public long MaxFileSizeBytes { get; init; } = 262_144L;

}
