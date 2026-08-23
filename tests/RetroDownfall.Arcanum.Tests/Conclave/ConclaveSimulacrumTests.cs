using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Conclave;

public sealed class ConclaveSimulacrumTests
{

    [Fact]
    public void Defaults_ConclaveDisabled()
    {
        Assert.False(ArcanumRuntimeDefaults.Conclave.Enabled);
        Assert.False(new ArcanumSettings().Features.Conclave);

    }

    [Fact]
    public async Task CastAsync_WhenConclaveDisabled_Fails()
    {

        FakeApprenticeRepository repo = new();

        ConclaveArchmage archmage = new(repo, Monitor(new ArcanumSettings()));

        Result<Apprentice> result = await archmage.CastAsync(
            new ConclaveCastRequest("Do a thing", WorkspacePath: "/tmp/ws"));

        Assert.True(result.IsFailure);

        Assert.Equal("Apprentice.ConclaveDisabled", result.Error.Code);

        Assert.Empty(repo.Items);

    }

    [Fact]
    public async Task CastAsync_WithEmptyGoal_Fails()
    {

        FakeApprenticeRepository repo = new();

        ConclaveArchmage archmage = new(repo, Monitor(EnabledSettings()));

        Result<Apprentice> result = await archmage.CastAsync(
            new ConclaveCastRequest("   ", WorkspacePath: "/tmp/ws"));

        Assert.True(result.IsFailure);

        Assert.Equal("Apprentice.InvalidGoal", result.Error.Code);

    }

    [Fact]
    public async Task CastAsync_WhenEnabled_CreatesChildWithLineageInCheckpoint()
    {

        FakeApprenticeRepository repo = new();

        ConclaveArchmage archmage = new(repo, Monitor(EnabledSettings()));

        Guid parentId = Guid.NewGuid();

        Result<Apprentice> result = await archmage.CastAsync(
            new ConclaveCastRequest("Delegated task", "Scout", "/tmp/ws", null, parentId));

        Assert.True(result.IsSuccess);

        Apprentice child = result.Value!;

        Assert.Equal(parentId, child.ParentApprenticeId);

        Assert.Equal(ApprenticeStatus.Idle.ToString(), child.Status);

        Assert.Equal("/tmp/ws", child.WorkspacePath);

        Assert.Single(repo.Items);

        ApprenticeCheckpoint? checkpoint = ApprenticeRepository.DeserializeCheckpoint(child.CheckpointData);

        Assert.NotNull(checkpoint);

        Assert.Equal(parentId, checkpoint!.ParentApprenticeId);

    }

    [Fact]
    public async Task CastAsync_WithoutParent_CreatesOrphanWithoutCheckpoint()
    {

        FakeApprenticeRepository repo = new();

        ConclaveArchmage archmage = new(repo, Monitor(EnabledSettings()));

        Result<Apprentice> result = await archmage.CastAsync(
            new ConclaveCastRequest("Lone task", WorkspacePath: "/tmp/ws"));

        Assert.True(result.IsSuccess);

        Assert.Null(result.Value!.ParentApprenticeId);

        Assert.Null(result.Value!.CheckpointData);

    }

    [Fact]
    public async Task CastAsync_DeepLineage_AllowsDelegation()
    {

        FakeApprenticeRepository repo = new();

        Guid root = Guid.NewGuid();
        Guid child = Guid.NewGuid();
        Guid grandchild = Guid.NewGuid();
        Guid greatGrandchild = Guid.NewGuid();

        repo.Items.Add(new Apprentice
        {
            Id = root,
            Name = "Root",
            Goal = "Root",
            Status = ApprenticeStatus.Running.ToString(),
            WorkspacePath = "/tmp/ws",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        repo.Items.Add(new Apprentice
        {
            Id = child,
            Name = "Child",
            Goal = "Child",
            Status = ApprenticeStatus.Running.ToString(),
            WorkspacePath = "/tmp/ws",
            ParentApprenticeId = root,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        repo.Items.Add(new Apprentice
        {
            Id = grandchild,
            Name = "Grandchild",
            Goal = "Grandchild",
            Status = ApprenticeStatus.Running.ToString(),
            WorkspacePath = "/tmp/ws",
            ParentApprenticeId = child,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        repo.Items.Add(new Apprentice
        {
            Id = greatGrandchild,
            Name = "Great-grandchild",
            Goal = "Great-grandchild",
            Status = ApprenticeStatus.Running.ToString(),
            WorkspacePath = "/tmp/ws",
            ParentApprenticeId = grandchild,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        ConclaveArchmage archmage = new(repo, Monitor(EnabledSettings()));

        Result<Apprentice> result = await archmage.CastAsync(
            new ConclaveCastRequest("Too deep", WorkspacePath: "/tmp/ws", ParentApprenticeId: greatGrandchild));

        Assert.True(result.IsSuccess);
        Assert.Equal(greatGrandchild, result.Value.ParentApprenticeId);

    }

    private static ArcanumSettings EnabledSettings() =>
        new()
        {
            Features = new FeatureSettings { Conclave = true },
        };

    private static IOptionsMonitor<ArcanumSettings> Monitor(ArcanumSettings settings) =>
        new StaticOptionsMonitor<ArcanumSettings>(settings);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {

        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;

    }

    private sealed class FakeApprenticeRepository : IApprenticeRepository
    {

        public List<Apprentice> Items { get; } = [];

        public Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(a => a.Id == id));

        public Task<ListPageResult<Apprentice>> ListAsync(
            Guid? campaignId,
            string? status,
            int? limit = null,
            DateTimeOffset? beforeUpdatedAt = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Apprentice>([.. Items], false));

        public Task<Apprentice> AddAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
        {

            Items.Add(apprentice);

            return Task.FromResult(apprentice);

        }

        public Task<Apprentice> UpdateAsync(Apprentice apprentice, CancellationToken cancellationToken = default) =>
            Task.FromResult(apprentice);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.RemoveAll(a => a.Id == id) > 0);

        public Task<IReadOnlyList<Apprentice>> GetResumableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<Apprentice>)[]);

        public Task<IReadOnlyList<Apprentice>> GetInterruptedPlanningAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<Apprentice>)[]);

    }

}

