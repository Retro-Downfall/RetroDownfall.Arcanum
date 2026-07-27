using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence.Spells;

public sealed class SpellAtomicFileTests : IAsyncLifetime
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
    public async Task WriteAllTextAsync_throws_when_atomic_replace_aborts()
    {

        string destination = Path.Combine(_workspace.Root, "SPELL.md");

        string alias = Path.Combine(_workspace.Root, "SPELL.alias.md");

        await File.WriteAllTextAsync(destination, "original");

        Assert.True(HardLinkTestSupport.TryCreate(alias, destination));

        await Assert.ThrowsAsync<IOException>(
            () => SpellAtomicFile.WriteAllTextAsync(
                destination,
                "replacement",
                CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(destination));

        Assert.Equal("original", await File.ReadAllTextAsync(alias));

    }

}
