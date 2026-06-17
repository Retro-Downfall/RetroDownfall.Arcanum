namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record DaemonSettings
{

    public List<UnseenServantJob> Jobs { get; init; } = [];

    /// <summary>
    /// Caps the number of Unseen Servant jobs that can run concurrently. Use to throttle
    /// host CPU/GPU/LLM backend load when many enabled jobs come due on the same tick.
    /// </summary>
    public int MaxConcurrentJobs { get; init; } = 8;

    /// <summary>
    /// Maximum time (seconds) <c>StopAsync</c> waits for in-flight jobs to drain after the
    /// host begins shutting down; <c>0</c> means no wait.
    /// </summary>
    public int ShutdownDrainTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Maximum number of execution records retained per daemon in the in-memory history store.
    /// </summary>
    public int ExecutionHistoryLimit { get; init; } = 100;

}
