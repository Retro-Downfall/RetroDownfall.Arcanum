using RetroDownfall.Arcanum.Core.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

public interface ILogRingBuffer
{

    void Write(LogEntry entry);

    IReadOnlyList<LogEntry> GetSnapshot();

    IAsyncEnumerable<LogEntry> StreamAsync(CancellationToken ct);

}
