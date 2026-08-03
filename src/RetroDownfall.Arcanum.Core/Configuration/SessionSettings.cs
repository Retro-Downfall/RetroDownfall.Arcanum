namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Code-owned session limits plus the runtime projection of
/// <c>Arcanum:Features:MemoryManagement</c>. This record is not a public configuration root.
/// </summary>
public sealed record SessionSettings
{

    public int? DefaultQueryLimit { get; set; } = 100;

    public int MaxStreamReplayEntries { get; set; } = 500;

    public int MaxEntryContentBytes { get; set; } = 1_048_576;

    /// <summary>
    /// When false (default), memory-management endpoints (<c>DELETE /entries</c>, pin/unpin, compact)
    /// return <c>Session.MemoryManagementDisabled</c>. The value projects
    /// <c>Arcanum:Features:MemoryManagement</c>.
    /// </summary>
    public bool AllowMemoryManagement { get; set; } = false;

    /// <summary>
    /// Maximum pinned entries exposed by the existing Forge session-management contract. Forge is
    /// outside the unrestricted-harness migration and retains this compatibility setting unchanged.
    /// </summary>
    public int MaxPinnedEntries { get; set; } = 10;

}
