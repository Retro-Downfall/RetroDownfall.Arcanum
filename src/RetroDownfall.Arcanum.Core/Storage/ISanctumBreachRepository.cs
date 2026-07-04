namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Grimoire-backed persistence for Sanctum breach history, replacing the previous in-memory ring
/// buffer (<c>SanctumBreachStore</c>). Writes enforce a per-campaign retention limit by deleting
/// the oldest rows beyond the configured maximum.
/// </summary>
public interface ISanctumBreachRepository
{

    /// <summary>
    /// Persists a breach and enforces retention (deletes oldest rows for the campaign beyond
    /// <paramref name="maxBreachCount"/>, clamped via <c>ArcanumSettingClamps.SanctumMaxBreachCount</c>).
    /// </summary>
    Task RecordAsync(SanctumBreachRecord breach, int maxBreachCount, CancellationToken ct = default);

    Task<IReadOnlyList<SanctumBreachRecord>> QueryAsync(
        string campaignId,
        int limit,
        DateTimeOffset? before = null,
        string? toolName = null,
        CancellationToken ct = default);

    Task<int> GetCountAsync(string campaignId, CancellationToken ct = default);

    Task<int> DeleteOldestAsync(string campaignId, int count, CancellationToken ct = default);

}
