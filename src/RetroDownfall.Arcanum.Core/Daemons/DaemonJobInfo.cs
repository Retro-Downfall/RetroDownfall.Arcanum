namespace RetroDownfall.Arcanum.Core.Daemons;

public sealed record DaemonJobInfo(
    string Id,
    string Name,
    string? Description,
    bool CanRunOnDemand);
