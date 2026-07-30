namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// Volatile event-driven workspace indexing state. Durable file/chunk counts remain owned by the
/// workspace index inspector; this record describes watcher and reconciliation health only.
/// </summary>
public sealed record WorkspaceIndexRuntimeStatus(
    bool Watching,
    bool Degraded,
    bool Overflowed,
    bool Reconciling,
    DateTimeOffset? LastEventAt,
    DateTimeOffset? LastSuccessfulIndexAt)
{

    public static WorkspaceIndexRuntimeStatus NotWatching { get; } =
        new(false, false, false, false, null, null);

}
