namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record SessionSettings
{

    public int? DefaultQueryLimit { get; init; } = 100;

    public int MaxStreamReplayEntries { get; init; } = 500;

    public int MaxEntriesPerSession { get; init; } = 100_000;

    public int MaxEntryContentBytes { get; init; } = 1_048_576;

}
