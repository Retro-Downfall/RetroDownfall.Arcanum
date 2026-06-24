namespace RetroDownfall.Arcanum.Core.TheForge;

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

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Apprentice>> GetResumableAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Apprentice>> GetInterruptedPlanningAsync(CancellationToken cancellationToken = default);

}
