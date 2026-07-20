namespace RetroDownfall.Arcanum.Core.TheForge;

public interface IGrimoireDbReadiness
{

    bool IsReady { get; }

    void MarkReady();

    /// <summary>
    /// Completes when <see cref="MarkReady"/> has run. Faults when <see cref="MarkFailed"/> is called
    /// (bootstrap threw). Cancels when <paramref name="cancellationToken"/> fires.
    /// </summary>
    Task WaitUntilReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signals that Grimoire bootstrap failed so <see cref="WaitUntilReadyAsync"/> does not hang.
    /// </summary>
    void MarkFailed(Exception exception);

}
