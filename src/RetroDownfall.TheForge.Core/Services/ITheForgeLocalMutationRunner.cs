namespace RetroDownfall.TheForge.Core.Services;

/// <summary>
/// Runs one complete local-file mutation through the desktop host's admission boundary. The Core
/// layer supplies the target path but remains independent of the Infrastructure coordination protocol.
/// </summary>
public interface ITheForgeLocalMutationRunner
{

    Task RunAsync(
        string path,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken = default);

}
