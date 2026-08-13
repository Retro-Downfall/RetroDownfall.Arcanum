using RetroDownfall.Arcanum.Core.Hosting;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed class InstallationResetDaemonMutation(
    IDaemonManager daemonManager) : IInstallationResetPreDataMutation
{

    public Task<Result> ExecuteAsync(CancellationToken cancellationToken) =>
        daemonManager.UninstallAsync(cancellationToken);

}
