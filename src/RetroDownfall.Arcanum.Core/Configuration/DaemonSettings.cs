namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record DaemonSettings
{

    public List<UnseenServantJob> Jobs { get; init; } = [];

}
