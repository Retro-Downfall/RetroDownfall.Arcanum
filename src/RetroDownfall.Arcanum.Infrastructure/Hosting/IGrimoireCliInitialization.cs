namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Runs one direct CLI storage operation while the process exclusively owns the installation.
/// </summary>
public interface IGrimoireCliInitialization
{
    /// <summary>
    /// Runs under exclusive installation ownership without creating or converging Grimoire storage.
    /// </summary>
    Task<T> RunExclusiveAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs under exclusive installation ownership after full Grimoire bootstrap and recovery.
    /// </summary>
    Task<T> RunExclusiveWithBootstrapAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}
