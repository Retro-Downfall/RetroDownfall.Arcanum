using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class InMemoryWorkspaceRegistryTests : IAsyncLifetime
{

    private string _root = string.Empty;

    public async Task InitializeAsync()
    {

        _root = Path.Combine(Path.GetTempPath(), "arcanum-ws-registry", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        await Task.CompletedTask;

    }

    public Task DisposeAsync()
    {

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;

    }

    [Fact]
    public async Task RegisterAsync_rejects_duplicate_name_and_path()
    {

        ArcanumSettings settings = new()
        {
            Campaigns = new CampaignsSettings { AllowedRoots = [_root] },
        };

        InMemoryWorkspaceRegistry registry = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        string child = Path.Combine(_root, "child");

        Directory.CreateDirectory(child);

        CreateWorkspaceRequest request = new("Alpha", child, WorkspaceType.Campaign);

        var first = await registry.RegisterAsync(request, CancellationToken.None);

        Assert.True(first.IsSuccess);

        string otherDir = Path.Combine(_root, "other");

        Directory.CreateDirectory(otherDir);

        var duplicateName = await registry.RegisterAsync(request with { Path = otherDir }, CancellationToken.None);

        Assert.True(duplicateName.IsFailure);

        Assert.Equal("Workspace.NameDuplicate", duplicateName.Error.Code);

        Directory.CreateDirectory(Path.Combine(_root, "other"));

        var duplicatePath = await registry.RegisterAsync(request with { Name = "Beta", Path = child }, CancellationToken.None);

        Assert.True(duplicatePath.IsFailure);

        Assert.Equal("Workspace.PathDuplicate", duplicatePath.Error.Code);

    }

    [Fact]
    public async Task GetAllAsync_returns_workspaces_sorted_by_name()
    {

        ArcanumSettings settings = new()
        {
            Campaigns = new CampaignsSettings { AllowedRoots = [_root] },
        };

        InMemoryWorkspaceRegistry registry = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        string zDir = Path.Combine(_root, "z");

        string aDir = Path.Combine(_root, "a");

        Directory.CreateDirectory(zDir);

        Directory.CreateDirectory(aDir);

        await registry.RegisterAsync(new CreateWorkspaceRequest("Zulu", zDir, WorkspaceType.Campaign), CancellationToken.None);

        await registry.RegisterAsync(new CreateWorkspaceRequest("Alpha", aDir, WorkspaceType.Campaign), CancellationToken.None);

        WorkspaceInfo[] all = await registry.GetAllAsync(CancellationToken.None);

        Assert.Equal(["Alpha", "Zulu"], all.Select(w => w.Name).ToArray());

    }

}
