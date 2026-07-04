namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Variable, breach-type-specific detail captured alongside a <see cref="SanctumBreachRecord"/>.
/// Serialized as the <c>DetailsJson</c> column via <c>TheForgeJsonContext.Default.SanctumBreachDetails</c>.
/// </summary>
/// <remarks>
/// <see cref="RequestedPath"/> (pre-resolution) is retained alongside <see cref="ResolvedPath"/>
/// (post-symlink-resolution, canonical for audit) because the former is useful when diagnosing a
/// specific symlink misconfiguration even though it is not the primary audit value.
/// </remarks>
public sealed record SanctumBreachDetails(
    string? RequestedPath,
    string? ResolvedPath,
    string? WorkspaceRoot,
    string? RequestedUrl,
    string? ToolArguments,
    string? LimitValue,
    string? ActualValue);
