using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>Data-source seam for The Tome, implemented by the API-backed adapter in production and fakes in tests.</summary>
public interface ITomeDataSource
{

    Task<SessionDetailDto?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    IAsyncEnumerable<IntelligenceEvent> PingStreamAsync(PingRequest request, CancellationToken cancellationToken);

    Task<EntryDto?> AppendEntryAsync(Guid sessionId, AppendEntryRequest request, CancellationToken cancellationToken);

    Task<SessionDetailDto?> ForkAsync(Guid sessionId, ForkSessionRequest? request, CancellationToken cancellationToken);

    Task<SessionExportResult?> ExportAsync(Guid sessionId, string format, CancellationToken cancellationToken);

    IAsyncEnumerable<EntryDto> StreamEntriesAsync(Guid sessionId, Guid? since, CancellationToken cancellationToken);

    Task<DataSourceResult<EntryDto[]>> GetEntriesAsync(Guid sessionId, int? offset, int? limit, CancellationToken cancellationToken);

    Task<DataSourceResult<bool>> PinEntryAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken);

    Task<DataSourceResult<bool>> UnpinEntryAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken);

    Task<DataSourceResult<bool>> DeleteEntryAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken);

    Task<DataSourceResult<CompactResult>> CompactAsync(Guid sessionId, CancellationToken cancellationToken);

}
