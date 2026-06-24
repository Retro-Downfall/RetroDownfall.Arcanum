using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.TheForge;

public sealed class ConclaveLineageTests
{

    [Fact]
    public async Task ValidateCastLimitsAsync_RejectsDepthExceeded()
    {

        Guid root = Guid.NewGuid();

        Guid child = Guid.NewGuid();

        Guid grandchild = Guid.NewGuid();

        LineageRepository repo = new();

        repo.Items.Add(MakeApprentice(root, null));

        repo.Items.Add(MakeApprentice(child, root));

        repo.Items.Add(MakeApprentice(grandchild, child));

        Result result = await ConclaveLineage.ValidateCastLimitsAsync(
            repo,
            grandchild,
            maxDelegationDepth: 2,
            maxDescendantsPerRoot: 10);

        Assert.True(result.IsFailure);

        Assert.Equal("Apprentice.ConclaveDepthExceeded", result.Error.Code);

    }

    [Fact]
    public async Task ValidateCastLimitsAsync_RejectsBreadthExceeded()
    {

        Guid root = Guid.NewGuid();

        Guid childA = Guid.NewGuid();

        Guid childB = Guid.NewGuid();

        LineageRepository repo = new();

        repo.Items.Add(MakeApprentice(root, null));

        repo.Items.Add(MakeApprentice(childA, root));

        repo.Items.Add(MakeApprentice(childB, root));

        Result result = await ConclaveLineage.ValidateCastLimitsAsync(
            repo,
            childA,
            maxDelegationDepth: 5,
            maxDescendantsPerRoot: 2);

        Assert.True(result.IsFailure);

        Assert.Equal("Apprentice.ConclaveBreadthExceeded", result.Error.Code);

    }

    [Fact]
    public async Task ValidateCastLimitsAsync_AllowsWithinLimits()
    {

        Guid root = Guid.NewGuid();

        LineageRepository repo = new();

        repo.Items.Add(MakeApprentice(root, null));

        Result result = await ConclaveLineage.ValidateCastLimitsAsync(
            repo,
            root,
            maxDelegationDepth: 3,
            maxDescendantsPerRoot: 4);

        Assert.True(result.IsSuccess);

    }

    private static Apprentice MakeApprentice(Guid id, Guid? parentId) =>
        new()
        {
            Id = id,
            Name = id.ToString("N")[..8],
            Goal = "Test",
            Status = ApprenticeStatus.Idle.ToString(),
            WorkspacePath = "/tmp/ws",
            ParentApprenticeId = parentId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private sealed class LineageRepository : IApprenticeRepository
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
