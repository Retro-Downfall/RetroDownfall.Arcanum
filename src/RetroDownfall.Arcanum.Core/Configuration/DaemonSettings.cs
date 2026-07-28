namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record DaemonSettings
{

    public List<UnseenServantJob> Jobs { get; set; } = [];

    /// <summary>
    /// Caps the number of Unseen Servant jobs that can run concurrently. Use to throttle
    /// host CPU/GPU/LLM backend load when many enabled jobs come due on the same tick.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 8;

}
