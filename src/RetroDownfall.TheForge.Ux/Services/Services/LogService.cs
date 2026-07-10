using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps <c>GET /api/logs</c> and <c>GET /api/events/logs</c> (SSE) for The Foundry Floor.</summary>
public sealed class LogService : ILogService
{

    private readonly ArcanumApiClient _apiClient;

    private readonly ArcanumSseClient _sseClient;

    public LogService(ArcanumApiClient apiClient, ArcanumSseClient sseClient)
    {

        _apiClient = apiClient;

        _sseClient = sseClient;

    }

    public Task<ApiResponse<LogQueryResult>?> QueryAsync(
        LogLevel? minLevel,
        string? category,
        string? search,
        int? limit,
        CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            "/api/logs",
            ("minLevel", minLevel?.ToString()),
            ("category", category),
            ("search", search),
            ("limit", limit?.ToString()));

        return _apiClient.GetAsync(path, ForgeJsonContext.Default.ApiResponseLogQueryResult, cancellationToken);

    }

    public IAsyncEnumerable<LogEntry> StreamLogsAsync(CancellationToken cancellationToken) =>
        _sseClient.StreamLogsAsync(cancellationToken);

}
