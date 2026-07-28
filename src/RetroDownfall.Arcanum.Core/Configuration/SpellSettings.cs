namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Spell management API path containment.
/// </summary>
public sealed record SpellSettings
{

    /// <summary>
    /// Optional list of absolute directory roots that spell CRUD routes may use when
    /// <c>?workspace=</c> is supplied or when a default workspace is resolved.
    /// An empty array denies all access by default (secure-by-default via
    /// <see cref="WorkspaceRootPolicy"/>). When non-empty, resolved workspace paths must
    /// fall under one of these roots.
    /// </summary>
    public string[] AllowedWorkspaceRoots { get; set; } = [];

    /// <summary>
    /// Maximum <c>SPELL.md</c> (and related frontmatter) read size in bytes. Default 256 KiB; clamp 1 KiB–1 MiB,
    /// further capped by <see cref="WorkspaceSettings.MaxFileReadSizeBytes"/>.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 262_144L;

    /// <summary>
    /// TTL in seconds for the in-process spell-metadata scan cache used by routing and Arcane Resonance.
    /// <c>0</c> disables caching.
    /// </summary>
    public int MetadataScanCacheTtlSeconds { get; set; } = 5;

    public int MaxDependencies { get; set; } = 20;

    public int MaxDeclaredTools { get; set; } = 50;

    public int MaxResonantDependencies { get; set; } = 10;

    public int MaxResonantBytes { get; set; } = 131_072;

}
