using RetroDownfall.Arcanum.Core.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

public interface ILogQueryService
{

    Task<LogQueryResult> QueryAsync(LogQueryRequest request, CancellationToken ct);

    IAsyncEnumerable<LogEntry> StreamAsync(LogQueryRequest? request, CancellationToken ct);

}
