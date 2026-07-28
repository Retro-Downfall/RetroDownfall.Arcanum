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

    [Fact]
    public async Task CountDescendantsOfRootAsync_CountsDescendantsAcrossPages()
    {

        Guid root = Guid.NewGuid();

        Guid childA = Guid.NewGuid();

        Guid childB = Guid.NewGuid();

        Guid grandchild = Guid.NewGuid();

        // Page size 1 forces the loader to paginate; the old single-page (HasMore-ignoring) count
        // would have missed every descendant past the first page.
        PagingLineageRepository repo = new(pageSize: 1);

        DateTimeOffset baseTime = DateTimeOffset.UtcNow;

        repo.Items.Add(MakeApprenticeAt(root, null, baseTime));

        repo.Items.Add(MakeApprenticeAt(childA, root, baseTime.AddSeconds(-1)));

        repo.Items.Add(MakeApprenticeAt(childB, root, baseTime.AddSeconds(-2)));

        repo.Items.Add(MakeApprenticeAt(grandchild, childA, baseTime.AddSeconds(-3)));

        int descendants = await ConclaveLineage.CountDescendantsOfRootAsync(repo, root, maxDescendants: 100);

        Assert.Equal(3, descendants);

        Assert.True(repo.ListCallCount > 1, "Expected the loader to paginate across more than one page.");

    }

    [Fact]
    public async Task FindRootAsync_CycleInParentChain_Terminates()
    {

        Guid a = Guid.NewGuid();

        Guid b = Guid.NewGuid();

        LineageRepository repo = new();

        repo.Items.Add(MakeApprentice(a, b));

        repo.Items.Add(MakeApprentice(b, a));

        Guid root = await ConclaveLineage.FindRootAsync(repo, a);

        Assert.True(root == a || root == b);

    }

    private static Apprentice MakeApprentice(Guid id, Guid? parentId) =>
        MakeApprenticeAt(id, parentId, DateTimeOffset.UtcNow);

    private static Apprentice MakeApprenticeAt(Guid id, Guid? parentId, DateTimeOffset updatedAt) =>
        new()
        {
            Id = id,
            Name = id.ToString("N")[..8],
            Goal = "Test",
            Status = ApprenticeStatus.Idle.ToString(),
            WorkspacePath = "/tmp/ws",
            ParentApprenticeId = parentId,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
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

    private sealed class PagingLineageRepository(int pageSize) : IApprenticeRepository
    {

        public List<Apprentice> Items { get; } = [];

        public int ListCallCount { get; private set; }

        public Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(a => a.Id == id));

        public Task<ListPageResult<Apprentice>> ListAsync(
            Guid? campaignId,
            string? status,
            int? limit = null,
            DateTimeOffset? beforeUpdatedAt = null,
            CancellationToken cancellationToken = default)
        {

            ListCallCount++;

            List<Apprentice> ordered = Items.OrderByDescending(a => a.UpdatedAt).ToList();

            List<Apprentice> remaining = beforeUpdatedAt is { } cursor
                ? ordered.Where(a => a.UpdatedAt < cursor).ToList()
                : ordered;

            List<Apprentice> pageItems = remaining.Take(pageSize).ToList();

            bool hasMore = remaining.Count > pageItems.Count;

            DateTimeOffset? next = pageItems.Count > 0 ? pageItems[^1].UpdatedAt : null;

            return Task.FromResult(new ListPageResult<Apprentice>([.. pageItems], hasMore, NextBeforeUpdatedAt: next));

        }

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
