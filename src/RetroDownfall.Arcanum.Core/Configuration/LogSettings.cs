using RetroDownfall.Arcanum.Core.Logging;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Runtime logging projection. The minimum buffered level comes from
/// <c>Arcanum:Host:MinLogLevelInBuffer</c>; ring capacity is code-owned.
/// </summary>
public sealed record LogSettings
{

    /// <summary>
    /// Code-owned capacity of the in-memory ring buffer.
    /// </summary>
    public int RingBufferCapacity { get; set; } = 10_000;

    public LogLevel MinLevelInBuffer { get; set; } = LogLevel.Information;

}
