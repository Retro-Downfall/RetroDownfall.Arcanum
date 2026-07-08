using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>API-backed data source for The Tome.</summary>
public sealed class TomeDataSource : ITomeDataSource
{

    private readonly SessionService _sessionService;

    public TomeDataSource(SessionService sessionService)
    {

        _sessionService = sessionService;

    }

    public async Task<SessionDetailDto?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {

        ApiResponse<SessionDetailDto>? response = await _sessionService.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public IAsyncEnumerable<IntelligenceEvent> PingStreamAsync(PingRequest request, CancellationToken cancellationToken) =>
        _sessionService.PingStreamAsync(request, cancellationToken);

    public async Task<EntryDto?> AppendEntryAsync(Guid sessionId, AppendEntryRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<EntryDto>? response = await _sessionService.AppendEntryAsync(sessionId, request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<SessionDetailDto?> ForkAsync(Guid sessionId, ForkSessionRequest? request, CancellationToken cancellationToken)
    {

        ApiResponse<SessionDetailDto>? response = await _sessionService.ForkAsync(sessionId, request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<SessionExportResult?> ExportAsync(Guid sessionId, string format, CancellationToken cancellationToken)
    {

        ApiResponse<SessionExportResult>? response = await _sessionService.ExportAsync(sessionId, format, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public IAsyncEnumerable<EntryDto> StreamEntriesAsync(Guid sessionId, DateTimeOffset? since, CancellationToken cancellationToken) =>
        _sessionService.StreamEntriesAsync(sessionId, since, cancellationToken);

}
