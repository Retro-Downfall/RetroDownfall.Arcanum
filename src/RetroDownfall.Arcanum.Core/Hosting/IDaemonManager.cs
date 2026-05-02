using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Hosting;

public interface IDaemonManager
{
    Task<Result> InstallAsync(CancellationToken cancellationToken);

    Task<Result> UninstallAsync(CancellationToken cancellationToken);

    Task<Result<string>> GetStatusAsync(CancellationToken cancellationToken);
}
