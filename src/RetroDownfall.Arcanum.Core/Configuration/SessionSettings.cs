namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record SessionSettings
{

    public int? DefaultQueryLimit { get; set; } = 100;

    public int MaxStreamReplayEntries { get; set; } = 500;

    public int MaxEntriesPerSession { get; set; } = 100_000;

    public int MaxEntryContentBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Maximum fork lineage depth — a session forked from a session that was itself forked
    /// <see cref="MaxForkDepth"/> times is rejected with <c>Session.ForkDepthExceeded</c>. Mirrors
    /// <c>ConclaveSettings.MaxDelegationDepth</c>'s role for Apprentice delegation trees. Default
    /// <c>3</c>; clamped 0–20 (<c>0</c> permits only forking an un-forked, "root" session).
    /// </summary>
    public int MaxForkDepth { get; set; } = 3;

    /// <summary>
    /// When false (default), memory-management endpoints (<c>DELETE /entries</c>, pin/unpin, compact)
    /// return <c>Session.MemoryManagementDisabled</c>. Gate remains off until an operator explicitly
    /// enables it.
    /// </summary>
    public bool AllowMemoryManagement { get; set; } = false;

    /// <summary>
    /// Maximum pinned entries per session. Pinned entries are always included in inference context
    /// even when compression would otherwise drop them. Default <c>10</c>; clamped 0–100.
    /// </summary>
    public int MaxPinnedEntries { get; set; } = 10;

}
