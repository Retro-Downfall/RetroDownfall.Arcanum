using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [Fact]
    public async Task Retired_Guild_registration_preserves_explicit_durable_ownership_against_ordinary_writers()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        string path = Path.Combine(f.Root, "owned.txt");

        File.WriteAllText(path, "original");

        var acquired = await f.AcquireAsync("owned.txt");

        Assert.True(acquired.IsSuccess, acquired.Error.Message);

        GuildWorkspaceMutationFence fence = new(f.Broker);

        using IDisposable registration = WorkspaceMutationFence.Install(fence);

        registration.Dispose();

        var written = await WriteAsync(f, "owned.txt");

        Assert.True(written.IsFailure);

        Assert.Equal("original", File.ReadAllText(path));
    }
}
