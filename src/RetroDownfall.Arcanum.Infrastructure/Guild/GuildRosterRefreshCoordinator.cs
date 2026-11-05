namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>
/// Installation-host coordination shared by every request scope. The installation lease excludes
/// competing hosts; this semaphore keeps accepted refresh replay ahead of source/cache effects.
/// </summary>
internal sealed class GuildRosterRefreshCoordinator : IDisposable
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    public void Dispose() => Gate.Dispose();
}
