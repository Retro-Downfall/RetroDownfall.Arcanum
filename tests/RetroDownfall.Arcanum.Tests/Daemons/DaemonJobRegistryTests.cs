using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Infrastructure.Daemons;

namespace RetroDownfall.Arcanum.Tests.Daemons;

public sealed class DaemonJobRegistryTests
{

    [Fact]
    public async Task GetAllAsync_returns_jobs_sorted_by_name()
    {

        FakeDaemonJob zebra = new("z", "Zebra", canRunOnDemand: true);

        FakeDaemonJob alpha = new("a", "Alpha", canRunOnDemand: false);

        DaemonJobRegistry registry = new([zebra, alpha]);

        DaemonJobInfo[] all = await registry.GetAllAsync(CancellationToken.None);

        Assert.Equal(["Alpha", "Zebra"], all.Select(j => j.Name).ToArray());

    }

    [Fact]
    public async Task GetAsync_and_TryGetJob_resolve_by_id()
    {

        FakeDaemonJob job = new("job-1", "Job One", canRunOnDemand: true);

        DaemonJobRegistry registry = new([job]);

        DaemonJobInfo? info = await registry.GetAsync("job-1", CancellationToken.None);

        Assert.NotNull(info);

        Assert.Equal("Job One", info!.Name);

        Assert.Same(job, registry.TryGetJob("job-1"));

        Assert.Null(registry.TryGetJob("missing"));

    }

    private sealed class FakeDaemonJob(string id, string name, bool canRunOnDemand) : IDaemonJob
    {

        public string Id { get; } = id;

        public string Name { get; } = name;

        public string? Description => null;

        public bool CanRunOnDemand { get; } = canRunOnDemand;

        public string TargetSpell => "spell";

        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;

    }

}
