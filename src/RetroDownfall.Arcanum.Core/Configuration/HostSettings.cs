namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record HostSettings
{

    public int Port { get; init; } = 5001;

    public int RetainedLogFileCount { get; init; } = 7;

}
