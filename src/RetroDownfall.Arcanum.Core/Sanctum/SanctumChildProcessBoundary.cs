namespace RetroDownfall.Arcanum.Core.Sanctum;

/// <summary>
/// Campaign-scoped path-boundary facts used when planning an OS filesystem jail for
/// <c>execute_command</c> / <c>run_spell_script</c> children.
/// </summary>
public sealed record SanctumChildProcessBoundary(
    string WorkspaceRoot,
    bool PathBoundaryRequired,
    IReadOnlyList<string> AllowedPaths);
