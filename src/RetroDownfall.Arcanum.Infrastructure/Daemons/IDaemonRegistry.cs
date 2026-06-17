using RetroDownfall.Arcanum.Core.Daemons;

namespace RetroDownfall.Arcanum.Infrastructure.Daemons;

public interface IDaemonRegistry
{

    Task<DaemonJobInfo[]> GetAllAsync(CancellationToken ct);

    Task<DaemonJobInfo?> GetAsync(string id, CancellationToken ct);

    IDaemonJob? TryGetJob(string id);

}
