namespace RetroDownfall.Arcanum.Core.Conclave;

using RetroDownfall.Arcanum.Core.Primitives;

public interface IApprenticeRepository
{
    Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ListPageResult<Apprentice>> ListAsync(
        Guid? campaignId,
        string? status,
        int? limit = null,
        DateTimeOffset? beforeUpdatedAt = null,
        CancellationToken cancellationToken = default);

    Task<Apprentice> AddAsync(Apprentice apprentice, CancellationToken cancellationToken = default);

    Task<Apprentice> UpdateAsync(Apprentice apprentice, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes only the execution-owned columns (plan, current step, and checkpoint) and leaves status, error
    /// message, and the Session binding alone, so a step committed after Pause or Cancel was requested can
    /// never revert that operator transition. The write lands only while the stored plan and current step are
    /// still <paramref name="expectedPlan"/> and <paramref name="expectedCurrentStep"/> — the ones the caller
    /// built on — so an operator's Reweave that landed in between is never overwritten with a stale plan.
    /// Returns false, writing nothing, when the row is gone or either has moved on.
    /// </summary>
    Task<bool> UpdateProgressAsync(
        Apprentice apprentice,
        string expectedPlan,
        int expectedCurrentStep,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the Session a run created for the Apprentice, writing that binding alone and only while the row
    /// has none. Returns false, writing nothing, when the row is gone or is already bound.
    /// </summary>
    Task<bool> BindSessionAsync(Guid id, Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the row's mutable columns only while its stored status is one of
    /// <paramref name="expectedStatuses"/> and its stored current step is still
    /// <paramref name="expectedCurrentStep"/>. Returns false, writing nothing, when either has moved on.
    /// </summary>
    Task<bool> TryUpdateAsync(
        Apprentice apprentice,
        IReadOnlyCollection<string> expectedStatuses,
        int expectedCurrentStep,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets only the status, and only while the stored status is one of
    /// <paramref name="expectedStatuses"/>. An operator transition writes through this so a row snapshot
    /// read before the execution was stopped can never revert progress the execution committed since.
    /// Returns false, writing nothing, when the row is gone or its status has moved on.
    /// </summary>
    Task<bool> TryUpdateStatusAsync(
        Guid id,
        string status,
        IReadOnlyCollection<string> expectedStatuses,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Apprentice>> GetResumableAsync(CancellationToken cancellationToken = default);
}
