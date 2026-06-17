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

    /// <summary>Get recent breaches for a campaign.</summary>
    Task<IReadOnlyList<SanctumBreach>> GetBreachesAsync(string campaignId, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Returns clamped <see cref="ResourceLimits"/> for a workspace path (campaign Sanctum config when registered, otherwise defaults).
    /// </summary>
    Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
        string? workspaceRoot,
        CancellationToken ct = default);

}
