using RetroDownfall.Arcanum.Core.Logging;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed class LogSettings
{

    public int RingBufferCapacity { get; init; } = 10_000;

    public LogLevel MinLevelInBuffer { get; init; } = LogLevel.Information;

}
