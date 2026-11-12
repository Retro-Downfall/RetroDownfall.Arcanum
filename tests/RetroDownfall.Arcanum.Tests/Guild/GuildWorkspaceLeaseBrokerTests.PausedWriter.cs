using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [Fact]
    public async Task Expired_lease_cannot_be_reassigned_while_an_admitted_physical_writer_is_paused()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        string path = Path.Combine(f.Root, "a.txt");

        await File.WriteAllTextAsync(path, "old");

        var held = await f.AcquireAsync("a.txt");

        Assert.True(held.IsSuccess, held.Error.Message);

        GuildWorkspaceMutationFence fence = new(f.Broker);

        using IDisposable registration = WorkspaceMutationFence.Install(fence);

        using IDisposable invocation = fence.EnterInvocation(f.Policy.Authority, f.Policy.WorkspaceId);

        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SandboxedFileIo.AfterCreateParentDirectoryForTests = _ =>
        {
            entered.TrySetResult();

            release.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
        };

        Task<(bool Success, McpToolsCallResultWire? Error)>? writer = null;

        try
        {
            writer = Task.Run(() => SandboxedFileIo.TryWriteAllTextAtomicallyAsync(f.Root, path, "new", deadline.Token));

            await entered.Task.WaitAsync(deadline.Token);

            f.Time.Advance(TimeSpan.FromMinutes(16));

            Assert.Equal(ErrorCodes.Guild.LeaseConflict, (await f.AcquireAsync("a.txt")).Error.Code);

            Assert.Equal("old", await File.ReadAllTextAsync(path, deadline.Token));

            release.TrySetResult();

            var completed = await writer.WaitAsync(deadline.Token);

            Assert.False(completed.Success);

            Assert.NotNull(completed.Error);

            Assert.Contains(ErrorCodes.Guild.LeaseLapsed, completed.Error.Content[0].Text);

            Assert.Equal("old", await File.ReadAllTextAsync(path, deadline.Token));

            var acquiredAfterDrain = await f.AcquireAsync("a.txt");

            Assert.True(acquiredAfterDrain.IsSuccess, acquiredAfterDrain.Error.Message);

            Assert.NotEqual(held.Value.Id, acquiredAfterDrain.Value.Id);
        }
        finally
        {
            release.TrySetResult();

            SandboxedFileIo.AfterCreateParentDirectoryForTests = null;

            if (writer is not null) await writer;
        }
    }
}
