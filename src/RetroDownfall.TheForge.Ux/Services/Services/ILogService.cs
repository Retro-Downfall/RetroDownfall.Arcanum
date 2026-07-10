using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Log query + SSE stream surface for The Foundry Floor.</summary>
public interface ILogService
{

    Task<ApiResponse<LogQueryResult>?> QueryAsync(
        LogLevel? minLevel,
        string? category,
        string? search,
        int? limit,
        CancellationToken cancellationToken);

    IAsyncEnumerable<LogEntry> StreamLogsAsync(CancellationToken cancellationToken);

}
