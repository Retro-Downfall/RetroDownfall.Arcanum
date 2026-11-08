using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [Fact]
    public async Task Authenticated_recursive_delete_adopts_existing_descendant_directories_and_releases_scope()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        Directory.CreateDirectory(Path.Combine(f.Root, "tree", "child", "nested"));

        File.WriteAllText(Path.Combine(f.Root, "tree", "child", "nested", "file.txt"), "owned");

        GuildWorkspaceMutationFence fence = new(f.Broker);

        using IDisposable registration = WorkspaceMutationFence.Install(fence);

        using IDisposable invocation = fence.EnterInvocation(f.Policy.Authority, f.Policy.WorkspaceId);

        ArcanumSettings settings = new();

        settings.Workspaces.EnableFileWrite = true;

        PhysicalFileSystemWriter writer = new(new TestOptionsSnapshot<ArcanumSettings>(settings));

        WorkspaceInfo workspace = new("test", "test", f.Root, WorkspaceType.Custom, DateTimeOffset.UtcNow);

        var result = await writer.DeleteAsync(workspace, "tree", recursive: true, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(Directory.Exists(Path.Combine(f.Root, "tree")));

        var acquired = await f.AcquireAsync("tree/next.txt");

        Assert.True(acquired.IsSuccess, acquired.Error.Message);
    }
}
