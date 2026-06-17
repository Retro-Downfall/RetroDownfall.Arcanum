using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;

namespace RetroDownfall.Arcanum.Core.TheForge;

public interface ISessionRepository
{

    Task<Session> CreateAsync(Guid? campaignId, string? title, CancellationToken ct);

    Task<Session?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<SessionQueryResult> QueryAsync(SessionQueryRequest request, CancellationToken ct);

    Task<SessionAnalytics> GetAnalyticsAsync(CancellationToken ct);

    Task<Result<SessionExportResult>> ExportAsync(Guid id, SessionExportFormat format, CancellationToken ct);

    Task<Entry> AddEntryAsync(Guid sessionId, Entry entry, CancellationToken ct);

    Task<List<Entry>> GetEntriesAscendingAsync(Guid sessionId, int takeLast, CancellationToken ct = default);

    Task<List<Entry>> GetEntriesAfterAsync(
        Guid sessionId,
        DateTimeOffset afterCreatedAt,
        Guid afterId,
        int limit,
        CancellationToken ct = default);

    Task<Entry?> GetEntryAsync(Guid sessionId, Guid entryId, CancellationToken ct = default);

    Task<List<Entry>> GetEntriesAsync(
        Guid sessionId,
        int offset = 0,
        int limit = 100,
        DateTimeOffset? beforeCreatedAt = null,
        Guid? beforeId = null,
        CancellationToken ct = default);

    Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken ct);

    Task UpdateSessionAsync(Session session, CancellationToken ct);

    Task ArchiveAsync(Guid id, CancellationToken ct);

}
