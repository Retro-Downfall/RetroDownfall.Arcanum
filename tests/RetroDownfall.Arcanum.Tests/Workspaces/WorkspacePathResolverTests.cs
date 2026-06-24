using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class WorkspacePathResolverTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

    }

    [Fact]
    public void ResolveRelativePath_returns_workspace_root_for_null_path()
    {

        WorkspaceInfo workspace = MakeWorkspace(_workspace.Root);

        var result = WorkspacePathResolver.ResolveRelativePath(workspace, null);

        Assert.True(result.IsSuccess);

        Assert.Equal(Path.GetFullPath(_workspace.Root), result.Value);

    }

    [Fact]
    public void ResolveRelativePath_resolves_child_file()
    {

        string file = _workspace.WriteFile("notes/readme.txt", "hello");

        WorkspaceInfo workspace = MakeWorkspace(_workspace.Root);

        var result = WorkspacePathResolver.ResolveRelativePath(workspace, "notes/readme.txt");

        Assert.True(result.IsSuccess);

        Assert.Equal(Path.GetFullPath(file), result.Value);

    }

    [Fact]
    public void ResolveRelativePath_rejects_parent_traversal()
    {

        WorkspaceInfo workspace = MakeWorkspace(_workspace.Root);

        var result = WorkspacePathResolver.ResolveRelativePath(workspace, "../outside");

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathTraversal", result.Error.Code);

    }

    [Fact]
    public void ResolveRelativePath_rejects_absolute_paths()
    {

        WorkspaceInfo workspace = MakeWorkspace(_workspace.Root);

        var result = WorkspacePathResolver.ResolveRelativePath(workspace, _workspace.Root);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathTraversal", result.Error.Code);

    }

    private static WorkspaceInfo MakeWorkspace(string path) =>
        new("id", "test", path, WorkspaceType.Campaign, DateTimeOffset.UtcNow);

}
