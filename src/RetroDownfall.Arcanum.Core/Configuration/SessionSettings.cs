namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record SessionSettings
{

    public int? DefaultQueryLimit { get; init; } = 100;

    public int MaxStreamReplayEntries { get; init; } = 500;

    public int MaxEntriesPerSession { get; init; } = 100_000;

    public int MaxEntryContentBytes { get; init; } = 1_048_576;

    /// <summary>
    /// Maximum fork lineage depth — a session forked from a session that was itself forked
    /// <see cref="MaxForkDepth"/> times is rejected with <c>Session.ForkDepthExceeded</c>. Mirrors
    /// <c>ConclaveSettings.MaxDelegationDepth</c>'s role for Apprentice delegation trees. Default
    /// <c>3</c>; clamped 0–20 (<c>0</c> permits only forking an un-forked, "root" session).
    /// </summary>
    public int MaxForkDepth { get; init; } = 3;

}
