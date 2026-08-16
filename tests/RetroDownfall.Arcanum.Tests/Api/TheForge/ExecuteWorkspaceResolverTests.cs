using RetroDownfall.Arcanum.Api.Spells;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

public sealed class ExecuteWorkspaceResolverTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public async Task ResolveAsync_QueryWorkspace_TakesPrecedence()
    {
        string explicitDir = _workspace.CreateSubdir("explicit");

        SpellWorkspaceResolver resolver = CreateResolver();

        Result<string?> result = await ExecuteWorkspaceResolver.ResolveAsync(
            queryWorkspace: explicitDir,
            bodyWorkspace: null,
            campaignId: null,
            resolver,
            new FakeCampaignRepository(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(Path.GetFullPath(explicitDir), result.Value);
    }

    [Fact]
    public async Task ResolveAsync_CampaignId_UsesCampaignPath()
    {
        Guid campaignId = Guid.NewGuid();

        string campaignPath = _workspace.CreateSubdir("campaign");

        FakeCampaignRepository campaigns = new();

        campaigns.ById[campaignId] = new Campaign { Id = campaignId, Path = campaignPath };

        SpellWorkspaceResolver resolver = CreateResolver();

        Result<string?> result = await ExecuteWorkspaceResolver.ResolveAsync(
            queryWorkspace: null,
            bodyWorkspace: null,
            campaignId: campaignId,
            resolver,
            campaigns,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(campaignPath, result.Value);
    }

    [Fact]
    public async Task ResolveAsync_UnknownCampaign_Fails()
    {
        SpellWorkspaceResolver resolver = CreateResolver();

        Result<string?> result = await ExecuteWorkspaceResolver.ResolveAsync(
            null,
            null,
            Guid.NewGuid(),
            resolver,
            new FakeCampaignRepository(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Campaign.NotFound", result.Error.Code);
    }

    private SpellWorkspaceResolver CreateResolver()
    {
        return new SpellWorkspaceResolver(
            new FakeHostWorkspaceContext(null),
            Microsoft.Extensions.Options.Options.Create(new Core.Configuration.ArcanumSettings
            {
                Security = new Core.Configuration.SecuritySettings
                {
                    SpellWorkspaceRoots = [_workspace.Root],
                },
            }));
    }

    private sealed class FakeHostWorkspaceContext(string? path) : Core.Hosting.IHostWorkspaceContext
    {

        public string? WorkspacePath => path;

    }

    private sealed class FakeCampaignRepository : ICampaignRepository
    {

        public Dictionary<Guid, Campaign> ById { get; } = new();

        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(ById.TryGetValue(id, out Campaign? c) ? c : null);

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Core.Primitives.ListPageResult<Campaign>> ListAsync(
            Core.Workspaces.WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Core.Primitives.ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

    }

}

public sealed class CampaignWorkspaceFillTests
{

    [Fact]
    public async Task ApplyAsync_NoCampaignId_ReturnsOriginal()
    {
        PingRequest request = new(Prompt: "hi");

        Result<PingRequest> result = await CampaignWorkspaceFill.ApplyAsync(
            request,
            new FakeCampaignRepository(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Same(request, result.Value);
    }

    [Fact]
    public async Task ApplyAsync_WithWorkingDirectory_SkipsLookup()
    {
        PingRequest request = new(Prompt: "hi", WorkingDirectory: "/already/set", CampaignId: Guid.NewGuid());

        Result<PingRequest> result = await CampaignWorkspaceFill.ApplyAsync(
            request,
            new FakeCampaignRepository(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("/already/set", result.Value!.WorkingDirectory);
    }

    [Fact]
    public async Task ApplyAsync_HydratesWorkingDirectoryFromCampaign()
    {
        Guid campaignId = Guid.NewGuid();

        string path = "/campaign/path";

        FakeCampaignRepository repo = new();

        repo.ById[campaignId] = new Campaign { Id = campaignId, Path = path };

        PingRequest request = new(Prompt: "hi", CampaignId: campaignId);

        Result<PingRequest> result = await CampaignWorkspaceFill.ApplyAsync(
            request,
            repo,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(path, result.Value!.WorkingDirectory);
    }

    [Fact]
    public async Task ApplyAsync_MissingCampaign_Fails()
    {
        PingRequest request = new(Prompt: "hi", CampaignId: Guid.NewGuid());

        Result<PingRequest> result = await CampaignWorkspaceFill.ApplyAsync(
            request,
            new FakeCampaignRepository(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Campaign.NotFound", result.Error.Code);
    }

    private sealed class FakeCampaignRepository : ICampaignRepository
    {

        public Dictionary<Guid, Campaign> ById { get; } = new();

        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(ById.TryGetValue(id, out Campaign? c) ? c : null);

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            Core.Workspaces.WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

    }

}
