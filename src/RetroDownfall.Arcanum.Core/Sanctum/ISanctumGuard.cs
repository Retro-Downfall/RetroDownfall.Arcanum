using RetroDownfall.Arcanum.Core.Platform;

namespace RetroDownfall.Arcanum.Core.Sanctum;

public interface ISanctumGuard
{

    /// <summary>Validate a file operation against the campaign's sanctum.</summary>
    Task<SanctumResult> ValidatePathAsync(
        string campaignId,
        string requestedPath,
        string operationType,
        string toolName,
        CancellationToken ct = default);

    /// <summary>Validate a network request.</summary>
    Task<SanctumResult> ValidateNetworkAsync(
        string campaignId,
        string url,
        string toolName,
        CancellationToken ct = default);

    /// <summary>Check if a tool is permitted in this sanctum.</summary>
    Task<SanctumResult> ValidateToolAsync(string campaignId, string toolName, CancellationToken ct = default);

    /// <summary>
    /// Returns clamped <see cref="ResourceLimits"/> for a workspace path (campaign Sanctum config when registered, otherwise defaults).
    /// </summary>
    Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
        string? workspaceRoot,
        CancellationToken ct = default);

    /// <summary>
    /// Returns workspace + Sanctum path-boundary policy for child-process FS jailing. Null when
    /// <paramref name="workspaceRoot"/> does not resolve to a known campaign (callers still jail
    /// against the workspace root alone on Linux/macOS).
    /// </summary>
    Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
        string? workspaceRoot,
        CancellationToken ct = default);

    /// <summary>
    /// Records a <c>ResourceLimit</c> breach (OS-enforced CPU/memory/file-descriptor cap exceeded, or
    /// the limit could not be applied) for the campaign resolved from <paramref name="workspaceRoot"/>.
    /// A no-op (log-only) when the path does not resolve to a known campaign, since breach persistence
    /// requires an existing campaign row (foreign key).
    /// </summary>
    Task RecordResourceLimitBreachAsync(
        string? workspaceRoot,
        string toolName,
        ResourceLimitKind resource,
        string limitValue,
        string? actualValue,
        CancellationToken ct = default);

}
