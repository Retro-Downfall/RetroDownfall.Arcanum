using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>
/// Wraps the <c>/api/sessions</c> route group and standalone-chat streaming for The Tome.
/// <see cref="PingStreamAsync"/> (<c>POST /api/intelligence/ping-stream</c>, NDJSON
/// <see cref="IntelligenceEvent"/>) is distinct from spell execution streaming on
/// <see cref="SpellService.ExecuteStreamAsync"/>. Live session observation
/// (<c>GET /api/sessions/{id}/stream</c>, SSE) is exposed via <see cref="ArcanumSseClient.StreamSessionEntriesAsync"/>.
/// </summary>
public sealed class SessionService
{

    private readonly ArcanumApiClient _apiClient;

    private readonly ArcanumSseClient _sseClient;

    public SessionService(ArcanumApiClient apiClient, ArcanumSseClient sseClient)
    {

        _apiClient = apiClient;

        _sseClient = sseClient;

    }

    /// <summary><c>POST /api/sessions</c> — creates a session; <c>CampaignId</c> may be null for a no-campaign session.</summary>
    public Task<ApiResponse<SessionDetailDto>?> CreateAsync(CreateSessionRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/sessions",
            request,
            TheForgeJsonContext.Default.CreateSessionRequest,
            TheForgeJsonContext.Default.ApiResponseSessionDetailDto,
            cancellationToken);

    public Task<ApiResponse<SessionQueryResult>?> QueryAsync(
        Guid? campaignId,
        string? status,
        string? search,
        int? limit,
        DateTimeOffset? beforeUpdatedAt,
        CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            "/api/sessions",
            ("campaignId", campaignId?.ToString()),
            ("status", status),
            ("search", search),
            ("limit", limit?.ToString()),
            ("beforeUpdatedAt", beforeUpdatedAt?.ToString("O")));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseSessionQueryResult, cancellationToken);

    }

    public Task<ApiResponse<SessionDetailDto>?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.GetAsync($"/api/sessions/{id}", TheForgeJsonContext.Default.ApiResponseSessionDetailDto, cancellationToken);

    public Task<ApiResponse<SessionDetailDto>?> ForkAsync(Guid id, ForkSessionRequest? request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/sessions/{id}/fork",
            request ?? new ForkSessionRequest(),
            TheForgeJsonContext.Default.ForkSessionRequest,
            TheForgeJsonContext.Default.ApiResponseSessionDetailDto,
            cancellationToken);

    public Task<ApiResponse<SessionExportResult>?> ExportAsync(Guid id, string format, CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build($"/api/sessions/{id}/export", ("format", format));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseSessionExportResult, cancellationToken);

    }

    /// <summary>Manual Entry toolbar action — operator-authored system notes or transcript corrections.</summary>
    public Task<ApiResponse<EntryDto>?> AppendEntryAsync(Guid id, AppendEntryRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/sessions/{id}/entries",
            request,
            TheForgeJsonContext.Default.AppendEntryRequest,
            TheForgeJsonContext.Default.ApiResponseEntryDto,
            cancellationToken);

    /// <summary>Standalone chat from The Tome — NDJSON <see cref="IntelligenceEvent"/> stream.</summary>
    public IAsyncEnumerable<IntelligenceEvent> PingStreamAsync(PingRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostNdjsonStreamAsync(
            "/api/intelligence/ping-stream",
            request,
            TheForgeJsonContext.Default.PingRequest,
            TheForgeJsonContext.Default.IntelligenceEvent,
            cancellationToken);

    /// <summary>
    /// <c>GET /api/sessions/{id}/stream</c> (SSE) — live observation of entries arriving from other
    /// sources (daemon jobs, other clients, manual appends). Replays recent entries on connect, then
    /// streams live. The Tome subscribes on open, unsubscribes on close.
    /// </summary>
    public IAsyncEnumerable<EntryDto> StreamEntriesAsync(Guid id, DateTimeOffset? since, CancellationToken cancellationToken) =>
        _sseClient.StreamSessionEntriesAsync(id, since, cancellationToken);

    /// <summary><c>GET /api/sessions/{id}/entries</c> — entry history with keyset pagination.</summary>
    public Task<ApiResponse<EntryDto[]>?> GetEntriesAsync(Guid id, int? offset, int? limit, CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            $"/api/sessions/{id}/entries",
            ("offset", offset?.ToString()),
            ("limit", limit?.ToString()));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseEntryDtoArray, cancellationToken);

    }

    /// <summary><c>DELETE /api/sessions/{id}/entries/{entryId}</c> — 204 / 400 <c>Session.MemoryManagementDisabled</c> / 404 <c>Session.EntryNotFound</c>.</summary>
    public Task<ApiResponse<bool>?> DeleteEntryAsync(Guid id, Guid entryId, CancellationToken cancellationToken) =>
        _apiClient.DeleteAsync(
            $"/api/sessions/{id}/entries/{entryId}",
            TheForgeJsonContext.Default.ApiResponseBoolean,
            cancellationToken);

    /// <summary><c>POST /api/sessions/{id}/entries/{entryId}/pin</c> — gated by <c>Arcanum:Sessions:AllowMemoryManagement</c>; 409 <c>Session.TooManyPinned</c>.</summary>
    public Task<ApiResponse<bool>?> PinEntryAsync(Guid id, Guid entryId, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/sessions/{id}/entries/{entryId}/pin",
            TheForgeJsonContext.Default.ApiResponseBoolean,
            cancellationToken);

    /// <summary><c>DELETE /api/sessions/{id}/entries/{entryId}/pin</c> — gated by <c>Arcanum:Sessions:AllowMemoryManagement</c>.</summary>
    public Task<ApiResponse<bool>?> UnpinEntryAsync(Guid id, Guid entryId, CancellationToken cancellationToken) =>
        _apiClient.DeleteAsync(
            $"/api/sessions/{id}/entries/{entryId}/pin",
            TheForgeJsonContext.Default.ApiResponseBoolean,
            cancellationToken);

    /// <summary><c>POST /api/sessions/{id}/compact</c> — gated by <c>Arcanum:Sessions:AllowMemoryManagement</c>.</summary>
    public Task<ApiResponse<CompactResult>?> CompactAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/sessions/{id}/compact",
            TheForgeJsonContext.Default.ApiResponseCompactResult,
            cancellationToken);

}
