using RetroDownfall.Arcanum.Core.Daemons;

namespace RetroDownfall.Arcanum.Infrastructure.Daemons;

public sealed class DaemonJobRegistry(IEnumerable<IDaemonJob> jobs) : IDaemonRegistry
{

    private readonly IDaemonJob[] _jobs = jobs.ToArray();

    public Task<DaemonJobInfo[]> GetAllAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        DaemonJobInfo[] result = _jobs
            .Select(ToInfo)
            .OrderBy(j => j.Name, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(result);
    }

    public Task<DaemonJobInfo?> GetAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        IDaemonJob? job = _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.Ordinal));

        return Task.FromResult(job is null ? null : ToInfo(job));
    }

    public IDaemonJob? TryGetJob(string id) =>
        _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.Ordinal));

    private static DaemonJobInfo ToInfo(IDaemonJob job) =>
        new(job.Id, job.Name, job.Description, job.CanRunOnDemand);

}
