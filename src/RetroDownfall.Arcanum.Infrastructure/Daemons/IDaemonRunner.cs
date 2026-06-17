using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Daemons;

public interface IDaemonRunner
{

    Task<Result<DaemonExecutionSummary>> RunAsync(string daemonId, bool force, CancellationToken ct);

    Task<Result<DaemonExecutionSummary>> RunScheduledAsync(string daemonId, CancellationToken ct);

}
