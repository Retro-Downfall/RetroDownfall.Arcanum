namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed class SessionSettings
{

    public int? DefaultQueryLimit { get; init; } = 100;

    public int MaxStreamReplayEntries { get; init; } = 500;

}
