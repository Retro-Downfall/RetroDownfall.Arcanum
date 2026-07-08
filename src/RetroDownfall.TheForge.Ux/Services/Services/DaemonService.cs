using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>
/// Wraps the <c>/api/daemons</c> and <c>/api/executions</c> route groups plus
/// <c>GET /api/events/daemon</c> (SSE) for The Servants' Quarters. Unseen Servant job-interval
/// adjustment (<c>/api/{unseen-servant,daemon}/jobs/...</c>) is deferred until that UI is built.
/// </summary>
public sealed class DaemonService
{

    private readonly ArcanumApiClient _apiClient;

    private readonly ArcanumSseClient _sseClient;

    public DaemonService(ArcanumApiClient apiClient, ArcanumSseClient sseClient)
    {

        _apiClient = apiClient;

        _sseClient = sseClient;

    }

    public Task<ApiResponse<DaemonJobInfo[]>?> ListAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/daemons", ForgeJsonContext.Default.ApiResponseDaemonJobInfoArray, cancellationToken);

    public Task<ApiResponse<DaemonJobInfo>?> GetAsync(string id, CancellationToken cancellationToken) =>
        _apiClient.GetAsync($"/api/daemons/{Uri.EscapeDataString(id)}", ForgeJsonContext.Default.ApiResponseDaemonJobInfo, cancellationToken);

    public Task<ApiResponse<DaemonExecutionSummary>?> RunAsync(string id, CancellationToken cancellationToken) =>
        _apiClient.PostAsync($"/api/daemons/{Uri.EscapeDataString(id)}/run", ForgeJsonContext.Default.ApiResponseDaemonExecutionSummary, cancellationToken);

    public Task<ApiResponse<DaemonExecutionSummary[]>?> HistoryAsync(string id, CancellationToken cancellationToken) =>
        _apiClient.GetAsync(
            $"/api/daemons/{Uri.EscapeDataString(id)}/history",
            ForgeJsonContext.Default.ApiResponseDaemonExecutionSummaryArray,
            cancellationToken);

    public IAsyncEnumerable<DaemonEvent> StreamEventsAsync(CancellationToken cancellationToken) =>
        _sseClient.StreamDaemonEventsAsync(cancellationToken);

}
