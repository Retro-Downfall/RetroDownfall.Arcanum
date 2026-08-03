using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

[Collection("Grimoire")]
public sealed class CampaignBackedWorkspaceRegistryTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _workspaceRoot = string.Empty;

    private ArcanumDbContext? _db;

    public CampaignBackedWorkspaceRegistryTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _workspaceRoot = Path.Combine(Path.GetTempPath(), "arcanum-campaign-registry", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_workspaceRoot);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

        if (Directory.Exists(_workspaceRoot))
        {

            Directory.Delete(_workspaceRoot, recursive: true);

        }

    }

    [SkippableFact]
    public async Task RegisterAsync_GetAsync_and_GetAllAsync_use_grimoire_campaigns()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ArcanumSettings settings = new()
        {
            Security = new SecuritySettings { CampaignRoots = [_workspaceRoot] },
        };

        GrimoireDbReadiness readiness = new();

        readiness.MarkReady();

        CampaignBackedWorkspaceRegistry registry = new(
            new CampaignRepositoryScopeFactory(_db!, settings),
            readiness,
            new TestOptionsMonitor<ArcanumSettings>(settings));

        string alphaDir = Path.Combine(_workspaceRoot, "alpha");

        string betaDir = Path.Combine(_workspaceRoot, "beta");

        Directory.CreateDirectory(alphaDir);

        Directory.CreateDirectory(betaDir);

        Result<WorkspaceInfo> alpha = await registry.RegisterAsync(
            new CreateWorkspaceRequest("Alpha", alphaDir, WorkspaceType.Campaign),
            CancellationToken.None);

        Result<WorkspaceInfo> beta = await registry.RegisterAsync(
            new CreateWorkspaceRequest("Beta", betaDir, WorkspaceType.Campaign),
            CancellationToken.None);

        Assert.True(alpha.IsSuccess);

        Assert.True(beta.IsSuccess);

        WorkspaceInfo? loaded = await registry.GetAsync(alpha.Value!.Id, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal("Alpha", loaded!.Name);

        WorkspaceInfo[] all = await registry.GetAllAsync(CancellationToken.None);

        Assert.Equal(["Alpha", "Beta"], all.Select(w => w.Name).ToArray());

    }

    [SkippableFact]
    public async Task UnregisterAsync_removes_campaign_from_grimoire()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ArcanumSettings settings = new()
        {
            Security = new SecuritySettings { CampaignRoots = [_workspaceRoot] },
        };

        GrimoireDbReadiness readiness = new();

        readiness.MarkReady();

        CampaignBackedWorkspaceRegistry registry = new(
            new CampaignRepositoryScopeFactory(_db!, settings),
            readiness,
            new TestOptionsMonitor<ArcanumSettings>(settings));

        string workspaceDir = Path.Combine(_workspaceRoot, "remove-me");

        Directory.CreateDirectory(workspaceDir);

        Result<WorkspaceInfo> registered = await registry.RegisterAsync(
            new CreateWorkspaceRequest("Remove Me", workspaceDir, WorkspaceType.Campaign),
            CancellationToken.None);

        Assert.True(registered.IsSuccess);

        Result<bool> removed = await registry.UnregisterAsync(registered.Value!.Id, CancellationToken.None);

        Assert.True(removed.IsSuccess);

        Assert.Null(await registry.GetAsync(registered.Value.Id, CancellationToken.None));

    }

    [Fact]
    public async Task RegisterAsync_preserves_repository_failure()
    {

        ArcanumSettings settings = new()
        {
            Security = new SecuritySettings { CampaignRoots = [_workspaceRoot] },
        };

        GrimoireDbReadiness readiness = new();

        readiness.MarkReady();

        CampaignBackedWorkspaceRegistry registry = new(
            new FixedCampaignRepositoryScopeFactory(new MaxReachedCampaignRepository()),
            readiness,
            new TestOptionsMonitor<ArcanumSettings>(settings));

        string workspaceDir = Path.Combine(_workspaceRoot, "max-reached");

        Directory.CreateDirectory(workspaceDir);

        Result<WorkspaceInfo> result = await registry.RegisterAsync(
            new CreateWorkspaceRequest("Rejected", workspaceDir, WorkspaceType.Campaign),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Campaign.MaxReached, result.Error.Code);

        Assert.Equal(
            "Repository failure selected by code, not legacy exception text.",
            result.Error.Message);

    }

    [Fact]
    public async Task GetAllAsync_reads_every_campaign_page()
    {

        ArcanumSettings settings = new();

        GrimoireDbReadiness readiness = new();

        readiness.MarkReady();

        PagedCampaignRepository repository = new(
        [
            CreateCampaign("Alpha"),
            CreateCampaign("Beta"),
            CreateCampaign("Gamma"),
            CreateCampaign("Delta"),
            CreateCampaign("Epsilon"),
        ],
        pageSize: 2);

        CampaignBackedWorkspaceRegistry registry = new(
            new FixedCampaignRepositoryScopeFactory(repository),
            readiness,
            new TestOptionsMonitor<ArcanumSettings>(settings));

        WorkspaceInfo[] all = await registry.GetAllAsync(CancellationToken.None);

        Assert.Equal(
            ["Alpha", "Beta", "Gamma", "Delta", "Epsilon"],
            all.Select(workspace => workspace.Name).ToArray());

        Assert.Equal([0, 2, 4], repository.RequestedOffsets);

    }

    private static Campaign CreateCampaign(string name) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = Path.Combine(Path.GetTempPath(), name),
            Type = WorkspaceType.Campaign,
        };

    private sealed class MaxReachedCampaignRepository : ICampaignRepository
    {

        public Task<Campaign?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByPathAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByNameAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(
            Campaign campaign,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<Campaign>.Failure(
                    new Error(
                        ErrorCodes.Campaign.MaxReached,
                        "Repository failure selected by code, not legacy exception text.")));

        public Task<Campaign> UpdateAsync(
            Campaign campaign,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<bool> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

    }

    private sealed class PagedCampaignRepository(
        Campaign[] campaigns,
        int pageSize) : ICampaignRepository
    {

        public List<int> RequestedOffsets { get; } = [];

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default)
        {

            RequestedOffsets.Add(offset);

            Campaign[] page = campaigns.Skip(offset).Take(pageSize).ToArray();

            int nextOffset = offset + page.Length;

            bool hasMore = nextOffset < campaigns.Length;

            return Task.FromResult(
                new ListPageResult<Campaign>(
                    page,
                    hasMore,
                    hasMore ? nextOffset : null));

        }

        public Task<Campaign?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Campaign?> GetByPathAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Campaign?> GetByNameAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<Campaign>> AddAsync(
            Campaign campaign,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Campaign> UpdateAsync(
            Campaign campaign,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private sealed class FixedCampaignRepositoryScopeFactory(
        ICampaignRepository repository) : IServiceScopeFactory
    {

        public IServiceScope CreateScope() => new FixedCampaignRepositoryScope(repository);

    }

    private sealed class FixedCampaignRepositoryScope(
        ICampaignRepository repository) : IServiceScope
    {

        public IServiceProvider ServiceProvider { get; } =
            new FixedCampaignRepositoryProvider(repository);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

    private sealed class FixedCampaignRepositoryProvider(
        ICampaignRepository repository) : IServiceProvider
    {

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICampaignRepository)
                ? repository
                : null;

    }

    private sealed class CampaignRepositoryScopeFactory : IServiceScopeFactory
    {

        private readonly ArcanumDbContext _db;

        private readonly ArcanumSettings _settings;

        public CampaignRepositoryScopeFactory(ArcanumDbContext db, ArcanumSettings settings)
        {

            _db = db;

            _settings = settings;

        }

        public IServiceScope CreateScope() => new CampaignRepositoryScope(_db, _settings);

    }

    private sealed class CampaignRepositoryScope : IServiceScope
    {

        private readonly IServiceProvider _provider;

        public CampaignRepositoryScope(ArcanumDbContext db, ArcanumSettings settings)
        {

            ServiceCollection services = new();

            services.AddSingleton(db);

            services.AddSingleton<ICampaignRepository>(_ =>
                new CampaignRepository(
                    db,
                    NullLogger<CampaignRepository>.Instance,
                    new TestOptionsSnapshot<ArcanumSettings>(settings)));

            _provider = services.BuildServiceProvider();

        }

        public IServiceProvider ServiceProvider => _provider;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

}
