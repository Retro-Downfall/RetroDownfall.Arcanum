using RetroDownfall.Arcanum.Core.Logging;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record LogSettings
{

    /// <summary>
    /// Capacity of the in-memory ring buffer. Read once at construction of
    /// <c>InMemoryLogRingBuffer</c>; changes require a restart to take effect.
    /// </summary>
    public int RingBufferCapacity { get; init; } = 10_000;

    public LogLevel MinLevelInBuffer { get; init; } = LogLevel.Information;

}
