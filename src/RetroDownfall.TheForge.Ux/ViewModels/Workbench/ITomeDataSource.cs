using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>Data-source seam for The Tome, implemented by the API-backed adapter in production and fakes in tests.</summary>
public interface ITomeDataSource
{

    Task<SessionDetailDto?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    IAsyncEnumerable<IntelligenceEvent> PingStreamAsync(PingRequest request, CancellationToken cancellationToken);

    Task<EntryDto?> AppendEntryAsync(Guid sessionId, AppendEntryRequest request, CancellationToken cancellationToken);

    Task<SessionDetailDto?> ForkAsync(Guid sessionId, ForkSessionRequest? request, CancellationToken cancellationToken);

    Task<SessionExportResult?> ExportAsync(Guid sessionId, string format, CancellationToken cancellationToken);

    IAsyncEnumerable<EntryDto> StreamEntriesAsync(Guid sessionId, DateTimeOffset? since, CancellationToken cancellationToken);

}
