using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps the <c>/api/saga</c> route group for The Archive (saga memory browser).</summary>
public sealed class SagaService
{

    private readonly ArcanumApiClient _apiClient;

    public SagaService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<SagaMemoryDto[]>?> ListAsync(
        string? query,
        Guid? sessionId,
        int? limit,
        int? offset,
        CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            "/api/saga",
            ("q", query),
            ("sessionId", sessionId?.ToString()),
            ("limit", limit?.ToString()),
            ("offset", offset?.ToString()));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseSagaMemoryDtoArray, cancellationToken);

    }

    public Task<ApiResponse<SagaSearchResult>?> SearchAsync(string query, int? limit, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/saga/divine",
            new SagaSearchRequest(query, limit),
            TheForgeJsonContext.Default.SagaSearchRequest,
            TheForgeJsonContext.Default.ApiResponseSagaSearchResult,
            cancellationToken);

    /// <summary><c>DELETE /api/saga/{id}</c> — 204 / 404 <c>Saga.NotFound</c>. <paramref name="id"/> is the Saga memory's string id.</summary>
    public Task<ApiResponse<bool>?> DeleteAsync(string id, CancellationToken cancellationToken) =>
        _apiClient.DeleteAsync(
            $"/api/saga/{Uri.EscapeDataString(id)}",
            TheForgeJsonContext.Default.ApiResponseBoolean,
            cancellationToken);

    /// <summary><c>DELETE /api/saga?confirm=true</c> — success is <c>204 No Content</c>.</summary>
    public Task<bool> DeleteAllAsync(CancellationToken cancellationToken) =>
        _apiClient.DeleteNoContentAsync("/api/saga?confirm=true", cancellationToken);

    /// <summary><c>GET /api/saga/stats</c> — always available (not gated on SagaEnabled).</summary>
    public Task<ApiResponse<SagaStats>?> GetStatsAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/saga/stats", TheForgeJsonContext.Default.ApiResponseSagaStats, cancellationToken);

}
