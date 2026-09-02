using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal interface IGrimoireCliStoppedHostInitialization
{

    Task<T> RunAsync<T>(
        Func<IServiceProvider,
            IStoppedHostGrimoireAuthorityIssuer,
            CancellationToken,
            Task<T>> operation,
        CancellationToken cancellationToken);

}
