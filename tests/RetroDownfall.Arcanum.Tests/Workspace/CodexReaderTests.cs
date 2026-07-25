using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class CodexReaderTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _workspace.WriteFile("CODEX.md", "Local codex body.");

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task ReadCodexFileAsync_returns_content_under_size_limit()
    {

        string path = _workspace.WriteFile("docs/CODEX.md", "Standalone codex.");

        string? content = await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 4096, CancellationToken.None);

        Assert.Equal("Standalone codex.", content);

    }

    [Fact]
    public async Task ReadCodexFileAsync_returns_null_when_file_exceeds_limit()
    {

        string path = _workspace.WriteFile("huge/CODEX.md", new string('x', 5000));

        string? content = await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 1024, CancellationToken.None);

        Assert.Null(content);

    }

    [Fact]
    public async Task ReadCodexAsync_returns_local_workspace_codex()
    {

        string? content = await CodexReader.ReadCodexAsync(_workspace.Root, maxSizeBytes: 4096, CancellationToken.None);

        Assert.Equal("Local codex body.", content);

    }

    [Fact]
    public async Task ReadCodexFileAsync_uses_cache_for_unchanged_file()
    {

        string path = _workspace.WriteFile("cached/CODEX.md", "cached text");

        DateTime initialLastWriteTimeUtc = File.GetLastWriteTimeUtc(path);

        string? first = await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 4096, CancellationToken.None);

        File.WriteAllText(path, "mutated text");

        File.SetLastWriteTimeUtc(path, initialLastWriteTimeUtc.AddSeconds(1));

        string? second = await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 4096, CancellationToken.None);

        Assert.Equal("cached text", first);

        Assert.Equal("mutated text", second);

    }

}
