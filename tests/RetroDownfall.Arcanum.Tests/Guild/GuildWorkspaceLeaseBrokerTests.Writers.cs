using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [Fact]
    public async Task Installed_broker_refuses_an_ordinary_writer_without_authenticated_context()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        File.WriteAllText(Path.Combine(f.Root, "a.txt"), "old");

        GuildWorkspaceMutationFence fence = new(f.Broker);

        using IDisposable registration = WorkspaceMutationFence.Install(fence);

        var result = await WriteAsync(f, "a.txt");

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guild.Forbidden, result.Error.Code);

        Assert.Equal("old", File.ReadAllText(Path.Combine(f.Root, "a.txt")));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("missing/nested/a.txt")]
    public async Task Authenticated_physical_writer_uses_real_broker_through_parent_creation_and_cleanup(string path)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        GuildWorkspaceMutationFence fence = new(f.Broker);

        using IDisposable registration = WorkspaceMutationFence.Install(fence);

        using IDisposable invocation = fence.EnterInvocation(f.Policy.Authority, f.Policy.WorkspaceId);

        var result = await WriteAsync(f, path);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("new", File.ReadAllText(Path.Combine(f.Root, path)));

        Assert.Single(Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories));

        Assert.True((await f.AcquireAsync(path)).IsSuccess);
    }

    [Fact]
    public async Task Ended_invocation_stops_forward_steps_but_allows_pinned_rollback_and_retains_uncertainty()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        GuildWorkspaceMutationFence fence = new(f.Broker);

        using IDisposable registration = WorkspaceMutationFence.Install(fence);

        IDisposable invocation = fence.EnterInvocation(f.Policy.Authority, f.Policy.WorkspaceId);

        var admitted = await fence.AdmitAsync(f.Root, [new("a.txt", GuildPathScopeKind.File)], default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        IWorkspaceMutationLease lease = admitted.Value;

        Assert.True((await lease.ValidateAsync(false, default)).IsSuccess);

        invocation.Dispose();

        Assert.True((await lease.ValidateAsync(false, default)).IsFailure);

        Assert.True((await lease.ValidateAsync(true, default)).IsSuccess);

        Assert.True((await lease.RetainRecoveryAsync("rollback_incomplete", default)).IsSuccess);

        await lease.DisposeAsync();

        Assert.Equal(ErrorCodes.Guild.LeaseConflict, (await f.AcquireAsync("a.txt")).Error.Code);
    }

    [Fact]
    public async Task Writer_context_cannot_substitute_a_different_workspace_root()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        GuildWorkspaceMutationFence fence = new(f.Broker);

        using IDisposable invocation = fence.EnterInvocation(f.Policy.Authority, f.Policy.WorkspaceId);

        var admitted = await fence.AdmitAsync(Path.GetTempPath(), [new("outside.txt", GuildPathScopeKind.File)], default);

        Assert.True(admitted.IsFailure);
    }

    [Fact]
    public async Task Completed_directory_effect_can_be_observed_after_its_invocation_ends()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        GuildWorkspaceMutationFence fence = new(f.Broker);

        using IDisposable registration = WorkspaceMutationFence.Install(fence);

        using IDisposable invocation = fence.EnterInvocation(f.Policy.Authority, f.Policy.WorkspaceId);

        var admitted = await fence.AdmitAsync(f.Root, [new("created", GuildPathScopeKind.Subtree)], default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        await using IWorkspaceMutationLease lease = admitted.Value;

        var prepared = WorkspaceLeaseDirectoryCreation.Prepare(Path.Combine(f.Root, "created"));

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        using WorkspaceLeaseDirectoryCreation preparation = prepared.Value;

        Assert.True((await lease.ValidateDirectoryCreationAsync(preparation, false, default)).IsSuccess);

        var created = preparation.CreateExclusive();

        Assert.True(created.IsSuccess, created.Error.Message);

        using WorkspaceLeaseCreatedDirectory proof = created.Value;

        invocation.Dispose();

        Assert.True((await lease.AdoptCreatedDirectoryAsync(proof, false, default)).IsSuccess);

        Assert.True((await lease.ValidateAsync(false, default)).IsFailure);

        Assert.True((await lease.ValidateAsync(true, default)).IsSuccess);
    }

    private static Task<Result<FileWriteResult>> WriteAsync(Fixture f, string path)
    {
        ArcanumSettings settings = new();

        settings.Workspaces.EnableFileWrite = true;

        PhysicalFileSystemWriter writer = new(new TestOptionsSnapshot<ArcanumSettings>(settings));

        WorkspaceInfo workspace = new("test", "test", f.Root, WorkspaceType.Custom, DateTimeOffset.UtcNow);

        return writer.WriteFileAsync(workspace, path, "new", default);
    }
}
